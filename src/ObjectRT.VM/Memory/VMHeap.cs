using ObjectRT.Abstractions.GC;

namespace ObjectRT.VM.Memory;

/// <summary>
/// V1 VM heap — slots with free-handle list, null holes.
/// Handle == index into _slots. Future V2 handle-table indirection will hide behind GetHeapBuffer.
/// Thread-safe via internal lock.
/// </summary>
internal sealed class VMHeap
{
    private readonly List<byte[]?> _slots;
    private readonly Stack<uint> _freeHandles = new();
    private long _allocatedBytes;
    private readonly object _lock = new();

    public VMHeap(int initialCapacitySlots = 2048)
    {
        _slots = new List<byte[]?>(initialCapacitySlots);
    }

    public VMHeap(HeapOptions opts)
    {
        var cap = opts.InitialHeapCapacitySlots > 0 ? opts.InitialHeapCapacitySlots : 2048;
        _slots = new List<byte[]?>(cap);
    }

    // Wrap existing list (for ExecutorState migration)
    public VMHeap(List<byte[]?> existing)
    {
        _slots = existing;
        // compute allocated
        long sum = 0;
        foreach (var b in _slots) if (b != null) sum += b.Length;
        _allocatedBytes = sum;
    }

    // — Capacity / stats —

    public int Capacity
    {
        get { lock (_lock) return _slots.Count; }
    }

    public long AllocatedBytes
    {
        get { lock (_lock) return _allocatedBytes; }
    }

    public int FreeSlots
    {
        get { lock (_lock) return _freeHandles.Count; }
    }

    public long HeapCapacityBytes
    {
        get { lock (_lock) return _allocatedBytes + _freeHandles.Count * 0; } // not used; keep for stats
    }

    // For GC sweeping — snapshot under lock externally
    public IReadOnlyList<byte[]?> RawSlots => _slots;

    // — Accessors — the seam for V2 handle table —

    public byte[]? GetHeapBuffer(uint handle)
    {
        lock (_lock)
        {
            if (handle >= (uint)_slots.Count) return null;
            return _slots[(int)handle];
        }
    }

    public bool TryGetBuffer(uint handle, out byte[]? buf)
    {
        lock (_lock)
        {
            if (handle >= (uint)_slots.Count) { buf = null; return false; }
            buf = _slots[(int)handle];
            return buf != null;
        }
    }

    public Span<byte> GetHeapSpan(uint handle)
    {
        var buf = GetHeapBuffer(handle);
        return buf == null ? Span<byte>.Empty : buf.AsSpan();
    }

    public void SetHeapBuffer(uint handle, byte[]? buf)
    {
        lock (_lock)
        {
            if (handle >= (uint)_slots.Count) return;
            var old = _slots[(int)handle];
            if (old != null) _allocatedBytes -= old.Length;
            if (buf != null) _allocatedBytes += buf.Length;
            _slots[(int)handle] = buf;
        }
    }

    // Helper for ldfld/stfld bounds check without exposing List
    public bool TryGetSpan(uint handle, out Span<byte> span, out int length)
    {
        lock (_lock)
        {
            if (handle >= (uint)_slots.Count) { span = Span<byte>.Empty; length = 0; return false; }
            var buf = _slots[(int)handle];
            if (buf == null) { span = Span<byte>.Empty; length = 0; return false; }
            span = buf.AsSpan();
            length = buf.Length;
            return true;
        }
    }

    // Non-locking fast path for GC mark phase when world is stopped (caller holds STW lock)
    // Use only when coordinator guarantees no concurrent mutation.
    internal byte[]? GetBufferUnsafe(uint handle)
    {
        if (handle >= (uint)_slots.Count) return null;
        return _slots[(int)handle];
    }

    // — Allocation —

    public uint Allocate(uint instanceSize)
    {
        lock (_lock)
        {
            byte[] data = new byte[instanceSize];
            if (_freeHandles.Count > 0)
            {
                uint handle = _freeHandles.Pop();
                // _slots[(int)handle] must be null (swept)
                _slots[(int)handle] = data;
                _allocatedBytes += data.Length;
                return handle;
            }
            uint h = (uint)_slots.Count;
            _slots.Add(data);
            _allocatedBytes += data.Length;
            return h;
        }
    }

    // Result-returning variant for future OOM path (kept for compat)
    public Result<uint> AllocateResult(uint instanceSize, out uint handle)
    {
        handle = Allocate(instanceSize);
        return handle;
    }

    // Used by sweep
    public void Free(uint handle)
    {
        lock (_lock)
        {
            if (handle >= (uint)_slots.Count) return;
            var buf = _slots[(int)handle];
            if (buf == null) return;
            _allocatedBytes -= buf.Length;
            _slots[(int)handle] = null;
            _freeHandles.Push(handle);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _slots.Clear();
            _freeHandles.Clear();
            _allocatedBytes = 0;
        }
    }

    // For stats
    public (int capacity, int free, long allocated) SnapshotStats()
    {
        lock (_lock) return (_slots.Count, _freeHandles.Count, _allocatedBytes);
    }

    // Unsafe snapshot for GC (world stopped) — no lock
    internal (int capacity, long allocated) SnapshotUnsafe() => (_slots.Count, _allocatedBytes);
    internal Stack<uint> FreeHandlesUnsafe => _freeHandles;
    internal List<byte[]?> SlotsUnsafe => _slots;
}
