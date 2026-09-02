using System.Collections.Generic;
using ObjectRT.Abstractions.GC;
using ObjectRT.VM.GC.Safepoint;
using ObjectRT.VM.Memory;

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
    public readonly List<byte[]?> Heap = new();

    /// <summary>VMHeap abstraction — free list, counters, accessors. Seam for V2 handle table.</summary>
    internal VMHeap VMHeap { get; }

    public HeapOptions HeapOptions { get; }

    internal SafepointCoordinator Coordinator { get; } = new();

    internal GC.MarkSweepGC GC { get; }
    public GCStats GCStats => GC.Stats;

    /// <summary>
    /// Heap handle → allocating type index. The VM's objects are plain byte
    /// buffers, so this side table is what lets instance calls refine their
    /// target through the RECEIVER's concrete type chain (virtual dispatch —
    /// base/interface-typed variables calling the most-derived override).
    /// </summary>
    public readonly Dictionary<uint, int> ObjectTypes = new();

    /// <summary>Records the type index a heap object was allocated as.</summary>
    public void RecordObjectType(uint handle, int typeIdx) => ObjectTypes[handle] = typeIdx;

    /// <summary>Looks up the allocating type index of a heap object.</summary>
    public bool TryGetObjectType(uint handle, out int typeIdx) => ObjectTypes.TryGetValue(handle, out typeIdx);

    /// <summary>Static field storage.</summary>
    public readonly Value[] StaticFields;

    // Interned string table (handles — the Value struct can't hold CLR refs).
    private readonly Dictionary<string, uint> _stringMap = new(StringComparer.Ordinal);
    private readonly List<string?> _strings = new();
    private readonly object _stringLock = new();

    public ExecutorState(CompiledModule mod) : this(mod, new HeapOptions()) { }

    public ExecutorState(CompiledModule mod, HeapOptions heapOptions) : this(mod, heapOptions, new GCOptions()) { }

    public ExecutorState(CompiledModule mod, HeapOptions heapOptions, GCOptions gcOptions)
    {
        HeapOptions = heapOptions;
        // Pre-size List capacity per InitialHeapCapacitySlots
        int cap = heapOptions.InitialHeapCapacitySlots > 0 ? heapOptions.InitialHeapCapacitySlots : 2048;
        Heap.Capacity = cap;
        VMHeap = new VMHeap(Heap);
        GC = new GC.MarkSweepGC(gcOptions);

        StaticFields = new Value[mod.Fields.Count];
        System.Array.Fill(StaticFields, Value.Nil());

        // The compiled module carries the ORBT string pool (type/method names,
        // and the string literals that `ldstr` indexes into). Those literal
        // handles are offsets into THIS pool, so seed the runtime string table
        // from it — otherwise a literal handle resolves against an empty list
        // and reads back null.
        foreach (var s in mod.Strings)
        {
            if (s == null) { _strings.Add(null); continue; }
            _stringMap.TryAdd(s, (uint)_strings.Count);
            _strings.Add(s);
        }
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

    /// <summary>
    /// Resolves a string handle to its CLR string. The string list is append-only
    /// after interning, so reads are safe without locking — the handle was valid
    /// at the time the Value was created and can only be looked up after that point.
    /// </summary>
    public string? GetStringValue(uint idx)
    {
        return idx < _strings.Count ? _strings[(int)idx] : null;
    }

    // ── External (CLR) object handles ──────────────────────────────
    // The Value.Obj tag carries a uint handle. Heap handles are indices into
    // Heap; external CLR objects (host bindings like List/Dict) use the high
    // bit so the two namespaces never collide.

    public const uint ExternalHandleFlag = 0x80000000;

    private readonly List<object?> _externals = new();
    private readonly object _externalLock = new();
    internal List<object?> ExternalsUnsafe => _externals;
    internal object ExternalsLock => _externalLock;
    internal int ExternalCountUnsafe => _externals.Count;

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
        return idx < _externals.Count ? _externals[(int)idx] : null;
    }

    /// <summary>True when an Obj-tag handle is an external CLR reference.</summary>
    public static bool IsExternal(uint handle) => (handle & ExternalHandleFlag) != 0;

    // ── VMHeap seam (V1 raw indices + null holes, V2 handle table) ──────────

    public byte[]? GetHeapBuffer(uint handle) => VMHeap.GetHeapBuffer(handle);
    public bool TryGetHeapBuffer(uint handle, out byte[]? buf) => VMHeap.TryGetBuffer(handle, out buf);
    public Span<byte> GetHeapSpan(uint handle) => VMHeap.GetHeapSpan(handle);

    public long AllocatedBytes => VMHeap.AllocatedBytes;
    public int HeapCapacitySlots => VMHeap.Capacity;
    public int FreeSlots => VMHeap.FreeSlots;

    public void ClearHeap()
    {
        VMHeap.Clear();
        ObjectTypes.Clear();
    }

    public bool CollectGC(GCReason reason = GCReason.Explicit) => GC.Collect(this, reason);
}
