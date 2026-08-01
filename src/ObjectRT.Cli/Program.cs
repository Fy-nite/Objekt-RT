using ObjectRT.Abstractions;
using ObjectRT.Reader;
using ObjectRT.Runtime;

// ── CLI ───────────────────────────────────────────────────────────────
var exe = Path.GetFileName(Environment.GetCommandLineArgs()[0]);
bool verbose = false, jit = false, scanOnly = false, runEntry = true;
string? filePath = null, methodCall = null;
var methodArgs = new List<string>();
string? emitDir = null, cacheDir = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-h": case "--help": Help(exe); return 0;
        case "-v": case "--verbose": verbose = true; break;
        case "-j": case "--jit": jit = true; break;
        case "-s": case "--scan": scanOnly = true; break;
        case "-m": case "--method":
            if (++i >= args.Length) { Error("--method requires a name"); return 1; }
            methodCall = args[i];
            runEntry = false;
            // Consume remaining non-flag args that look like literals (numbers/booleans
            // or quoted strings), stopping at the file path.
            while (i + 1 < args.Length
                   && !args[i + 1].StartsWith('-')
                   && !args[i + 1].EndsWith(".oil", StringComparison.OrdinalIgnoreCase)
                   && !args[i + 1].EndsWith(".orbt", StringComparison.OrdinalIgnoreCase)
                   && !args[i + 1].EndsWith(".oir", StringComparison.OrdinalIgnoreCase))
                methodArgs.Add(args[++i]);
            break;
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
    Console.WriteLine($"; Loaded {Path.GetFileName(filePath)} ({format}) — " +
        $"{module.Types.Count} types, {module.Types.Sum(t => t.MethodCount)} methods");

// ── Scan ──────────────────────────────────────────────────────────────
if (scanOnly)
{
    Console.Write(module.Dump(verbose));
    return 0;
}

// ── Configure Runtime ────────────────────────────────────────────────
if (emitDir is not null) Runtime.EmitDir = emitDir;
if (cacheDir is not null) Runtime.CacheDir = cacheDir;

var rt = new Runtime { Mode = jit ? JitMode.Reflection : JitMode.Interpreter };

// Scan for @DllImport / @NativeBinding annotations before loading.
rt.DllResolver.ScanModule(module, m => { if (verbose) Console.WriteLine($";   {m}"); });
rt.NativeResolver.ScanModule(module, m => { if (verbose) Console.WriteLine($";   {m}"); });

rt.LoadModule(module);

if (verbose)
    Console.WriteLine($"; Module loaded, executor: {(jit ? "ReflectionJit" : "Interpreter")}");

// ── Run ──────────────────────────────────────────────────────────────
try
{
    if (methodCall is not null)
    {
        var parsedArgs = methodArgs.Count > 0
            ? methodArgs.Select(a => (object?)ParseArg(a)).ToArray()
            : Array.Empty<object?>();

        if (verbose)
            Console.WriteLine($"; Calling {methodCall}({string.Join(", ", parsedArgs.Select(a => a is string s ? $"\"{s}\"" : a?.ToString() ?? "null"))})");

        var result = rt.CallMethod<object?>(methodCall, parsedArgs);
        Console.WriteLine(result is null ? "null" : result is int i ? i.ToString() : result.ToString());
    }
    else if (runEntry)
    {
        // Search for an entry point: any "*.Main" function, or "Main".
        string? entryName = null;
        foreach (var t in module.Types)
        {
            var name = $"{module.Resolve(t.NameIndex)}.Main";
            if (t.Methods.Any(m => module.Resolve(m.NameIndex) == "Main"))
                { entryName = name; break; }
        }

        if (entryName is null)
        {
            Error("No entry point (class with static method Main) found.");
            return 1;
        }

        if (verbose) Console.WriteLine($"; Entry: {entryName}");
        rt.CallMethod<object?>(entryName);
        if (verbose) Console.WriteLine("; Done.");
    }
}
catch (Exception e)
{
    Error($"Runtime error: {e.Message}");
    return 1;
}

return 0;

// ── Helpers ──────────────────────────────────────────────────────────

static object? ParseArg(string a) =>
    int.TryParse(a, out var i) ? i :
    a == "true" ? true :
    a == "false" ? false :
    a;

static void Help(string exe) => Console.WriteLine($"""
ObjectRT CLI — loads .oil / .orbt modules and runs them through the VM.

Usage: {exe} [options] <file>

Options:
  -v, --verbose       Show detailed output
  -j, --jit           Use the Roslyn JIT backend (ReflectionJit)
  -s, --scan          Dump module structure without executing
  -m, --method <name> Call a specific method (args follow)
  --emit  <dir>       Write generated C# to directory (JIT only)
  --cache <dir>       Cache compiled assemblies (JIT only)
  -h, --help          Show this message

Formats: .oil (text), .orbt (binary)

Examples:
  {exe} hello.oil                     Run entry point
  {exe} -j -v game.oil               JIT mode, verbose
  {exe} -m Calc.Add 3 4 math.oil     Call Calc.Add(3, 4)
  {exe} --jit --emit ./gen prog.oil  JIT + dump generated C#
  {exe} -m Kernel32.GetTickCount demo.oil   Call a @DllImport method
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
