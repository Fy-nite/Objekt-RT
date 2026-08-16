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
/// (auto-marshaled as <see cref="UnmanagedType.LPUTF8Str"/> — UTF-8 bytes —
/// which is what modern C libraries like raylib expect), <c>float32 → float</c>,
/// etc.
/// </summary>
public sealed class DllImportResolver : INativeResolver
{
    // ── Registered DllImport classes ─────────────────────────────

    // className (e.g. "User32") → (dllName, entryPoint defaults)
    private readonly Dictionary<string, DllImportInfo> _imports = new(StringComparer.Ordinal);

    // Wire struct name → fields (name, wire type). Collected from the module so
    // method signatures can be typed as blittable C# structs.
    private readonly Dictionary<string, List<(string Name, string WireType)>> _moduleStructs = new(StringComparer.Ordinal);

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
        public string RetWireType = "void";
        public string CSharpRetType = "void";
        public bool ReturnIsStruct;
        public readonly List<(string ParamName, string WireType, string CsType, string Attrs, bool IsStruct)> Params = new();
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

    /// <summary>Exact module-struct key for a wire type name (last-segment
    /// fallback: "Color" finds "com.example.Color"), or null when not a struct.</summary>
    private string? FindStructKey(string wireType)
    {
        if (string.IsNullOrEmpty(wireType)) return null;
        if (_moduleStructs.ContainsKey(wireType)) return wireType;
        int dot = wireType.LastIndexOf('.');
        if (dot > 0 && dot < wireType.Length - 1)
        {
            string shortName = wireType[(dot + 1)..];
            foreach (var key in _moduleStructs.Keys)
                if (key == shortName || key.EndsWith("." + shortName, StringComparison.Ordinal)) return key;
        }
        return null;
    }

    /// <summary>True when 'irType' names a struct declared in the scanned module.</summary>
    private bool IsModuleStruct(string irType) => FindStructKey(irType) != null;

    /// <summary>Maps a wire type to (C# type, isStruct). Struct names map to the generated blittable C# struct.</summary>
    private (string CsType, bool IsStruct) MapStructAware(string irType)
    {
        var key = FindStructKey(irType);
        if (key != null)
            return ($"__st_{BridgeClassName(key)}", true);
        return (MapType(irType), false);
    }

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

    /// <summary>
    /// Additional directories probed by generated bridges for native
    /// libraries, on top of the current working directory and the app base.
    /// The module loader registers the directory of any module file it loads,
    /// so DLLs sitting next to a compiled module (e.g. <c>bin\raylib.dll</c>)
    /// are found even though CLR P/Invoke probing does not search CWD.
    /// </summary>
    public static readonly ConcurrentBag<string> ExtraSearchDirectories = new();

    /// <summary>Registers an extra directory probed for native libraries by generated bridges.</summary>
    public static void AddSearchDirectory(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        try
        {
            if (Directory.Exists(dir))
                ExtraSearchDirectories.Add(Path.GetFullPath(dir));
        }
        catch { /* unregisterable dir — ignore */ }
    }

    // ── Registration ────────────────────────────────────────────

