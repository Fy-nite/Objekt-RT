using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ObjectRT.Abstractions;
using ObjectRT.Reader;
using ObjectRT.Runtime;

// ── CLI ───────────────────────────────────────────────────────────────
var exe = Path.GetFileName(Environment.GetCommandLineArgs()[0]);
bool verbose = false, jit = false, scanOnly = false, compileOnly = false, bundle = false, runEntry = true;
string? filePath = null, methodCall = null;
var methodArgs = new List<string>();
var rids = new List<string>();
string? emitDir = null, cacheDir = null, outPath = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-h": case "--help": Help(exe); return 0;
        case "-v": case "--verbose": verbose = true; break;
        case "-j": case "--jit": jit = true; break;
        case "-s": case "--scan": scanOnly = true; runEntry = false; break;
        case "-c": case "--compile": compileOnly = true; runEntry = false; break;
        case "-b": case "--bundle": bundle = true; runEntry = false; break;
        case "--rid":
            if (++i >= args.Length) { Error("--rid requires a value (comma-separated)"); return 1; }
            rids.AddRange(args[i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            break;
        case "-m": case "--method":
            if (++i >= args.Length) { Error("--method requires a name"); return 1; }
            methodCall = args[i]; runEntry = false;
            while (i + 1 < args.Length && !args[i + 1].StartsWith('-')
                   && !args[i + 1].EndsWith(".oil", StringComparison.OrdinalIgnoreCase)
                   && !args[i + 1].EndsWith(".orbt", StringComparison.OrdinalIgnoreCase)
                   && !args[i + 1].EndsWith(".oir", StringComparison.OrdinalIgnoreCase))
                methodArgs.Add(args[++i]);
            break;
        case "-o": case "--output":
            if (++i >= args.Length) { Error("--output requires a path"); return 1; }
            outPath = args[i]; break;
        case "--emit":
            if (++i >= args.Length) { Error("--emit requires a directory"); return 1; }
            emitDir = args[i]; break;
        case "--cache":
            if (++i >= args.Length) { Error("--cache requires a directory"); return 1; }
            cacheDir = args[i]; break;
        default:
            if (args[i].StartsWith('-')) { Error($"Unknown option: {args[i]}"); Help(exe); return 1; }
            filePath = args[i]; break;
    }
}

if (filePath is null) { Error("No input file specified."); Help(exe); return 1; }

// ── Load ─────────────────────────────────────────────────────────────
ORBTModule module;
var format = DetectFormat(filePath);
try
{
    module = format == FileFormat.ORBT
        ? OrbtFileReader.ReadFile(filePath)
        : OilFileReader.ParseFile(filePath);
}
catch (Exception e) { Error($"Parse error: {e.Message}"); return 1; }

if (verbose)
    Console.Error.WriteLine($"; Loaded {Path.GetFileName(filePath)} ({format}) — " +
        $"{module.Types.Count} types, {module.Types.Sum(t => t.MethodCount)} methods");

// ── Scan only ────────────────────────────────────────────────────────
if (scanOnly) { Console.Write(module.Dump(verbose)); return 0; }

// ── Compile to .orbt ─────────────────────────────────────────────────
if (compileOnly)
{
    outPath ??= Path.ChangeExtension(filePath, ".orbt");
    var writer = new ORBTWriter();
    var bytes = writer.WriteModule(module);
    File.WriteAllBytes(outPath, bytes);
    if (verbose) Console.Error.WriteLine($"; Wrote {bytes.Length} bytes to {outPath}");
    else Console.WriteLine(outPath);
    return 0;
}

// ── Bundle: wrap .orbt in a dotnet executable ────────────────────────
if (bundle)
{
    // Ensure we have .orbt bytes (compile .oil first if needed).
    var orbtBytes = format == FileFormat.ORBT
        ? File.ReadAllBytes(filePath)
        : new ORBTWriter().WriteModule(module);
    return Bundle(exe, filePath, orbtBytes, outPath, rids, verbose);
}

// ── Configure Runtime ────────────────────────────────────────────────
if (emitDir is not null) Runtime.EmitDir = emitDir;
if (cacheDir is not null) Runtime.CacheDir = cacheDir;

var rt = new Runtime { Mode = jit ? JitMode.Reflection : JitMode.Interpreter };
rt.DllResolver.ScanModule(module, m => { if (verbose) Console.Error.WriteLine($";   {m}"); });
rt.NativeResolver.ScanModule(module, m => { if (verbose) Console.Error.WriteLine($";   {m}"); });
rt.LoadModule(module);

if (verbose)
    Console.Error.WriteLine($"; Module loaded, executor: {(jit ? "ReflectionJit" : "Interpreter")}");

