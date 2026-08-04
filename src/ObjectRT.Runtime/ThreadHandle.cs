using System;

namespace ObjectRT.Runtime;

/// <summary>
/// A handle to a script-visible thread: wraps a VM delegate handle (the work
/// to run) together with the OS thread that runs it. Created via the
/// <c>Thread.Create</c> native, controlled via <c>Thread.Start</c>,
/// <c>Thread.Join</c>, and <c>Thread.IsAlive</c> — a C#-style lifecycle where
/// a thread is a value you can store, pass around, and start explicitly.
/// The delegate runs on a fresh interpreter sharing the module state, so
/// closures captured by the delegate are valid on the new thread.
/// </summary>
public sealed class ThreadHandle
{
    internal ThreadHandle(uint delegateHandle)
    {
        DelegateHandle = delegateHandle;
    }

    /// <summary>The VM heap handle of the delegate this thread runs.</summary>
    public uint DelegateHandle { get; }

    /// <summary>The OS thread, once started (null until <c>Thread.Start</c>).</summary>
    public System.Threading.Thread? OsThread { get; private set; }

    /// <summary>True once the thread has been started.</summary>
    public bool Started => OsThread != null;

    /// <summary>True while the thread is running (false before start / after it finishes).</summary>
    public bool IsAlive => OsThread?.IsAlive ?? false;

    /// <summary>Blocks the calling thread until this thread finishes.</summary>
    public void Join()
    {
        if (OsThread == null)
            throw new InvalidOperationException("Thread has not been started.");
        OsThread.Join();
    }

    /// <summary>Creates the background OS thread and runs <paramref name="body"/> on it.</summary>
    internal void Launch(Action body)
    {
        OsThread = new System.Threading.Thread(new System.Threading.ThreadStart(body))
        {
            IsBackground = true
        };
        OsThread.Start();
    }
}
