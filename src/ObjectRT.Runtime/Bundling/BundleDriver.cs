using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ObjectRT.Abstractions;
using ObjectRT.Reader;

namespace ObjectRT.Runtime;

/// <summary>
/// Options for <see cref="BundleDriver.Bundle"/>. Describes how to build a
/// standalone executable around a compiled <c>.orbt</c> module.
/// </summary>
public sealed class BundleSpec
{
    /// <summary>The runtime class the bundle instantiates and runs the module
    /// on. Must be public, parameterless-constructible, and implement
    /// <see cref="IHostedRuntime"/>. Its assembly must be resolvable at bundle
    /// time — either a dependency of the CLI or passed via
    /// <see cref="BindingAssemblyPaths"/>.</summary>
    public required Type HostType { get; init; }

    /// <summary>Binding assemblies to copy next to the bundle and register on
    /// the host at startup (loaded by file name from the app base directory).
    /// Also added as compile references so the host template can name types
    /// from them.</summary>
    public IReadOnlyList<string> BindingAssemblyPaths { get; init; } = Array.Empty<string>();

    /// <summary>Optional C# statements inserted into the host's Main after the
    /// runtime is constructed (before the module runs). Use for host
    /// configuration the runtime doesn't expose through its constructor, e.g.
    /// <c>rt.Mode = ObjectRT.Runtime.JitMode.Reflection;</c>.</summary>
    public string? HostInit { get; init; }

    /// <summary>Runtime identifiers for self-contained publish, e.g.
    /// <c>["win-x64"]</c>. Empty = framework-dependent bundle (needs .NET
    /// installed).</summary>
    public IReadOnlyList<string> Rids { get; init; } = Array.Empty<string>();

    /// <summary>When true and <see cref="Rids"/> is non-empty, publish as a
    /// single-file executable (<c>PublishSingleFile</c>).</summary>
    public bool SingleFile { get; init; }

    /// <summary>Target framework written into the runtimeconfig / csproj.
    /// Defaults to net10.0 (matches the runtime libraries).</summary>
    public string TargetFramework { get; init; } = "net10.0";
}

/// <summary>
/// Produces standalone executables from compiled <c>.orbt</c> modules using
/// any <see cref="IHostedRuntime"/>. The module is embedded as a manifest
/// resource in a generated C# host, which is compiled in-memory with Roslyn
/// and wrapped in a native apphost (or published self-contained per-RID).
///
/// This is the reusable, generic form of the technique that powers both
/// <c>objectrt -b</c> (host: <see cref="Runtime"/>) and <c>ccl bundle</c>
/// (host: the Contract runtime). The host and any binding assemblies are
/// supplied by the caller, so any runtime that implements
/// <see cref="IHostedRuntime"/> can be bundled without changing this driver.
/// </summary>
public static class BundleDriver
{
    /// <summary>
    /// Returns the module names a program depends on, resolved from its module
    /// metadata: the format's import table plus every <c>Call</c>/<c>Callvirt</c>/
    /// <c>NativeCall</c> method reference ("Module.Method"), minus the types the
    /// module itself defines. The leftover prefixes are external bindings —
    /// e.g. "Ui" and "IO" for a program that uses a native UI facade and the
    /// standard library. Callers diff this against their known built-in
    /// bindings to catch "forgot to pass the binding assembly" at bundle time
    /// instead of at runtime.
    /// </summary>
    public static IReadOnlyList<string> RequiredBindingModules(ORBTModule module)
    {
        var defined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in module.Types)
        {
            var name = module.Resolve(type.NameIndex);
            if (!string.IsNullOrEmpty(name)) defined.Add(name);
        }

        var prefixes = new HashSet<string>(StringComparer.Ordinal);

        // The format's import table (some producers populate it).
        foreach (var imp in module.Imports)
        {
            var name = module.Resolve(imp.ModuleIndex);
            if (!string.IsNullOrEmpty(name)) prefixes.Add(name);
        }

        // Call/Callvirt/NativeCall operands reference "Module.Method" strings.
        foreach (var type in module.Types)
        {
            foreach (var method in type.Methods)
            {
                foreach (var instr in method.Instructions)
                {
                    if (instr.Operand is OperandNativeCall call)
                    {
                        var fullName = module.Resolve(call.StringIndex);
                        var dot = fullName.LastIndexOf('.');
                        if (dot > 0)
                        {
                            var prefix = fullName[..dot];
                            if (!string.IsNullOrEmpty(prefix)) prefixes.Add(prefix);
                        }
                    }
                }
            }
        }