// ── Run ──────────────────────────────────────────────────────────────
try
{
    if (methodCall is not null)
    {
        var parsedArgs = methodArgs.Count > 0
            ? methodArgs.Select(a => (object?)ParseArg(a)).ToArray()
            : Array.Empty<object?>();

        if (verbose)
            Console.Error.WriteLine($"; Calling {methodCall}({string.Join(", ", parsedArgs.Select(a =>
                a is string s ? $"\"{s}\"" : a?.ToString() ?? "null"))})");

        var result = rt.CallMethod<object?>(methodCall, parsedArgs);
        Console.WriteLine(result is null ? "null" : result is int i ? i.ToString() : result.ToString());
    }
    else if (runEntry)
    {
        string? entryName = null;
        foreach (var t in module.Types)
        {
            var name = $"{module.Resolve(t.NameIndex)}.Main";
            if (t.Methods.Any(m => module.Resolve(m.NameIndex) == "Main"))
                { entryName = name; break; }
        }
        if (entryName is null) { Error("No entry point (class with static method Main) found."); return 1; }
        if (verbose) Console.Error.WriteLine($"; Entry: {entryName}");
        rt.CallMethod<object?>(entryName);
        if (verbose) Console.Error.WriteLine("; Done.");
    }
}
catch (Exception e) { Error(e.Message); return 1; }

return 0;

// ── Helpers ──────────────────────────────────────────────────────────

static object? ParseArg(string a) =>
    int.TryParse(a, out var i) ? i :
    a == "true" ? true :
    a == "false" ? false :
    a;

// ── Bundle: wrap a .orbt binary in a standalone dotnet executable ─────

