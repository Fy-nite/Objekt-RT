using ObjectRT.Abstractions;
using ObjectRT.Reader;
using ObjectRT.VM;

var programName = Environment.GetCommandLineArgs()[0];
bool verbose = false;
bool runVm = false;
bool trace = false;
string? filePath = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-h":
        case "--help":
            PrintHelp(programName);
            return 0;
        case "-v":
        case "--verbose":
            verbose = true;
            break;
        case "-r":
        case "--run":
            runVm = true;
            break;
        case "-t":
        case "--trace":
            trace = true;
            runVm = true;
            break;
        default:
            if (args[i].StartsWith('-'))
            {
                Console.Error.WriteLine($"Unknown option: {args[i]}");
                PrintHelp(programName);
                return 1;
            }
            filePath = args[i];
            break;
    }
}

if (filePath == null)
{
    Console.Error.WriteLine("Error: No input file specified.");
    PrintHelp(programName);
    return 1;
}

// Detect format
var format = DetectFormat(filePath);
if (format == FileFormat.Unknown)
{
    Console.Error.WriteLine($"Error: Unrecognized file format for '{filePath}'.");
    return 1;
}

// Read the module
ORBTModule? module = null;
try
{
    if (format == FileFormat.ORBT)
    {
        Console.WriteLine($"; Reading ORBT binary: {filePath}\n");
        module = OrbtFileReader.ReadFile(filePath);
    }
    else
    {
        Console.WriteLine($"; Reading ObjectIL text: {filePath}\n");
        module = OilFileReader.ParseFile(filePath);
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error reading '{filePath}': {ex.Message}");
    return 1;
}

if (!runVm)
{
    Console.Write(module.Dump(verbose));
    return 0;
}

// Compile and execute
Console.WriteLine("; Compiling to VM bytecode...\n");
var compileResult = VmCompiler.Compile(module);
if (compileResult.IsError)
{
    Console.Error.WriteLine($"Compilation error: {compileResult.Error}");
    return 1;
}
var compiled = compileResult.Value;

Console.WriteLine($"; Compiled module: {compiled.Functions.Count} functions, " +
                  $"{compiled.Types.Count} types, {compiled.Strings.Count} strings");
if (compiled.HasEntry)
{
    Console.WriteLine($"; Entry point: {compiled.GetFunction(compiled.EntryFunction).DebugName} " +
                      $"[{compiled.GetFunction(compiled.EntryFunction).Code.Length} bytes]");
}

if (verbose)
{
    foreach (var func in compiled.Functions)
    {
        Console.WriteLine($";   {func.DebugName}: {func.Code.Length} bytes, " +
                          $"{func.NumParams} params, {func.NumLocals} locals, max_stack={func.MaxStack}");
    }
}

Console.WriteLine("\n; Executing...");
var vm = new Interpreter(compiled);
vm.Trace = trace;

var runResult = vm.Run();
if (runResult.IsError)
{
    Console.Error.WriteLine($"Runtime error: {runResult.Error}");
    return 1;
}

var result = runResult.Value;
Console.WriteLine($"; Execution complete — result: {result}");
return 0;

// ── Helper functions ──────────────────────────────────────────────────

static void PrintHelp(string name)
{
    Console.WriteLine($"""
ObjectRT Module Reader & VM
Reads ObjectIL (.oil) and ORBT (.orbt) module files.
Can compile to VM-friendly bytecode and execute.

Usage: {Path.GetFileName(name)} [options] <file>

Options:
  -v, --verbose     Show detailed output (string pool, all instructions)
  -r, --run         Compile and execute via the flat-bytecode VM
  -t, --trace       Run with per-instruction trace
  -h, --help        Show this help message

Supported formats:
  .oil   ObjectIL text format
  .orbt  ORBT binary format
""");
}

static FileFormat DetectFormat(string path)
{
    // Try reading first 4 bytes for ORBT magic
    try
    {
        using var fs = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[4];
        if (fs.Read(magic) >= 4)
        {
            if (magic[0] == 'O' && magic[1] == 'R' && magic[2] == 'B' && magic[3] == 'T')
                return FileFormat.ORBT;
        }

        // Check extension
        if (path.EndsWith(".oil", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".oir", StringComparison.OrdinalIgnoreCase))
            return FileFormat.ObjectIL;

        if (path.EndsWith(".orbt", StringComparison.OrdinalIgnoreCase))
            return FileFormat.ORBT;

        // Check for "module" text
        fs.Seek(0, SeekOrigin.Begin);
        Span<byte> head = stackalloc byte[6];
        if (fs.Read(head) >= 6)
        {
            if (head[0] == 'm' && head[1] == 'o' && head[2] == 'd'
                && head[3] == 'u' && head[4] == 'l' && head[5] == 'e')
                return FileFormat.ObjectIL;
        }
    }
    catch
    {
        // Fall through
    }

    return FileFormat.Unknown;
}

enum FileFormat { Unknown, ObjectIL, ORBT }
