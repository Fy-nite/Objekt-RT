using System.Collections.Concurrent;

namespace ObjectRT.VM.GC.Safepoint;

/// <summary>
/// Cooperative STW safepoint coordinator. One per ExecutorState, shared by all Interpreters
/// over that state. NativeAOT-safe: only Monitor + volatile + MRES.
/// PR2: stop/resume only, no collection. Invariants testable.
/// </summary>
internal sealed class SafepointCoordinator
{
    private readonly object _lock = new();
    private volatile bool _gcRequested;
    private int _gcEpoch;
    private readonly ManualResetEventSlim _gcDone = new(true);
    private int _parkedCount;

    // Live interpreters — weak? Use ConcurrentDictionary for lock-free register, but correctly
    // guarded by _lock for epoch checks to avoid race where Register happens after Request.
    private readonly ConcurrentDictionary<Interpreter, byte> _live = new();

    public bool IsGcRequested => _gcRequested;
    public int Epoch => _gcEpoch;

    public void Register(Interpreter interp)
    {
        _live[interp] = 0;
        // If GC already requested, park immediately before returning to caller.
        if (_gcRequested)
        {
            interp.EnterSafepointPark(this);
        }
    }

    public void Unregister(Interpreter interp)
    {
        _live.TryRemove(interp, out _);
        lock (_lock) { Monitor.PulseAll(_lock); }
    }

    public IReadOnlyCollection<Interpreter> LiveSnapshot() => _live.Keys.ToArray();

    public int LiveCount => _live.Count;

    // Only thread holding GcLock should call these
    public void RequestStop()
    {
        lock (_lock)
        {
            if (_gcRequested) return;
            _gcRequested = true;
            _gcEpoch++;
            _gcDone.Reset();
            _parkedCount = 0;
        }
    }

    public bool WaitForWorldStopped(int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        lock (_lock)
        {
            while (true)
            {
                int needPark = 0;
                int parkedOrNative = 0;
                foreach (var kv in _live)
                {
                    var ip = kv.Key;
                    if (!ip.IsExecuting) continue; // idle => GC-safe
                    needPark++;
                    if (ip.IsParked || ip.IsInNative) parkedOrNative++;
                }
                if (parkedOrNative >= needPark) return true;
                if (sw.ElapsedMilliseconds > timeoutMs) return false;
                Monitor.Wait(_lock, 50);
            }
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            _gcRequested = false;
            _parkedCount = 0;
            _gcDone.Set();
            Monitor.PulseAll(_lock);
        }
    }

    internal void OnInterpreterParked()
    {
        lock (_lock)
        {
            _parkedCount++;
            Monitor.PulseAll(_lock);
        }
    }

    internal void WaitUntilResumed()
    {
        _gcDone.Wait();
    }

    internal object SyncRoot => _lock;
}