/// <summary>
/// Compile the host (embedding the .orbt as a manifest resource) via Roslyn
/// in memory, write the assembly + apphost to disk. For --rid, additionally
/// run dotnet publish to get a self-contained native executable.
/// Output: {outDir}/{name} (no rid) or {outDir}/{name}-{rid}/{name}.
/// Works when the CLI is installed as a global dotnet tool — runtime DLLs
/// are referenced from AppContext.BaseDirectory, not from a source checkout.
/// </summary>
static int Bundle(string exe, string sourceFile, byte[] orbtBytes, string? outDir,
    List<string> rids, bool verbose)
{
    var name = Path.GetFileNameWithoutExtension(sourceFile);
    outDir ??= Path.Combine(Directory.GetCurrentDirectory(), name);
    var safeName = SafeName(name);

    // ── Runtime DLLs come from the CLI's own directory ───────────────
    var baseDir = AppContext.BaseDirectory;
    var required = new[]
    {
        "ObjectRT.Runtime.dll", "ObjectRT.VM.dll",
        "ObjectRT.Reader.dll", "ObjectRT.Abstractions.dll",
        "Microsoft.CodeAnalysis.dll", "Microsoft.CodeAnalysis.CSharp.dll",
    };
    var missing = required.Where(d => !File.Exists(Path.Combine(baseDir, d))).ToArray();
    if (missing.Length > 0)
    {
        Error($"Cannot find runtime assemblies in '{baseDir}': {string.Join(", ", missing)}");
        return 1;
    }

    // ── Host source (embedded module + runtime wiring) ───────────────
    var program = $$"""
    using System;
    using System.Linq;
    using System.Collections.Generic;
    using ObjectRT.Abstractions;
    using ObjectRT.Reader;
    using ObjectRT.Runtime;

    internal static class Program
    {
        private static void Main()
        {
            var rt = new Runtime();
            using var stream = typeof(Program).Assembly.GetManifestResourceStream("module.orbt")
                ?? throw new InvalidOperationException("embedded module missing");
            var data = new byte[stream.Length];
            _ = stream.Read(data, 0, data.Length);
            var module = OrbtFileReader.ReadBytes(data);

            rt.DllResolver.ScanModule(module, null);
            rt.NativeResolver.ScanModule(module, null);
            rt.LoadModule(module);

            string? entry = null;
            foreach (var t in module.Types)
            {
                var nm = $"{module.Resolve(t.NameIndex)}.Main";
                if (t.Methods.Any(m => module.Resolve(m.NameIndex) == "Main")) { entry = nm; break; }
            }
            if (entry is null)
            {
                Console.Error.WriteLine("Error: no entry point (class with static method Main) found.");
                Environment.Exit(1);
            }
            rt.CallMethod<object?>(entry);
        }
    }
    """;

    var refs = required
        .Select(d => MetadataReference.CreateFromFile(Path.Combine(baseDir, d)))
        .ToList();

    // Reference every DLL in the CLI dir (Roslyn + ObjectRT deps).
    foreach (var dll in Directory.GetFiles(baseDir, "*.dll"))
    {
        var path = Path.Combine(baseDir, dll);
        if (!refs.Any(r => r.FilePath == path))
        {
            try { refs.Add(MetadataReference.CreateFromFile(path)); } catch { }
        }
    }

    // The canonical runtime reference set: every shared-framework assembly.
    // This guarantees System.Console, System.Linq, System.Runtime, etc. resolve
    // in the Roslyn compilation regardless of how the CLI was installed.
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

    // ── Default: framework-dependent, in-memory compile + write ──────
    if (rids.Count == 0)
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

        // Copy runtime DLLs next to the host.
        foreach (var dll in Directory.GetFiles(baseDir, "*.dll"))
            File.Copy(dll, Path.Combine(outputDir, Path.GetFileName(dll)), overwrite: true);

        // runtimeconfig.json (framework-dependent).
        File.WriteAllText(Path.Combine(outputDir, $"{safeName}.runtimeconfig.json"), $$"""
        {
          "runtimeOptions": {
            "tfm": "net10.0",
            "rollForward": "LatestMinor",
            "framework": { "name": "Microsoft.NETCore.App", "version": "10.0.0" }
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

    // ── --rid: self-contained publish (needs dotnet SDK + MSBuild) ───
    var workDir = Path.Combine(Path.GetTempPath(), $"objrt_bundle_{Guid.NewGuid():N}");
    Directory.CreateDirectory(workDir);
    var libDir = Path.Combine(workDir, "lib");
    Directory.CreateDirectory(libDir);

    try
    {
        foreach (var dll in Directory.GetFiles(baseDir, "*.dll"))
            File.Copy(dll, Path.Combine(libDir, Path.GetFileName(dll)), overwrite: true);
        File.WriteAllBytes(Path.Combine(workDir, "module.orbt"), orbtBytes);

        var refsXml = string.Join("\n",
            required.Select(d => $"            <Reference Include=\"{Path.GetFileNameWithoutExtension(d)}\"><HintPath>lib\\{d}</HintPath></Reference>"));

        File.WriteAllText(Path.Combine(workDir, $"{safeName}.csproj"), $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
            <AssemblyName>{safeName}</AssemblyName>
          </PropertyGroup>
          <ItemGroup>
        {refsXml}
            <EmbeddedResource Include="module.orbt"><LogicalName>module.orbt</LogicalName></EmbeddedResource>
          </ItemGroup>
        </Project>
        """);
        File.WriteAllText(Path.Combine(workDir, "Program.cs"), program);

        foreach (var rid in rids)
        {
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

/// <summary>
/// Locate the installed .NET SDK and use its apphost template + HostModel
/// library to produce a native executable that launches the given DLL.
/// </summary>
static bool TryCreateAppHost(string appHostPath, string managedDllName, bool verbose)
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

static string? ParseSdkPath(string line)
{
    var bracket = line.IndexOf('[');
    var close = line.LastIndexOf(']');
    if (bracket < 0 || close <= bracket) return null;
    var root = line[(bracket + 1)..close];     // e.g. "C:\Program Files\dotnet\sdk"
    var version = line[..bracket].Trim();      // e.g. "10.0.302"
    var sdk = Path.Combine(root, version);
    return Directory.Exists(sdk) ? sdk : null;
}

static string SafeName(string name)
{
    var sb = new System.Text.StringBuilder();
    foreach (char c in name)
        sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
    return sb.Length > 0 ? sb.ToString() : "app";
}

static void Help(string exe) => Console.Error.WriteLine($"""
ObjectRT CLI — load, compile, run, and bundle ObjectIL modules.

Usage: {exe} [options] <file>

Options:
  -v, --verbose       Show detailed output
  -j, --jit           Use the Roslyn JIT backend (ReflectionJit)
  -s, --scan          Dump module structure without executing
  -c, --compile       Compile .oil source to .orbt binary
  -b, --bundle        Wrap .orbt in a standalone dotnet executable
  --rid <list>        RIDs for --bundle, comma-separated (default: win-x64)
  -o, --output <path> Output path for compiled .orbt / bundle directory
  -m, --method <name> Call a specific method (args follow)
  --emit  <dir>       Write generated C# to directory (JIT only)
  --cache <dir>       Cache compiled assemblies (JIT only)
  -h, --help          Show this message

Examples:
  {exe} hello.oil                     Run entry point
  {exe} -j -v game.oil               JIT mode, verbose
  {exe} -c prog.oil -o prog.orbt     Compile to binary
  {exe} -b prog.orbt --rid win-x64   Bundle as Windows exe
  {exe} -b prog.orbt --rid win-x64,linux-x64,osx-x64   Bundle for all platforms
  {exe} -m Calc.Add 3 4 math.oil     Call Calc.Add(3, 4)
  {exe} --jit --emit ./gen prog.oil  JIT + dump generated C#
""");

static void Error(string msg) => Console.Error.WriteLine($"Error: {msg}");

static FileFormat DetectFormat(string path)
{
    try
    {
        using var fs = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[4];
        if (fs.Read(magic) >= 4)
            if (magic[0] == 'O' && magic[1] == 'R' && magic[2] == 'B' && magic[3] == 'T')
                return FileFormat.ORBT;
        if (path.EndsWith(".oil", StringComparison.OrdinalIgnoreCase)
         || path.EndsWith(".oir", StringComparison.OrdinalIgnoreCase))
            return FileFormat.ObjectIL;
        if (path.EndsWith(".orbt", StringComparison.OrdinalIgnoreCase))
            return FileFormat.ORBT;
        fs.Seek(0, SeekOrigin.Begin);
        Span<byte> head = stackalloc byte[6];
        if (fs.Read(head) >= 6)
            if (head[0] == 'm' && head[1] == 'o' && head[2] == 'd'
             && head[3] == 'u' && head[4] == 'l' && head[5] == 'e')
                return FileFormat.ObjectIL;
    }
    catch { }
    return FileFormat.Unknown;
}

enum FileFormat { Unknown, ObjectIL, ORBT }
