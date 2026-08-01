using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ObjectRT.Abstractions;

namespace ObjectRT.Runtime;

/// <summary>
/// Resolves <c>@DllImport("library.dll")</c> annotations in ObjectIL modules
/// to native P/Invoke calls. On first encounter, a bridge assembly is compiled
/// via Roslyn; subsequent calls hit a cache. Marshaling is handled entirely by
/// the CLR's built-in P/Invoke layer.
///
/// Usage in ObjectIL:
/// <code>
/// @DllImport("user32.dll")
/// class User32 {
///     static method MessageBox(hWnd: int32, text: string, caption: string, type: int32) -> int32 { ret }
/// }
/// </code>
///
/// Then in scripts:
/// <code>
/// call User32.MessageBox(int32, string, string, int32)
/// </code>
///
/// The class body is stripped (only signatures matter). Method signatures
/// map directly to P/Invoke: <c>int32 → int</c>, <c>string → string</c>
/// (auto-marshaled as LPWStr when CharSet=Unicode), <c>float32 → float</c>, etc.
/// </summary>
public sealed class DllImportResolver : INativeResolver
{
    // ── Registered DllImport classes ─────────────────────────────

    // className (e.g. "User32") → (dllName, entryPoint defaults)
    private readonly Dictionary<string, DllImportInfo> _imports = new(StringComparer.Ordinal);

    // qualifiedName → cached delegate
    private readonly ConcurrentDictionary<string, Func<object?[], object?>> _cache = new(StringComparer.Ordinal);

    // qualifiedName → pending Task (background compilation in flight)
    private readonly ConcurrentDictionary<string, Task<Func<object?[], object?>>> _pending = new(StringComparer.Ordinal);

    private sealed class DllImportInfo
    {
        public string DllName = "";
        public string CharSet = "Auto";       // Auto | Unicode | Ansi
        public bool ExactSpelling;
        public readonly List<MethodInfo> Methods = new();
    }

    private sealed class MethodInfo
    {
        public string Name = "";
        public string? EntryPoint;
        public string CSharpRetType = "void";
        public readonly List<(string ParamName, string CsType, string Attrs)> Params = new();
    }

    // ── Type mapping ────────────────────────────────────────────

