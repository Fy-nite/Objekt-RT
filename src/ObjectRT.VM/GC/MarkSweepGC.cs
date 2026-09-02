using System.Diagnostics;
using System.Runtime.InteropServices;
using ObjectRT.Abstractions.GC;
using ObjectRT.VM.GC.Safepoint;

namespace ObjectRT.VM.GC;

internal sealed class MarkSweepGC
{
    private readonly GCOptions _opts;
    public GCStats Stats { get; } = new();
    private long _nextThreshold;

    public MarkSweepGC(GCOptions? opts = null)
    {
        _opts = opts ?? new GCOptions();
        _nextThreshold = _opts.InitialThresholdBytes;
    }

    public MarkSweepGC(GCOptions opts, GCStats stats)
    {
        _opts = opts;
        Stats = stats;
        _nextThreshold = opts.InitialThresholdBytes;
    }

    public long NextThreshold => _nextThreshold;

    public bool ShouldCollect(long allocatedBytes) => allocatedBytes >= _nextThreshold;

    public bool Collect(ExecutorState state, GCReason reason)
    {
        var sw = Stopwatch.StartNew();
        // STW
        state.Coordinator.RequestStop();
        bool stopped = state.Coordinator.WaitForWorldStopped(5000);
        if (!stopped)
        {
            // Timeout — still try to resume to avoid deadlock, but record failure
            state.Coordinator.Resume();
            return false;
        }
        long reclaimedBytes = 0;
        int reclaimedSlots = 0;
        long liveBytes = 0;
        int liveSlots = 0;
        try
        {
            var heap = state.VMHeap;
            int capacity = heap.Capacity;
            // Early exit if no heap
            if (capacity == 0)
            {
                sw.Stop();
                UpdateStats(sw.Elapsed, 0, 0, 0, 0, reason, heap);
                return true;
            }

            bool[] marked = new bool[capacity];
            bool[] liveExternals = new bool[state.ExternalCountUnsafe];
            Stack<uint> work = new(capacity / 4 + 16);

            void MarkValue(Value v)
            {
                if (v.Tag != ValueTag.Obj) return;
                uint h = v.AsObj();
                if (ExecutorState.IsExternal(h))
                {
                    uint idx = h & ~ExecutorState.ExternalHandleFlag;
                    if (idx < (uint)liveExternals.Length) liveExternals[idx] = true;
                    return;
                }
                if (h >= (uint)marked.Length) return;
                var buf = heap.GetBufferUnsafe(h);
                if (buf == null) return;
                if (marked[h]) return;
                marked[h] = true;
                work.Push(h);
            }

            void ScavengeExternalsForHeapHandles()
            {
                // For each live external, if it's a container holding boxed heap handles, mark them
                for (int i = 0; i < liveExternals.Length; i++)
                {
                    if (!liveExternals[i]) continue;
                    var obj = state.ExternalsUnsafe[i];
                    if (obj == null) continue;
                    // object[] arrays (newarr)
                    if (obj is object[] arr)
                    {
                        foreach (var e in arr)
                        {
                            if (e is uint uh && !ExecutorState.IsExternal(uh))
                            {
                                if (uh < (uint)marked.Length && heap.GetBufferUnsafe(uh) != null && !marked[uh])
                                {
                                    marked[uh] = true;
                                    work.Push(uh);
                                }
                            }
                        }
                    }
                    else if (obj is System.Collections.Generic.List<object> list)
                    {
                        foreach (var e in list)
                        {
                            if (e is uint uh && !ExecutorState.IsExternal(uh))
                            {
                                if (uh < (uint)marked.Length && heap.GetBufferUnsafe(uh) != null && !marked[uh])
                                {
                                    marked[uh] = true;
                                    work.Push(uh);
                                }
                            }
                        }
                    }
                    else
                    {
                        // ThreadHandle or other host object holding a VM handle (e.g. ThreadHandle.DelegateHandle)
                        // Avoid compile-time dependency VM -> Runtime; extract via reflection if present
                        var t = obj.GetType();
                        var prop = t.GetProperty("DelegateHandle");
                        if (prop != null && prop.PropertyType == typeof(uint))
                        {
                            try
                            {
                                uint dh = (uint)prop.GetValue(obj)!;
                                if (!ExecutorState.IsExternal(dh) && dh < (uint)marked.Length && heap.GetBufferUnsafe(dh) != null && !marked[dh])
                                {
                                    marked[dh] = true;
                                    work.Push(dh);
                                }
                            }
                            catch { /* ignore */ }
                        }
                    }
                }
            }

            void Drain()
            {
                while (work.Count > 0)
                {
                    uint h = work.Pop();
                    var buf = heap.GetBufferUnsafe(h);
                    if (buf == null) continue;
                    // stride 16 over Value slots
                    for (int off = 0; off + 16 <= buf.Length; off += 16)
                    {
                        var fv = MemoryMarshal.Read<Value>(buf.AsSpan(off, 16));
                        if (fv.Tag != ValueTag.Obj) continue;
                        uint fh = fv.AsObj();
                        if (ExecutorState.IsExternal(fh))
                        {
                            uint idx = fh & ~ExecutorState.ExternalHandleFlag;
                            if (idx < (uint)liveExternals.Length) liveExternals[idx] = true;
                            continue;
                        }
                        if (fh >= (uint)marked.Length) continue;
                        if (heap.GetBufferUnsafe(fh) == null) continue;
                        if (marked[fh]) continue;
                        marked[fh] = true;
                        work.Push(fh);
                    }
                    // After pushing new externals, scavenge them if they contain handles?
                    // But scavenge needs liveExternals to be up to date; we will scavenge
                    // outer loop after drain stabilizes. For now, also scavenge if new external found
                    // we need to handle external containers that contain heap handles — those handles are discovered
                    // via MarkValue on heap fields that are external, but heap->external->heap via object[] requires extra pass.
                }
            }

            // — Roots —
            bool debug = Environment.GetEnvironmentVariable("ORTRT_GC_DEBUG") == "1";
            if (debug) Console.Error.WriteLine($"; GC roots: statics={state.StaticFields.Length} liveIps={state.Coordinator.LiveCount}");
            foreach (var v in state.StaticFields) { if(debug) Console.Error.WriteLine($";  static {v}"); MarkValue(v); }
            foreach (var interp in state.Coordinator.LiveSnapshot())
            {
                if(debug) Console.Error.WriteLine($";  ip IsExec={interp.IsExecuting} IsParked={interp.IsParked} IsInNative={interp.IsInNative} stack={interp.StackForGC.Count} frames={interp.FramesForGC.Count}");
                foreach (var v in interp.StackForGC) { if(debug) Console.Error.WriteLine($";   stack {v}"); MarkValue(v); }
                foreach (var fr in interp.FramesForGC) foreach (var v in fr.Locals) { if(debug) Console.Error.WriteLine($";   locals {v}"); MarkValue(v); }
                foreach (var ef in interp.ExceptionHandlersForGC) { if(debug) Console.Error.WriteLine($";   pending {ef.PendingException}"); MarkValue(ef.PendingException); }
                // DirectStack window — conservative scan all 256 when InNative
                if (interp.IsInNative)
                {
                    var ds = interp.DirectStackForGC;
                    for (int i = 0; i < ds.Length; i++) { if(debug && ds[i].Tag==ValueTag.Obj) Console.Error.WriteLine($";   direct {ds[i]}"); MarkValue(ds[i]); }
                }
            }

            // Initial drain to discover heap->external and heap->heap
            Drain();
            // Scavenge live externals for heap handles they box, then drain again until fixpoint
            // Iterate because scavenge may discover new heap objects that contain new externals
            bool changed;
            do
            {
                int before = work.Count;
                ScavengeExternalsForHeapHandles();
                Drain();
                changed = work.Count != before;
            } while (changed);

            // Count live bytes
            for (int i = 0; i < marked.Length; i++) if (marked[i])
            {
                var buf = heap.GetBufferUnsafe((uint)i);
                if (buf != null) { liveBytes += buf.Length; liveSlots++; }
            }

            // Sweep heap
            for (int i = 0; i < marked.Length; i++)
            {
                if (marked[i]) continue;
                var buf = heap.GetBufferUnsafe((uint)i);
                if (buf == null) continue;
                reclaimedBytes += buf.Length;
                reclaimedSlots++;
                heap.Free((uint)i);
                state.ObjectTypes.Remove((uint)i);
            }

            // Sweep externals — null dead slots but keep indices stable (handle = Flag|idx)
            // Dead external slot stays as null hole; handle to it becomes invalid (GetExternal returns null)
            for (int i = 0; i < liveExternals.Length; i++)
            {
                if (!liveExternals[i] && state.ExternalsUnsafe[i] != null)
                {
                    // Only null if not a host-global? All externals are scavenge-determined;
                    // host globals are not in this list (they are in InterfaceHostResolver), so safe to null.
                    // Keep slot as null to allow CLR GC.
                    state.ExternalsUnsafe[i] = null;
                }
            }

            sw.Stop();
            // Adaptive threshold
            long next = (long)(liveBytes * _opts.GrowthFactor);
            long minNext = liveBytes + _opts.MinHeadroomBytes;
            if (next < minNext) next = minNext;
            if (next < _opts.InitialThresholdBytes) next = _opts.InitialThresholdBytes;
            // clamp to MaximumHeapSize if capped
            // Note: MaximumHeapSize is in HeapOptions, not GCOptions — clamp externally via Runtime if needed
            _nextThreshold = next;

            UpdateStats(sw.Elapsed, reclaimedBytes, reclaimedSlots, liveBytes, liveSlots, reason, heap);
            return true;
        }
        finally
        {
            if (sw.IsRunning) sw.Stop();
            state.Coordinator.Resume();
        }
    }

    private void UpdateStats(TimeSpan pause, long reclaimedBytes, int reclaimedSlots, long liveBytes, int liveSlots, GCReason reason, Memory.VMHeap heap)
    {
        var (cap, free, allocated) = heap.SnapshotStats();
        Stats.CollectionCount++;
        Stats.TotalPause += pause;
        Stats.LastPause = pause;
        Stats.ReclaimedBytesLast = reclaimedBytes;
        Stats.ReclaimedSlotsLast = reclaimedSlots;
        Stats.LiveBytes = liveBytes;
        Stats.AllocatedBytes = allocated;
        Stats.HeapCapacitySlots = cap;
        Stats.FreeSlots = free;
        Stats.HeapCapacityBytes = allocated; // capacity bytes ~ allocated (no compaction)
        Stats.LastReason = reason;
        Stats.LastCollectionUtc = DateTime.UtcNow;
    }
}
