using System.Collections.Generic;

namespace ObjectRT.VM;

/// <summary>
/// The mutable module state shared by all executors: the object heap, static
/// fields, and the interned string table. Executors (one per thread) own their
/// own call stacks (<see cref="Interpreter"/>'s _stack/_frames), but share this
/// state so object handles (heap indices) created on one thread are valid on
/// another — that's what makes Thread.Spawn(delegate) work: the delegate and its
/// closure live in the shared heap, and the spawned thread reads them there.
///
/// Thread-safety: the string table is locked (concurrent interning would
/// corrupt the dictionary). Heap allocation is NOT locked — v1 threading is
/// fire-and-forget and threads typically touch their own closures; sharing a
/// mutable object across threads is the programmer's responsibility, like C#.
/// </summary>
public sealed class ExecutorState
{
    /// <summary>Heap — each object is a byte buffer sized by the type's instance_size.</summary>
    public readonly List<byte[]> Heap = new();

    /// <summary>Static field storage.</summary>
    public readonly Value[] StaticFields;

    // Interned string table (handles — the Value struct can't hold CLR refs).
    private readonly Dictionary<string, uint> _stringMap = new(StringComparer.Ordinal);
    private readonly List<string?> _strings = new();
    private readonly object _stringLock = new();

    public ExecutorState(CompiledModule mod)
    {
        StaticFields = new Value[mod.Fields.Count];
        System.Array.Fill(StaticFields, Value.Nil());
    }

    public uint InternString(string s)
    {
        lock (_stringLock)
        {
            if (_stringMap.TryGetValue(s, out var idx)) return idx;
            idx = (uint)_strings.Count;
            _strings.Add(s);
            _stringMap[s] = idx;
            return idx;
        }
    }

    public string? GetStringValue(uint idx)
    {
        lock (_stringLock)
        {
            return idx < _strings.Count ? _strings[(int)idx] : null;
        }
    }

    // ── External (CLR) object handles ──────────────────────────────
    // The Value.Obj tag carries a uint handle. Heap handles are indices into
    // Heap; external CLR objects (host bindings like List/Dict) use the high
    // bit so the two namespaces never collide.

    public const uint ExternalHandleFlag = 0x80000000;

    private readonly List<object?> _externals = new();
    private readonly object _externalLock = new();

    /// <summary>Interns a CLR object reference and returns its external handle.</summary>
    public uint InternExternal(object? obj)
    {
        lock (_externalLock)
        {
            uint idx = (uint)_externals.Count;
            _externals.Add(obj);
            return ExternalHandleFlag | idx;
        }
    }

    /// <summary>Resolves an external handle to its CLR object, or null.</summary>
    public object? GetExternal(uint handle)
    {
        uint idx = handle & ~ExternalHandleFlag;
        lock (_externalLock)
        {
            return idx < _externals.Count ? _externals[(int)idx] : null;
        }
    }

    /// <summary>True when an Obj-tag handle is an external CLR reference.</summary>
    public static bool IsExternal(uint handle) => (handle & ExternalHandleFlag) != 0;
}