        // A module is only an external binding if the module doesn't define it.
        prefixes.ExceptWith(defined);
        return prefixes.OrderBy(name => name, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Bundles <paramref name="orbtBytes"/> into a standalone executable.
    /// Returns 0 on success, non-zero with a message on stderr otherwise.
    /// </summary>
    public static int Bundle(
        string exeName,
        string sourceFile,
        byte[] orbtBytes,
        string? outDir,
        BundleSpec spec,
        bool verbose)
    {
        var name = Path.GetFileNameWithoutExtension(sourceFile);
        outDir ??= Path.Combine(Directory.GetCurrentDirectory(), name);
        var safeName = SafeName(name);

        var hostAsmFile = Path.GetFileName(spec.HostType.Assembly.Location);
        var bindFiles = spec.BindingAssemblyPaths
            .Select(Path.GetFileName)
            .Where(f => !string.IsNullOrEmpty(f))
            .Cast<string>()
            .ToList();

        // ── Runtime DLLs come from the CLI's own directory ───────────────
        var baseDir = AppContext.BaseDirectory;
        var required = new[]
        {
            "ObjectRT.Runtime.dll", "ObjectRT.VM.dll",
            "ObjectRT.Reader.dll", "ObjectRT.Abstractions.dll",
            "Microsoft.CodeAnalysis.dll", "Microsoft.CodeAnalysis.CSharp.dll",
        };
        var missing = required
            .Where(d => !File.Exists(Path.Combine(baseDir, d)))
            .ToArray();
        if (missing.Length > 0)
        {
            Error($"Cannot find runtime assemblies in '{baseDir}': {string.Join(", ", missing)}");
            return 1;
        }

        // ── Host source (embedded module + runtime wiring) ───────────────
        var program = BuildHostSource(spec, hostAsmFile, bindFiles);

        var refs = required
            .Select(d => MetadataReference.CreateFromFile(Path.Combine(baseDir, d)))
            .ToList();

        // Every DLL in the CLI dir (ObjectRT + Roslyn + anything else).
        foreach (var dll in Directory.GetFiles(baseDir, "*.dll"))
        {
            var path = Path.Combine(baseDir, dll);
            if (!refs.Any(r => r.FilePath == path))
            {
                try { refs.Add(MetadataReference.CreateFromFile(path)); } catch { }
            }
        }

        // Binding assemblies (from their source paths).
        foreach (var path in spec.BindingAssemblyPaths)
        {
            if (File.Exists(path) && !refs.Any(r => r.FilePath == path))
            {
                try { refs.Add(MetadataReference.CreateFromFile(path)); } catch { }
            }
        }

        // The canonical shared-framework reference set.
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
        {
            foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!refs.Any(r => r.FilePath == path))
                {
                    try { refs.Add(MetadataReference.CreateFromFile(path)); } catch { }
                }
            }
        }

        var tree = CSharpSyntaxTree.ParseText(program, new CSharpParseOptions(LanguageVersion.Latest));

        // Embed the .orbt as a manifest resource so the host can read it.
        var resource = new ResourceDescription(
            "module.orbt",
            () => new MemoryStream(orbtBytes),
            isPublic: true);

