using ObjektRT.Core.Model;

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

    /// <summary>Bytecode offset → original-source mapping (from `// #line` comments).</summary>
    public List<SourceMapEntry> SourceMap { get; set; } = new();

    /// <summary>Index within the module's function table for fast dispatch.</summary>
    public uint SelfIndex { get; set; }

    /// <summary>
    /// Wire type name of each parameter ("int32", "string", "uint8", "Color",
    /// ...), in order. Used by interop marshalling so a native call can pack
    /// struct arguments and convert primitive widths.
    /// </summary>
    public string[]? ParamTypeNames { get; set; }

    /// <summary>Wire type name of the return type, for interop marshalling.</summary>
    public string? ReturnTypeName { get; set; }

    /// <summary>Source name of each parameter, in slot order. Null when the module carries no names.</summary>
    public string[]? ParamNames { get; set; }

    /// <summary>Source name of each local, in slot order. Null when the module carries no names.</summary>
    public string[]? LocalNames { get; set; }
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

    /// <summary>
    /// Wire type name of each field ("int32", "uint8", "Color", ...), in order.
    /// Used by interop marshalling to pack/unpack a struct object into the C
    /// layout expected by the P/Invoke bridge.
    /// </summary>
    public string[]? FieldTypeNames { get; set; }

    /// <summary>
    /// Names of the interfaces this type declares (<c>implements</c>).
    /// Virtual dispatch consults these: a receiver whose base chain misses
    /// the call's named type may still relate to it through an interface.
    /// </summary>
    public string[]? InterfaceNames { get; set; }
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

    /// <summary>Field qualified name ("Type.field") → flat field index (for static access).</summary>
    public Dictionary<string, uint> FieldMap { get; set; } = new();

    /// <summary>O(1) type-name → index map, built after Types are populated.</summary>
    private Dictionary<string, int>? _typeNameMap;

    /// <summary>Cached Delegate type index (-1 if not found). Built lazily on first access.</summary>
    private int _delegateTypeIdx = -2; // -2 = not yet looked up

    /// <summary>
    /// Per-type vtable: for each type index, a dictionary mapping method
    /// name → function index. Includes inherited and overridden methods,
    /// so virtual dispatch is a single O(1) dictionary lookup with zero
    /// allocations (no HashSet, no string concatenation, no type-chain walk).
    /// Built once at module load by <see cref="BuildVTables"/>.
    /// </summary>
    private Dictionary<int, Dictionary<string, uint>>? _vtables;

    public bool HasEntry => EntryFunction < Functions.Count;

    public uint FindFunction(string name) =>
        FunctionMap.TryGetValue(name, out var idx) ? idx
            : throw new KeyNotFoundException($"Function not found: {name}");

    public CompiledFunction GetFunction(uint idx) => Functions[(int)idx];
    public VMType GetType(uint idx) => Types[(int)idx];
    public string GetString(uint idx) => Strings[(int)idx];

    /// <summary>
    /// Builds the O(1) type-name → index dictionary. Call once after Types
    /// are fully populated (during ModuleCompiler.Compile).
    /// </summary>
    public void BuildTypeNameMap()
    {
        _typeNameMap = new Dictionary<string, int>(Types.Count, StringComparer.Ordinal);
        for (int i = 0; i < Types.Count; i++)
            _typeNameMap[Types[i].DebugName] = i;
    }

    /// <summary>
    /// Builds per-type vtables for O(1) virtual dispatch. Each type gets a
    /// dictionary mapping method names to function indices, with inherited
    /// methods filled in from the base chain (most-derived wins).
    /// </summary>
    public void BuildVTables()
    {
        _vtables = new Dictionary<int, Dictionary<string, uint>>(Types.Count);

        for (int ti = 0; ti < Types.Count; ti++)
        {
            var vtable = new Dictionary<string, uint>(StringComparer.Ordinal);

            // Walk the base chain from root to derived, so derived overrides
            // shadow inherited entries (last write wins).
            var chain = new List<int>();
            int cur = ti;
            var visited = new HashSet<int>();
            while (cur >= 0 && visited.Add(cur))
            {
                chain.Add(cur);
                cur = Types[cur].BaseType;
            }
            chain.Reverse(); // root first

            foreach (var typeIdx in chain)
            {
                var t = Types[typeIdx];
                for (uint mi = 0; mi < t.MethodCount; mi++)
                {
                    string mname = Functions[(int)(t.MethodOffset + mi)].DebugName;
                    // DebugName is "Type.Method" — extract just the method name
                    int dot = mname.LastIndexOf('.');
                    if (dot >= 0 && dot < mname.Length - 1)
                    {
                        string shortName = mname[(dot + 1)..];
                        vtable[shortName] = t.MethodOffset + mi;
                    }
                }
            }

            // Also add interface method implementations
            if (Types[ti].InterfaceNames != null)
            {
                // Walk the type's own chain to find interface implementations
                cur = ti;
                visited.Clear();
                while (cur >= 0 && visited.Add(cur))
                {
                    var t = Types[cur];
                    if (t.InterfaceNames != null)
                    {
                        foreach (var iname in t.InterfaceNames)
                        {
                            int ifaceIdx = FindTypeIndex(iname);
                            if (ifaceIdx < 0) continue;
                            var iface = Types[ifaceIdx];
                            for (uint mi = 0; mi < iface.MethodCount; mi++)
                            {
                                string mname = Functions[(int)(iface.MethodOffset + mi)].DebugName;
                                int dot = mname.LastIndexOf('.');
                                if (dot >= 0 && dot < mname.Length - 1)
                                {
                                    string shortName = mname[(dot + 1)..];
                                    // Only add if not already overridden in the type's own vtable
                                    // (the base-chain walk above already set concrete implementations)
                                }
                            }
                        }
                    }
                    cur = t.BaseType;
                }
            }

            _vtables[ti] = vtable;
        }
    }

    /// <summary>
    /// O(1) virtual dispatch: looks up a method in the receiver type's vtable.
    /// Returns the function index, or uint.MaxValue if not found.
    /// </summary>
    public uint ResolveVirtualMethod(int receiverTypeIdx, string methodName)
    {
        if (_vtables != null && _vtables.TryGetValue(receiverTypeIdx, out var vtable)
            && vtable.TryGetValue(methodName, out var funcIdx))
            return funcIdx;
        return uint.MaxValue;
    }

    /// <summary>
    /// Cached index of the built-in "Delegate" type. Returns -1 if not found.
    /// Avoids the O(n) linear scan on every delegate dispatch.
    /// </summary>
    public int DelegateTypeIdx
    {
        get
        {
            if (_delegateTypeIdx == -2)
            {
                _delegateTypeIdx = -1;
                for (int ti = 0; ti < Types.Count; ti++)
                {
                    if (Types[ti].DebugName == "Delegate") { _delegateTypeIdx = ti; break; }
                }
            }
            return _delegateTypeIdx;
        }
    }

    /// <summary>
    /// Finds a function by its qualified name ("Type.Method"), falling back to
    /// inheritance-aware resolution: when the named type does not declare the
    /// method itself, its base-type chain is walked most-derived first (so
    /// "Derived.Method" resolves to an inherited declaration, and an override
    /// on a derived type shadows the base one). Returns
    /// <see cref="uint.MaxValue"/> when nothing matches.
    /// </summary>
    public uint ResolveFunction(string name)
    {
        if (FunctionMap.TryGetValue(name, out var idx)) return idx;

        int dot = name.LastIndexOf('.');
        if (dot <= 0 || dot >= name.Length - 1) return uint.MaxValue;
        string typeName = name[..dot];
        string methodName = name[(dot + 1)..];

        int typeIdx = FindTypeIndex(typeName);
        int depthLimit = 64;
        while (typeIdx >= 0 && depthLimit-- > 0)
        {
            var type = Types[typeIdx];
            if (FunctionMap.TryGetValue($"{type.DebugName}.{methodName}", out idx)) return idx;
            typeIdx = type.BaseType;
        }
        return uint.MaxValue;
    }

    /// <summary>O(1) type-name → index lookup using the pre-built dictionary.</summary>
    private int FindTypeIndex(string name)
    {
        if (_typeNameMap != null)
            return _typeNameMap.TryGetValue(name, out var idx) ? idx : -1;
        // Fallback if BuildTypeNameMap was never called (shouldn't happen)
        for (int i = 0; i < Types.Count; i++)
            if (Types[i].DebugName == name) return i;
        return -1;
    }

    /// <summary>Public wrapper for tools that need type lookup by wire name.</summary>
    public int TryFindTypeIndex(string name) => FindTypeIndex(name);

    /// <summary>
    /// Index of the type with this debug (wire) name, or -1. Falls back to a
    /// last-segment match ("Color" finds "com.lib.Color").
    /// </summary>
    public int FindTypeIndexByName(string name)
    {
        int idx = FindTypeIndex(name);
        if (idx >= 0) return idx;
        int dot = name.LastIndexOf('.');
        if (dot > 0 && dot < name.Length - 1)
        {
            string shortName = name[(dot + 1)..];
            if (_typeNameMap != null)
            {
                foreach (var kv in _typeNameMap)
                    if (kv.Key == shortName || kv.Key.EndsWith("." + shortName, StringComparison.Ordinal))
                        return kv.Value;
            }
            else
            {
                for (int i = 0; i < Types.Count; i++)
                    if (Types[i].DebugName == shortName || Types[i].DebugName.EndsWith("." + shortName, StringComparison.Ordinal))
                        return i;
            }
        }
        return -1;
    }
}