    /// <summary>
    /// Scan a module for <c>@DllImport("lib.dll")</c> classes and register
    /// all their methods. Returns the number of import classes found.
    /// </summary>
    public int ScanModule(ORBTModule mod, Action<string>? logger = null)
    {
        // Collect every struct the module declares so import signatures can be
        // typed as blittable C# structs (structs passed to / returned from
        // native functions by value).
        _moduleStructs.Clear();
        foreach (var type in mod.Types)
        {
            if ((ObjectRT.Abstractions.TypeKind)(byte)type.Kind != ObjectRT.Abstractions.TypeKind.Struct) continue;
            var fields = new List<(string, string)>(type.Fields.Count);
            foreach (var f in type.Fields)
                fields.Add((mod.Resolve(f.NameIndex), mod.Resolve(f.TypeIndex)));
            _moduleStructs[mod.Resolve(type.NameIndex)] = fields;
        }

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
                var (retCs, retStruct) = MapStructAware(retType);
                mi.RetWireType = retType;
                mi.CSharpRetType = retCs;
                mi.ReturnIsStruct = retStruct;

                // Parameters
                foreach (var p in method.Params)
                {
                    var pname = mod.Resolve(p.NameIndex);
                    var ptype = mod.Resolve(p.TypeIndex);
                    var (cst, isStruct) = MapStructAware(ptype);
                    // Modern C libraries (raylib, SDL, ...) take `const char*`
                    // as UTF-8. LPWStr would pass UTF-16 bytes, which a C
                    // string reader truncates at the first NUL byte (the
                    // classic "H" instead of "Hello world" bug).
                    var attrs = ptype.Equals("string", StringComparison.OrdinalIgnoreCase)
                        ? "[MarshalAs(UnmanagedType.LPUTF8Str)]" : "";
                    mi.Params.Add((pname, ptype, cst, attrs, isStruct));
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

        // Native library search directories: CWD, app base, and any
        // directories registered by the host (e.g. the module's own
        // directory, where DLLs are usually dropped).
        var searchDirs = new List<string>();
        void AddSearchDir(string? d)
        {
            if (string.IsNullOrEmpty(d)) return;
            try
            {
                var full = Path.GetFullPath(d);
                if (!searchDirs.Contains(full, StringComparer.OrdinalIgnoreCase))
                    searchDirs.Add(full);
            }
            catch { }
        }
        AddSearchDir(Environment.CurrentDirectory);
        AddSearchDir(AppContext.BaseDirectory);
        foreach (var d in ExtraSearchDirectories) AddSearchDir(d);

        if (searchDirs.Count > 0)
        {
            sb.AppendLine("internal static class __dll_search");
            sb.AppendLine("{");
            sb.Append("    internal static readonly string[] Directories = new[] { ");
            sb.Append(string.Join(", ", searchDirs.Select(d => $"@\"{d.Replace("\"", "\"\"")}\"")));
            sb.AppendLine(" };");
            sb.AppendLine("    internal static System.IntPtr Resolve(string libraryName, System.Reflection.Assembly assembly, System.Runtime.InteropServices.DllImportSearchPath? searchPath)");
            sb.AppendLine("    {");
            sb.AppendLine("        var baseName = libraryName.EndsWith(\".dll\", StringComparison.OrdinalIgnoreCase) ? libraryName : libraryName + \".dll\";");
            sb.AppendLine("        foreach (var dir in Directories)");
            sb.AppendLine("        {");
            sb.AppendLine("            var candidate = System.IO.Path.Combine(dir, baseName);");
            sb.AppendLine("            if (System.IO.File.Exists(candidate) && System.Runtime.InteropServices.NativeLibrary.TryLoad(candidate, out var handle))");
            sb.AppendLine("                return handle;");
            sb.AppendLine("        }");
            sb.AppendLine("        return System.IntPtr.Zero;");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        // ── Struct definitions + marshalling helpers ──────────────
        // Structs referenced by import signatures, transitively (a struct field
        // may itself be a struct). Blittable Sequential C# structs, so the CLR
        // P/Invoke layer moves them to/from native by value.
        var referencedStructs = new List<string>();
        var referencedSet = new HashSet<string>(StringComparer.Ordinal);
        var structStack = new Stack<string>();
        foreach (var (_, info) in _imports)
        {
            foreach (var mi in info.Methods)
            {
                if (mi.ReturnIsStruct) structStack.Push(mi.RetWireType);
                foreach (var p in mi.Params) if (p.IsStruct) structStack.Push(p.WireType);
            }
        }
        while (structStack.Count > 0)
        {
            var name = structStack.Pop();
            var key = FindStructKey(name);
            if (key == null || !referencedSet.Add(key)) continue;
            referencedStructs.Add(key);
            foreach (var (_, ft) in _moduleStructs[key])
            {
                var fkey = FindStructKey(ft);
                if (fkey != null) structStack.Push(fkey);
            }
        }

        sb.AppendLine("internal static class __dll_conv");
        sb.AppendLine("{");
        sb.AppendLine("    public static T __from_bytes<T>(byte[] b) where T : struct");
        sb.AppendLine("    {");
        sb.AppendLine("        var g = System.Runtime.InteropServices.GCHandle.Alloc(b, GCHandleType.Pinned);");
        sb.AppendLine("        try { return System.Runtime.InteropServices.Marshal.PtrToStructure<T>(g.AddrOfPinnedObject()); }");
        sb.AppendLine("        finally { g.Free(); }");
        sb.AppendLine("    }");
        sb.AppendLine("    public static byte[] __to_bytes<T>(T v) where T : struct");
        sb.AppendLine("    {");
        sb.AppendLine("        int size = System.Runtime.InteropServices.Marshal.SizeOf<T>();");
        sb.AppendLine("        var b = new byte[size];");
        sb.AppendLine("        var g = System.Runtime.InteropServices.GCHandle.Alloc(b, GCHandleType.Pinned);");
        sb.AppendLine("        try { System.Runtime.InteropServices.Marshal.StructureToPtr(v, g.AddrOfPinnedObject(), false); }");
        sb.AppendLine("        finally { g.Free(); }");
        sb.AppendLine("        return b;");
        sb.AppendLine("    }");
        sb.AppendLine("    public static byte __cvt_byte(object? v) => unchecked((byte)(int)v!);");
        sb.AppendLine("    public static sbyte __cvt_sbyte(object? v) => unchecked((sbyte)(int)v!);");
        sb.AppendLine("    public static short __cvt_short(object? v) => unchecked((short)(int)v!);");
        sb.AppendLine("    public static ushort __cvt_ushort(object? v) => unchecked((ushort)(int)v!);");
        sb.AppendLine("    public static uint __cvt_uint(object? v) => unchecked((uint)(int)v!);");
        sb.AppendLine("    public static ulong __cvt_ulong(object? v) => unchecked((ulong)(long)v!);");
        sb.AppendLine("    public static long __cvt_long(object? v) => (long)v!;");
        sb.AppendLine("    public static float __cvt_float(object? v) => (float)Convert.ToDouble(v!);");
        sb.AppendLine("    public static double __cvt_double(object? v) => Convert.ToDouble(v!);");
        sb.AppendLine("    public static bool __cvt_bool(object? v) => Convert.ToBoolean(v!);");
        sb.AppendLine("    public static char __cvt_char(object? v) => (char)(int)v!;");
        sb.AppendLine("    public static object? __norm_byte(byte v) => (int)v;");
        sb.AppendLine("    public static object? __norm_sbyte(sbyte v) => (int)v;");
        sb.AppendLine("    public static object? __norm_short(short v) => (int)v;");
        sb.AppendLine("    public static object? __norm_ushort(ushort v) => (int)v;");
        sb.AppendLine("    public static object? __norm_uint(uint v) => unchecked((int)v);");
        sb.AppendLine("    public static object? __norm_ulong(ulong v) => unchecked((long)v);");
        sb.AppendLine("    public static object? __norm_char(char v) => (int)v;");
        sb.AppendLine("}");

        foreach (var name in referencedStructs)
        {
            sb.AppendLine("[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]");
            sb.AppendLine($"public struct __st_{BridgeClassName(name)} {{");
            foreach (var (fname, ftype) in _moduleStructs[name])
            {
                var fkey = FindStructKey(ftype);
                var fcs = fkey != null ? $"__st_{BridgeClassName(fkey)}" : MapType(ftype);
                sb.AppendLine($"    public {fcs} {SafeIdentifier(fname)};");
            }
            sb.AppendLine("}");
            sb.AppendLine();
        }

        foreach (var (className, info) in _imports)
        {
            var safeClass = $"__dll_{BridgeClassName(className)}";
            sb.AppendLine($"public static class {safeClass} {{");

            if (searchDirs.Count > 0)
            {
                sb.AppendLine("    [System.Runtime.CompilerServices.ModuleInitializer]");
                sb.AppendLine($"    internal static void __register_dll() => System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(typeof({safeClass}).Assembly, __dll_search.Resolve);");
            }

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
                var returns = mi.CSharpRetType != "void";

                sb.Append($"    public static object? __bridge_{mi.Name}(object?[] a) {{");

                var args = new List<string>();
                for (int i = 0; i < mi.Params.Count; i++)
                {
                    var p = mi.Params[i];
                    if (p.IsStruct)
                        args.Add($"__dll_conv.__from_bytes<__st_{BridgeClassName(FindStructKey(p.WireType)!)}>((byte[])a[{i}]!)");
                    else if (p.WireType.Equals("int32", StringComparison.OrdinalIgnoreCase))
                        args.Add($"(int)a[{i}]!");
                    else if (p.WireType.Equals("string", StringComparison.OrdinalIgnoreCase))
                        args.Add($"(string)a[{i}]!");
                    else
                        args.Add($"__dll_conv.__cvt_{p.CsType}(a[{i}])");
                }

                var call = $"__pinvk_{mi.Name}({string.Join(", ", args)})";
                if (!returns)
                {
                    sb.AppendLine($" {call}; return null; }}");
                }
                else if (mi.ReturnIsStruct)
                {
                    sb.AppendLine($" return __dll_conv.__to_bytes({call}); }}");
                }
                else if (NeedsReturnNorm(mi.RetWireType))
                {
                    sb.AppendLine($" return __dll_conv.__norm_{mi.CSharpRetType}({call}); }}");
                }
                else
                {
                    sb.AppendLine($" return {call}; }}");
                }
            }

            sb.AppendLine("}");
            sb.AppendLine();
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
            var safeClass = $"__dll_{BridgeClassName(className)}";
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

    /// <summary>
    /// Converts a module type name — which may be namespace-qualified, e.g.
    /// "Raylib.Raylib" — into a valid C# identifier for the generated bridge
    /// class. Dots and other non-identifier characters become '_'; a short
    /// stable hash of the original name is appended whenever a replacement was
    /// needed, so distinct names can never collide (e.g. "A.B" vs "A_B").
    /// </summary>
    private static string BridgeClassName(string className)
    {
        var sb = new StringBuilder(className.Length + 10);
        foreach (var c in className)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        var safe = sb.ToString();
        if (safe == className) return safe;
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(className)));
        return $"{safe}_{hash[..8].ToLowerInvariant()}";
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>Escapes a generated field name when it collides with a C# keyword.</summary>
    private static string SafeIdentifier(string name)
    {
        switch (name)
        {
            case "abstract": case "as": case "base": case "bool": case "break": case "byte":
            case "case": case "catch": case "char": case "checked": case "class": case "const":
            case "continue": case "decimal": case "default": case "delegate": case "do": case "double":
            case "else": case "enum": case "event": case "explicit": case "extern": case "false":
            case "finally": case "fixed": case "float": case "for": case "foreach": case "goto":
            case "if": case "implicit": case "in": case "int": case "interface": case "internal":
            case "is": case "lock": case "long": case "namespace": case "new": case "null":
            case "object": case "operator": case "out": case "override": case "params": case "private":
            case "protected": case "public": case "readonly": case "ref": case "return": case "sbyte":
            case "sealed": case "short": case "sizeof": case "stackalloc": case "static": case "string":
            case "struct": case "switch": case "this": case "throw": case "true": case "try":
            case "typeof": case "uint": case "ulong": case "unchecked": case "unsafe": case "ushort":
            case "using": case "virtual": case "void": case "volatile": case "while":
                return "@" + name;
            default:
                return name;
        }
    }

    /// <summary>Returns that the CLR can hand back to the VM unchanged. All
    /// other integer/char widths must be re-widened to what the VM expects.</summary>
    private static bool NeedsReturnNorm(string wireType) => wireType.ToLowerInvariant() switch
    {
        "int8" or "uint8" or "int16" or "uint16" or "uint32" or "uint64" or "char" => true,
        _ => false,
    };

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