        var compilation = CSharpCompilation.Create(
            safeName,
            new[] { tree },
            refs,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, optimizationLevel: OptimizationLevel.Release));

        // ── Framework-dependent: in-memory compile + write ───────────────
        if (spec.Rids.Count == 0)
        {
            var outputDir = Path.Combine(outDir, name);
            Directory.CreateDirectory(outputDir);

            using var dllMs = new MemoryStream();
            var result = compilation.Emit(dllMs, manifestResources: new[] { resource });
            if (!result.Success)
            {
                var diags = string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
                Error($"Compilation failed:\n{diags}");
                return 1;
            }

            var dllPath = Path.Combine(outputDir, $"{safeName}.dll");
            File.WriteAllBytes(dllPath, dllMs.ToArray());

            // Copy binding assemblies next to the host first (their own
            // dependencies — e.g. a UI framework the binding wraps), then the
            // CLI's runtime DLLs on top so *our* (freshest) copies of the
            // ObjectRT/Contract/Roslyn assemblies always win over any stale
            // versions a binding directory happens to carry.
            foreach (var dll in BindingDependencyDlls(spec.BindingAssemblyPaths))
                File.Copy(dll, Path.Combine(outputDir, Path.GetFileName(dll)), overwrite: true);
            foreach (var dll in Directory.GetFiles(baseDir, "*.dll"))
                File.Copy(dll, Path.Combine(outputDir, Path.GetFileName(dll)), overwrite: true);

            // Native assets (runtimes/<rid>/native) can't be probed without a
            // deps.json — P/Invoke must find them next to the host. Copy the
            // current platform's natives to the output root.
            CopyBindingNativeAssets(spec.BindingAssemblyPaths, outputDir, rids: null);

            // runtimeconfig.json (framework-dependent). Write the *minimal*
            // version for the target framework ("net10.0" → "10.0.0") plus
            // rollForward LatestMajor: the host rolls up to the nearest
            // installed patch/minor, and forward to a future major if that's
            // all the user has. Never write the exact build's patch version —
            // that would refuse to run on any older patch.
            var tfmVersion = spec.TargetFramework.StartsWith("net", StringComparison.Ordinal)
                ? spec.TargetFramework["net".Length..]
                : spec.TargetFramework;
            var parts = tfmVersion.Split('.');
            var runtimeVersion = parts.Length >= 2
                ? $"{parts[0]}.{parts[1]}.0"
                : $"{tfmVersion}.0";
            File.WriteAllText(Path.Combine(outputDir, $"{safeName}.runtimeconfig.json"), $$"""
            {
              "runtimeOptions": {
                "tfm": "{{spec.TargetFramework}}",
                "rollForward": "LatestMajor",
                "framework": { "name": "Microsoft.NETCore.App", "version": "{{runtimeVersion}}" }
              }
            }
            """);

            // Native apphost (.exe) so it can be launched without `dotnet`.
            var exePath = Path.Combine(outputDir, $"{safeName}{(OperatingSystem.IsWindows() ? ".exe" : "")}");
            if (TryCreateAppHost(exePath, $"{safeName}.dll", verbose))
                Console.WriteLine(exePath);
            else
                Console.WriteLine(dllPath); // fall back to `dotnet {name}.dll`

            return 0;
        }

        // ── Self-contained publish (needs dotnet SDK + MSBuild) ──────────
        var workDir = Path.Combine(Path.GetTempPath(), $"objrt_bundle_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var libDir = Path.Combine(workDir, "lib");
        Directory.CreateDirectory(libDir);

        try
        {
            foreach (var dll in BindingDependencyDlls(spec.BindingAssemblyPaths))
                File.Copy(dll, Path.Combine(libDir, Path.GetFileName(dll)), overwrite: true);
            foreach (var dll in Directory.GetFiles(baseDir, "*.dll"))
                File.Copy(dll, Path.Combine(libDir, Path.GetFileName(dll)), overwrite: true);
            File.WriteAllBytes(Path.Combine(workDir, "module.orbt"), orbtBytes);

            // Native DLLs (libSkiaSharp etc.) are not managed assemblies — keep
            // them out of the <Reference> list or the build breaks. They get
            // shipped via the <None> glob below instead.
            var allLibDlls = Directory.GetFiles(libDir, "*.dll")
                .Select(Path.GetFileName)
                .Where(f => f is not null)
                .Where(f => IsManagedDll(Path.Combine(libDir, f)))
                .Cast<string>()
                .ToList();
            var refsXml = string.Join("\n",
                allLibDlls.Select(d => $"            <Reference Include=\"{Path.GetFileNameWithoutExtension(d)}\"><HintPath>lib\\{d}</HintPath></Reference>"));

            var singleFile = spec.SingleFile
                ? "    <PublishSingleFile>true</PublishSingleFile>\n"
                : "";
            File.WriteAllText(Path.Combine(workDir, $"{safeName}.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>{spec.TargetFramework}</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <AssemblyName>{safeName}</AssemblyName>
            {singleFile}  </PropertyGroup>
              <ItemGroup>
            {refsXml}
                <None Include="lib\**\*.dll" CopyToPublishDirectory="PreserveNewest" />
                <EmbeddedResource Include="module.orbt"><LogicalName>module.orbt</LogicalName></EmbeddedResource>
              </ItemGroup>
            </Project>
            """);
            File.WriteAllText(Path.Combine(workDir, "Program.cs"), program);

            foreach (var rid in spec.Rids)
            {
                // Refresh this RID's native assets so the right arch is present
                // (and wins any overwrite) in lib when this publish runs.
                CopyBindingNativeAssets(spec.BindingAssemblyPaths, libDir, new[] { rid });

                var output = Path.Combine(outDir, $"{name}-{rid}");
                Directory.CreateDirectory(output);
                if (verbose) Console.Error.WriteLine($"; Publishing {rid} → {output}");

                var psi = new System.Diagnostics.ProcessStartInfo("dotnet")
                {
                    Arguments = $"publish \"{workDir}\" -c Release -r {rid} -o \"{output}\" --self-contained true",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) { Error($"Failed to start dotnet publish for {rid}."); return 1; }
                var stdout = proc.StandardOutput.ReadToEnd();
                var stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();

                if (proc.ExitCode != 0)
                {
                    Error($"dotnet publish failed for {rid}:\n{stderr}");
                    if (verbose) Console.Error.WriteLine(stdout);
                    return proc.ExitCode;
                }

                var ext = rid.StartsWith("win") ? ".exe" : "";
                var artifact = Path.Combine(output, $"{safeName}{ext}");
                Console.WriteLine(File.Exists(artifact) ? artifact : output);
            }
            return 0;
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    private static string BuildHostSource(BundleSpec spec, string hostAsmFile, IReadOnlyList<string> bindFiles)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.IO;");
        sb.AppendLine("using System.Linq;");
        sb.AppendLine("using System.Reflection;");
        sb.AppendLine("using ObjectRT.Abstractions;");
        sb.AppendLine("using ObjectRT.Reader;");
        sb.AppendLine();
        sb.AppendLine("internal static class Program");
        sb.AppendLine("{");
        sb.AppendLine("    private static void Main()");
        sb.AppendLine("    {");

        for (int i = 0; i < bindFiles.Count; i++)
            sb.AppendLine($"        var bind{i} = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, \"{bindFiles[i]}\"));");

        sb.AppendLine($"        IHostedRuntime rt = new {spec.HostType.FullName}();");
        for (int i = 0; i < bindFiles.Count; i++)
            sb.AppendLine($"        rt.RegisterBindingAssembly(bind{i});");

        // Any IHostedRuntimeSetup implementations in the binding assemblies get
        // a chance to initialize platform state before the module runs.
        for (int i = 0; i < bindFiles.Count; i++)
        {
            sb.AppendLine($"        foreach (var setupType in bind{i}.GetTypes())");
            sb.AppendLine("        {");
            sb.AppendLine("            if (setupType.IsClass && !setupType.IsAbstract");
            sb.AppendLine("                && typeof(ObjectRT.Abstractions.IHostedRuntimeSetup).IsAssignableFrom(setupType))");
            sb.AppendLine("            {");
            sb.AppendLine("                var setup = (ObjectRT.Abstractions.IHostedRuntimeSetup)Activator.CreateInstance(setupType)!;");
            sb.AppendLine("                setup.Setup(rt);");
            sb.AppendLine("                break;");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
        }

        if (!string.IsNullOrEmpty(spec.HostInit))
        {
            foreach (var line in spec.HostInit.Split('\n'))
                sb.AppendLine("        " + line.TrimEnd('\r'));
        }

        sb.AppendLine(@"        using var stream = typeof(Program).Assembly.GetManifestResourceStream(""module.orbt"")");
        sb.AppendLine(@"            ?? throw new InvalidOperationException(""embedded module missing"");");
        sb.AppendLine("        var data = new byte[stream.Length];");
        sb.AppendLine("        _ = stream.Read(data, 0, data.Length);");
        sb.AppendLine("        rt.RunModule(OrbtFileReader.ReadBytes(data));");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// Locate the installed .NET SDK and use its apphost template + HostModel
    /// library to produce a native executable that launches the given DLL.
    /// </summary>
    private static bool TryCreateAppHost(string appHostPath, string managedDllName, bool verbose)
    {
        try
        {
            // Find the SDK root from `dotnet --list-sdks`.
            var psi = new System.Diagnostics.ProcessStartInfo("dotnet")
            {
                Arguments = "--list-sdks",
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return false;
            var sdkLines = proc.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            proc.WaitForExit();

            if (sdkLines.Length == 0) return false;
            var sdkPath = ParseSdkPath(sdkLines[^1]);   // latest SDK
            if (sdkPath is null) return false;

            var template = Path.Combine(sdkPath, "AppHostTemplate",
                OperatingSystem.IsWindows() ? "apphost.exe" : "apphost");
            var hostModel = Path.Combine(sdkPath, "Microsoft.NET.HostModel.dll");
            if (!File.Exists(template) || !File.Exists(hostModel)) return false;

            // Load HostModel from the SDK and call HostWriter.CreateAppHost.
            var asm = Assembly.LoadFrom(hostModel);
            var hostWriter = asm.GetType("Microsoft.NET.HostModel.AppHost.HostWriter")!;
            var createMethods = hostWriter.GetMethods()
                .Where(m => m.Name == "CreateAppHost")
                .OrderByDescending(m => m.GetParameters().Length)
                .ToArray();

            // Common signature: (appHostSource, appHostDestination, appBinary, bool gui, ...).
            foreach (var create in createMethods)
            {
                var ps = create.GetParameters();
                try
                {
                    var argsList = new List<object?> { template, appHostPath, managedDllName };
                    // Fill remaining params: gui bool, then default/optional.
                    foreach (var p in ps.Skip(3))
                    {
                        if (p.ParameterType == typeof(bool)) argsList.Add(false);
                        else if (p.ParameterType == typeof(string)) argsList.Add(null);
                        else if (p.HasDefaultValue) argsList.Add(p.DefaultValue);
                        else if (p.ParameterType.IsValueType) argsList.Add(Activator.CreateInstance(p.ParameterType));
                        else argsList.Add(null);
                    }
                    create.Invoke(null, argsList.ToArray());
                    return true;
                }
                catch
                {
                    // Try next overload.
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            if (verbose) Console.Error.WriteLine($"; apphost creation failed (falling back to `dotnet dll`): {ex.Message}");
            return false;
        }
    }

    private static string? ParseSdkPath(string line)
    {
        var bracket = line.IndexOf('[');
        var close = line.LastIndexOf(']');
        if (bracket < 0 || close <= bracket) return null;
        var root = line[(bracket + 1)..close];     // e.g. "C:\Program Files\dotnet\sdk"
        var version = line[..bracket].Trim();      // e.g. "10.0.302"
        var sdk = Path.Combine(root, version);
        return Directory.Exists(sdk) ? sdk : null;
    }

    private static string SafeName(string name)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in name)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        return sb.Length > 0 ? sb.ToString() : "app";
    }

    /// <summary>
    /// Every DLL found in the directories of the given binding assemblies —
    /// i.e. the binding assemblies plus their own dependencies. Deduplicated
    /// by file name so a shared dependency appears once.
    /// </summary>
    private static IEnumerable<string> BindingDependencyDlls(IReadOnlyList<string> bindingAssemblyPaths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var path in bindingAssemblyPaths)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            foreach (var dll in Directory.GetFiles(dir, "*.dll"))
            {
                var name = Path.GetFileName(dll);
                if (seen.Add(name)) result.Add(dll);
            }
        }
        return result;
    }

    /// <summary>
    /// Copies native assets (<c>runtimes/&lt;rid&gt;/native/*</c>) from the
    /// binding assembly directories into <paramref name="outputDir"/>, flat at
    /// the root. A bundle has no deps.json, so the runtime cannot probe
    /// <c>runtimes/...</c> paths — P/Invoke must find the natives next to the
    /// host. <paramref name="rids"/> null/empty copies only the current
    /// platform's natives (framework-dependent bundles); pass explicit RIDs for
    /// self-contained publishes.
    /// </summary>
    private static void CopyBindingNativeAssets(IReadOnlyList<string> bindingAssemblyPaths, string outputDir, IReadOnlyList<string>? rids)
    {
        var ridSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (rids != null)
            foreach (var r in rids) ridSet.Add(r);
        try { ridSet.Add(System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier); } catch { }

        foreach (var path in bindingAssemblyPaths)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            var runtimes = Path.Combine(dir, "runtimes");
            if (!Directory.Exists(runtimes)) continue;

            foreach (var rid in ridSet)
            {
                var native = Path.Combine(runtimes, rid, "native");
                if (!Directory.Exists(native)) continue;
                foreach (var file in Directory.GetFiles(native))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext is not (".dll" or ".so" or ".dylib")) continue;
                    try { File.Copy(file, Path.Combine(outputDir, Path.GetFileName(file)), overwrite: true); } catch { }
                }
            }
        }
    }

    /// <summary>True when the file is a managed .NET assembly (as opposed to a native library).</summary>
    private static bool IsManagedDll(string path)
    {
        try
        {
            System.Reflection.AssemblyName.GetAssemblyName(path);
            return true;
        }
        catch (BadImageFormatException) { return false; }
        catch (Exception) { return false; }
    }

    private static void Error(string msg) => Console.Error.WriteLine($"Error: {msg}");
}
