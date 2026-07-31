using ObjectRT.Abstractions;

namespace ObjectRT.VM;

// ── Constants ───────────────────────────────────────────────────────────

public static class VmConstants
{
    /// <summary>Each Value (tagged union) stored as a field slot occupies this many bytes.</summary>
    public const uint FieldSlotSize = 16;
}

// ── VM-friendly type IDs ───────────────────────────────────────────────

public enum VMTypeKind : byte
{
    Class     = 0x01,
    Interface = 0x02,
    Struct    = 0x03,
    Enum      = 0x04,
}

// ── CompiledFunction — flat bytecode for one method ────────────────────

public class CompiledFunction
{
    public string DebugName { get; set; } = "";
    public byte[] Code { get; set; } = Array.Empty<byte>();
    public uint NumParams { get; set; }
    public uint NumLocals { get; set; }
    public uint MaxStack { get; set; }

    /// <summary>Offset ranges in the module string table that this function's code
    /// references via ldc/ldstr with inline uint16 indices.</summary>
    public uint StringStart { get; set; }
    public uint StringCount { get; set; }

    /// <summary>Index within the module's function table for fast dispatch.</summary>
    public uint SelfIndex { get; set; }
}

// ── VMType — minimal resolved type descriptor ──────────────────────────

public class VMType
{
    public string DebugName { get; set; } = "";
    public VMTypeKind Kind { get; set; } = VMTypeKind.Class;
    public int BaseType { get; set; } = -1;
    public uint FieldOffset { get; set; }
    public uint FieldCount { get; set; }
    public uint MethodOffset { get; set; }
    public uint MethodCount { get; set; }
    public uint InstanceSize { get; set; }
}

// ── VMField — field with resolved layout offset ───────────────────────

public class VMField
{
    public string DebugName { get; set; } = "";
    public uint TypeIndex { get; set; }
    public uint Offset { get; set; }
}

// ── CompiledModule — the whole thing, flat and cache-friendly ─────────

public class CompiledModule
{
    public List<VMType> Types { get; set; } = new();
    public List<VMField> Fields { get; set; } = new();
    public List<CompiledFunction> Functions { get; set; } = new();
    public List<string> Strings { get; set; } = new();

    public uint EntryFunction { get; set; } = uint.MaxValue;

    public Dictionary<string, uint> FunctionMap { get; set; } = new();

    public bool HasEntry => EntryFunction < Functions.Count;

    public uint FindFunction(string name) =>
        FunctionMap.TryGetValue(name, out var idx) ? idx
            : throw new KeyNotFoundException($"Function not found: {name}");

    public CompiledFunction GetFunction(uint idx) => Functions[(int)idx];
    public VMType GetType(uint idx) => Types[(int)idx];
    public string GetString(uint idx) => Strings[(int)idx];
}
