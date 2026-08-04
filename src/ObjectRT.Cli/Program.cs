using ObjectRT.Abstractions;
using ObjectRT.Reader;
using ObjectRT.Runtime;

// â”€â”€ CLI â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
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

// â”€â”€ Load â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
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
    Console.Error.WriteLine($"; Loaded {Path.GetFileName(filePath)} ({format}) â€” " +
        $"{module.Types.Count} types, {module.Types.Sum(t => t.MethodCount)} methods");

// â”€â”€ Scan only â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
if (scanOnly) { Console.Write(module.Dump(verbose)); return 0; }

// â”€â”€ Compile to .orbt â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
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

// â”€â”€ Bundle: wrap .orbt in a dotnet executable â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
if (bundle)
{
    // Ensure we have .orbt bytes (compile .oil first if needed).
    var orbtBytes = format == FileFormat.ORBT
        ? File.ReadAllBytes(filePath)
        : new ORBTWriter().WriteModule(module);

    var spec = new BundleSpec
    {
        HostType = typeof(Runtime),
        HostInit = jit ? "rt.Mode = ObjectRT.Runtime.JitMode.Reflection;" : null,
        Rids = rids,
    };
    return BundleDriver.Bundle(exe, filePath, orbtBytes, outPath, spec, verbose);
}

// â”€â”€ Configure Runtime â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
if (emitDir is not null) Runtime.EmitDir = emitDir;
if (cacheDir is not null) Runtime.CacheDir = cacheDir;

var rt = new Runtime { Mode = jit ? JitMode.Reflection : JitMode.Interpreter };
rt.DllResolver.ScanModule(module, m => { if (verbose) Console.Error.WriteLine($";   {m}"); });
rt.NativeResolver.ScanModule(module, m => { if (verbose) Console.Error.WriteLine($";   {m}"); });
rt.LoadModule(module);

if (verbose)
    Console.Error.WriteLine($"; Module loaded, executor: {(jit ? "ReflectionJit" : "Interpreter")}");

// â”€â”€ Run â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
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

// â”€â”€ Helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

static object? ParseArg(string a) =>
    int.TryParse(a, out var i) ? i :
    a == "true" ? true :
    a == "false" ? false :
    a;

static void Help(string exe) => Console.Error.WriteLine($"""
ObjectRT CLI â€” load, compile, run, and bundle ObjectIL modules.

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