    private static readonly Dictionary<string, string> TypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["void"] = "void",   ["int32"] = "int",    ["uint32"] = "uint",
        ["int64"] = "long",  ["uint64"] = "ulong",  ["int16"] = "short",
        ["uint16"] = "ushort", ["int8"] = "sbyte",  ["uint8"] = "byte",
        ["float32"] = "float", ["float64"] = "double",
        ["string"] = "string", ["object"] = "object",
        ["bool"] = "bool",   ["char"] = "char",
    };

    private static string MapType(string irType) =>
        TypeMap.TryGetValue(irType, out var cs) ? cs : "int";

    private static bool ReturnsValue(string irType) =>
        !string.Equals(irType, "void", StringComparison.OrdinalIgnoreCase);

    // ── I/O ─────────────────────────────────────────────────────

    /// <summary>
    /// When non-null, generated P/Invoke bridge source is written to this
    /// directory as <c>dll_{ClassName}.g.cs</c>.
    /// </summary>
    public static string? EmitDir { get; set; }

    /// <summary>
    /// When non-null, compiled bridge assemblies are cached to this directory
    /// as <c>dll_{hash}.dll</c>.
    /// </summary>
    public static string? CacheDir { get; set; }

    // ── Registration ────────────────────────────────────────────

    /// <summary>
    /// Scan a module for <c>@DllImport("lib.dll")</c> classes and register
    /// all their methods. Returns the number of import classes found.
    /// </summary>
    public int ScanModule(ORBTModule mod, Action<string>? logger = null)
    {
        int count = 0;
        foreach (var type in mod.Types)
        {
            string? dllName = null;

            foreach (var attr in type.Attributes)
            {
                var attrName = mod.Resolve(attr.NameIndex);
                if (attrName == "DllImport" && attr.ArgIndices.Count > 0)
                {
                    dllName = mod.Resolve(attr.ArgIndices[0]);
                    if (dllName.StartsWith("\"") && dllName.EndsWith("\""))
                        dllName = dllName[1..^1];
                    break;
                }
            }

            if (dllName == null) continue;

            var info = new DllImportInfo { DllName = dllName };
            var className = mod.Resolve(type.NameIndex);

            foreach (var method in type.Methods)
            {
                var mname = mod.Resolve(method.NameIndex);
                var mi = new MethodInfo
                {
                    Name = mname,
                    EntryPoint = mname,
                };

                // Return type
                var retType = mod.Resolve(method.SignatureIndex);
                mi.CSharpRetType = MapType(retType);

                // Parameters
                foreach (var p in method.Params)
                {
                    var pname = mod.Resolve(p.NameIndex);
                    var ptype = mod.Resolve(p.TypeIndex);
                    var cst = MapType(ptype);
                    var attrs = ptype.Equals("string", StringComparison.OrdinalIgnoreCase)
                        ? "[MarshalAs(UnmanagedType.LPWStr)]" : "";
                    mi.Params.Add((pname, cst, attrs));
                }

                info.Methods.Add(mi);
            }

            _imports[className] = info;
            logger?.Invoke($"@DllImport(\"{dllName}\") on {className} ({info.Methods.Count} methods)");
            count++;
        }

        if (count > 0)
            GenerateAndCompileBridges();

        return count;
    }

    // ── INativeResolver ─────────────────────────────────────────

    public Delegate? TryResolve(string qualifiedName, object?[] args)
    {
        // ── Cache hit ───────────────────────────────────────────
        if (_cache.TryGetValue(qualifiedName, out var cached))
            return cached;

        // ── Background compile in flight ─────────────────────────
        if (_pending.TryGetValue(qualifiedName, out var task))
            return new Func<object?[], object?>(a => task.Result(a));

        // ── Parse "ClassName.MethodName" ─────────────────────────
        var dot = qualifiedName.LastIndexOf('.');
        if (dot < 0) return null;
        var className = qualifiedName[..dot];
        var methodName = qualifiedName[(dot + 1)..];

        if (!_imports.TryGetValue(className, out var info)) return null;

        var methodInfo = info.Methods.FirstOrDefault(m =>
            m.Name.Equals(methodName, StringComparison.Ordinal));
        if (methodInfo == null) return null;

        // ── Start background compilation ─────────────────────────
        var tcs = new TaskCompletionSource<Func<object?[], object?>>();
        _pending[qualifiedName] = tcs.Task;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                GenerateAndCompileBridges();
                var fn = _cache.TryGetValue(qualifiedName, out var c) ? c : null;
                tcs.SetResult(fn ?? Fallback(qualifiedName));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DllImport] '{qualifiedName}' failed: {ex.Message}");
                tcs.SetResult(Fallback(qualifiedName));
            }
        });

        return new Func<object?[], object?>(a => tcs.Task.Result(a));
    }

    private static Func<object?[], object?> Fallback(string name) =>
        _ => throw new MissingMethodException($"DllImport '{name}' could not be resolved");

    // ── Bridge generation ───────────────────────────────────────

    private void GenerateAndCompileBridges()
    {
        if (_imports.Count == 0) return;

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Runtime.InteropServices;");
        sb.AppendLine();

        int classIdx = 0;
        foreach (var (className, info) in _imports)
        {
            var safeClass = $"__dll_{className}";
            sb.AppendLine($"public static class {safeClass} {{");

            // P/Invoke declarations
            foreach (var mi in info.Methods)
            {
                var entry = mi.EntryPoint ?? mi.Name;
                var ret = mi.CSharpRetType;

                sb.Append($"    [DllImport(\"{Escape(info.DllName)}\", EntryPoint = \"{Escape(entry)}\"");
                if (!info.ExactSpelling)
                {
                    sb.Append($", CharSet = CharSet.{info.CharSet}");
                    // Append W suffix for Unicode on non-exact-spelling
                    if (info.CharSet == "Unicode")
                        sb.Append($", /* maps to {entry}W */");
                }
                sb.AppendLine(")]");

                sb.Append($"    public static extern {ret} __pinvk_{mi.Name}(");
                var paramDecls = mi.Params.Select(p =>
                    $"{(p.Attrs.Length > 0 ? p.Attrs + " " : "")}{p.CsType} {p.ParamName}");
                sb.Append(string.Join(", ", paramDecls));
                sb.AppendLine(");");
            }

            sb.AppendLine();

            // Bridge delegates: object?[] ↔ typed P/Invoke call
            foreach (var mi in info.Methods)
            {
                var ret = mi.CSharpRetType;
                var returns = ret != "void";

                sb.Append($"    public static object? __bridge_{mi.Name}(object?[] a) {{");

                if (returns) sb.Append(" return ");
                sb.Append($"__pinvk_{mi.Name}(");

                var args = new List<string>();
                for (int i = 0; i < mi.Params.Count; i++)
                {
                    var p = mi.Params[i];
                    args.Add($"({p.CsType})a[{i}]!");
                }
                sb.Append(string.Join(", ", args));
                sb.Append(");");

                if (!returns) sb.Append(" return null;");
                sb.AppendLine(" }");
            }

            sb.AppendLine("}");
            sb.AppendLine();

            classIdx++;
        }

        var source = sb.ToString();

        // ── Emit to disk ──────────────────────────────────────────
        if (EmitDir is not null)
        {
            try
            {
                Directory.CreateDirectory(EmitDir);
                var path = Path.Combine(EmitDir, $"dllimport_bridge.g.cs");
                File.WriteAllText(path, source);
            }
            catch { }
        }

        // ── Cache check ──────────────────────────────────────────
        var hash = ComputeHash(source);
        Assembly? asm = null;

        if (CacheDir is not null)
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                var cachePath = Path.Combine(CacheDir, $"dll_{hash}.dll");
                if (File.Exists(cachePath))
                {
                    asm = Assembly.Load(File.ReadAllBytes(cachePath));
                }
            }
            catch { }
        }

        // ── Compile + cache ──────────────────────────────────────
        if (asm == null)
        {
            asm = CompileAssembly(source);
            if (asm != null && CacheDir is not null)
            {
                try
                {
                    var cachePath = Path.Combine(CacheDir, $"dll_{hash}.dll");
                    if (s_lastEmittedBytes is not null)
                        File.WriteAllBytes(cachePath, s_lastEmittedBytes);
                }
                catch { }
            }
        }

        if (asm == null) return;

        // ── Extract delegates ────────────────────────────────────
        foreach (var (className, info) in _imports)
        {
            var safeClass = $"__dll_{className}";
            var type = asm.GetType(safeClass);
            if (type == null) continue;

            foreach (var mi in info.Methods)
            {
                var qualifiedName = $"{className}.{mi.Name}";
                var bridge = type.GetMethod($"__bridge_{mi.Name}", BindingFlags.Public | BindingFlags.Static);
                if (bridge == null) continue;

                var fn = (Func<object?[], object?>)Delegate.CreateDelegate(
                    typeof(Func<object?[], object?>), bridge);
                _cache[qualifiedName] = fn;
            }
        }
    }

    // ── Roslyn ──────────────────────────────────────────────────

    private static byte[]? s_lastEmittedBytes;

    private static Assembly? CompileAssembly(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source,
            new CSharpParseOptions(LanguageVersion.Latest));

        var refs = new[]
        {
            typeof(object).Assembly,
            Assembly.Load("System.Runtime"),
        }.Select(a => MetadataReference.CreateFromFile(a.Location)).ToArray();

        var comp = CSharpCompilation.Create("DllImport_Bridge",
            new[] { tree }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release));

        using var ms = new MemoryStream();
        var result = comp.Emit(ms);
        if (!result.Success)
        {
            var diag = string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
            Console.Error.WriteLine($"[DllImport] compilation failed:\n{diag}");
            s_lastEmittedBytes = null;
            return null;
        }

        var bytes = ms.ToArray();
        s_lastEmittedBytes = bytes;
        return Assembly.Load(bytes);
    }

    // ── Helpers ─────────────────────────────────────────────────

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string ComputeHash(string source)
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Clear all registrations and caches.</summary>
    public void Reset()
    {
        _imports.Clear();
        _cache.Clear();
        _pending.Clear();
    }
}
