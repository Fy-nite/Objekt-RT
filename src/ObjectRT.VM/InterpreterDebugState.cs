using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ObjektRT.Core.Model;

namespace ObjectRT.VM;

/// <summary>Debug stepping mode.</summary>
public enum StepMode
{
    None,
    StepIn,
    StepOver,
    StepOut
}

/// <summary>
/// A breakpoint: file path + source line number.
/// The DAP server resolves these to bytecode offsets via source maps.
/// </summary>
public class Breakpoint
{
    public string File { get; set; } = "";
    public int Line { get; set; }
    public int Column { get; set; }
    public bool Verified { get; set; }
    public uint? BytecodeOffset { get; set; }
    public string? FunctionName { get; set; }
}

/// <summary>
/// Snapshot of the current execution state, sent to the DAP client on pause/breakpoint hit.
/// </summary>
public class DebugPauseEventArgs : EventArgs
{
    public string Reason { get; set; } = "";         // "breakpoint", "step", "pause", "entry"
    public string File { get; set; } = "";
    public int Line { get; set; }
    public int Column { get; set; }
    public List<DebugFrame> Frames { get; set; } = new();
}

/// <summary>
/// A single frame in the debug call stack.
/// </summary>
public class DebugFrame
{
    public string Name { get; set; } = "";
    public string File { get; set; } = "";
    public int Line { get; set; }
    public int Column { get; set; }
    public uint FrameIndex { get; set; }
    public CompiledFunction Func { get; set; } = null!;
    public Value[] Locals { get; set; } = Array.Empty<Value>();
    public int ArgCount { get; set; }
}

/// <summary>
/// Mutable debug state shared between the interpreter and the DAP server.
/// The interpreter checks this on every instruction; the DAP server mutates it
/// from its message loop thread (all mutations are via lock).
/// </summary>
public class InterpreterDebugState
{
    private readonly object _lock = new();
    private volatile bool _justResumed;   // skip one check after resume to avoid re-pausing

    // ── Breakpoints ────────────────────────────────────────────────

    /// <summary>Breakpoints by normalized file path.</summary>
    private readonly Dictionary<string, List<Breakpoint>> _breakpoints = new();

    /// <summary>Set breakpoints for a file. Replaces any existing breakpoints for that file.</summary>
    public void SetBreakpoints(string file, List<Breakpoint> bps)
    {
        lock (_lock)
            _breakpoints[file] = bps;
    }

    /// <summary>Clear all breakpoints.</summary>
    public void ClearBreakpoints()
    {
        lock (_lock)
            _breakpoints.Clear();
    }

    /// <summary>
    /// Checks whether the current PC hits a breakpoint.
    /// Must be called from the interpreter thread.
    /// </summary>
    public bool IsBreakpointHit(string functionName, uint pc, out string? file, out int line)
    {
        lock (_lock)
        {
            file = null;
            line = 0;
            foreach (var kvp in _breakpoints)
            {
                foreach (var bp in kvp.Value)
                {
                    if (bp.Verified && bp.FunctionName == functionName && bp.BytecodeOffset == pc)
                    {
                        file = kvp.Key;
                        line = bp.Line;
                        return true;
                    }
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Resolves breakpoints against source maps. Called by the DAP server after
    /// setBreakpoints, and by the interpreter on first hit detection.
    /// </summary>
    public void ResolveBreakpoints(CompiledModule mod)
    {
        lock (_lock)
        {
            foreach (var kvp in _breakpoints)
            {
                foreach (var bp in kvp.Value)
                {
                    bp.Verified = false;
                    bp.BytecodeOffset = null;
                    bp.FunctionName = null;

                    // Find the function by scanning all functions' source maps
                    foreach (var func in mod.Functions)
                    {
                        if (func.SourceMap == null) continue;
                        foreach (var entry in func.SourceMap)
                        {
                            if (entry.Line == bp.Line)
                            {
                                // Check if this source map's line matches the breakpoint file
                                // (We match by line number since source maps don't store file paths)
                                bp.BytecodeOffset = entry.Offset;
                                bp.FunctionName = func.DebugName;
                                bp.Verified = true;
                                break;
                            }
                        }
                        if (bp.Verified) break;
                    }
                }
            }
        }
    }

    // ── Stepping ───────────────────────────────────────────────────

    private StepMode _stepMode = StepMode.None;
    private int _stepTargetDepth;
    private uint _stepTargetRetPc;

    public StepMode StepMode
    {
        get { lock (_lock) return _stepMode; }
        set { lock (_lock) _stepMode = value; }
    }

    public int StepTargetDepth
    {
        get { lock (_lock) return _stepTargetDepth; }
        set { lock (_lock) _stepTargetDepth = value; }
    }

    public uint StepTargetRetPc
    {
        get { lock (_lock) return _stepTargetRetPc; }
        set { lock (_lock) _stepTargetRetPc = value; }
    }

    /// <summary>
    /// Returns true when stepping should pause at this point.
    /// Called from the interpreter after each instruction; pauses on source-line
    /// change rather than per instruction, so a step advances a whole statement.
    /// </summary>
    public bool ShouldStepPause(int currentDepth, int currentLine)
    {
        lock (_lock)
        {
            return _stepMode switch
            {
                StepMode.StepIn => currentLine != LastPausedLine,
                StepMode.StepOver => currentDepth <= _stepTargetDepth && currentLine != LastPausedLine,
                StepMode.StepOut => currentDepth < _stepTargetDepth,
                _ => false
            };
        }
    }

    /// <summary>Configure step-over: pause when we return to the same or shallower depth.</summary>
    public void ConfigureStepOver(int currentDepth)
    {
        lock (_lock)
        {
            _stepMode = StepMode.StepOver;
            _stepTargetDepth = currentDepth;
        }
    }

    /// <summary>Configure step-out: pause when execution returns above the
    /// depth we are currently paused at.</summary>
    public void ConfigureStepOut(int currentDepth)
    {
        lock (_lock)
        {
            _stepMode = StepMode.StepOut;
            _stepTargetDepth = currentDepth;
        }
    }

    /// <summary>Configure step-in: always pause on next instruction.</summary>
    public void ConfigureStepIn()
    {
        lock (_lock)
        {
            _stepMode = StepMode.StepIn;
        }
    }

    /// <summary>Clear stepping (resume normal execution).</summary>
    public void ClearStep()
    {
        lock (_lock)
        {
            _stepMode = StepMode.None;
        }
    }

    // ── Pause/Resume ───────────────────────────────────────────────

    private readonly ManualResetEventSlim _resumeEvent = new(true); // starts signaled (not paused)
    private volatile bool _pauseRequested;

    /// <summary>Source line of the most recent pause; stepping compares against it.</summary>
    public int LastPausedLine { get; private set; }

    /// <summary>True when the interpreter is currently paused.</summary>
    public bool IsPaused => !_resumeEvent.IsSet;

    /// <summary>
    /// Called by the DAP server to request the interpreter to pause at the
    /// next safe point (before the next instruction).
    /// </summary>
    public void RequestPause()
    {
        _pauseRequested = true;
    }

    /// <summary>
    /// Called by the interpreter to check if it should pause before executing.
    /// If a pause is requested, blocks until the DAP server signals resume.
    /// </summary>
    /// <returns>True if execution should stop (pause point reached), false to continue.</returns>
    public bool CheckPause(string functionName, uint pc, int frameDepth, CompiledModule mod)
    {
        int curLine = ResolveLineFromSourceMap(functionName, pc, mod);

        // After resuming, skip the breakpoint check once so a breakpoint on the
        // current instruction doesn't re-fire immediately. Stepping checks stay
        // active — they are line/depth based and must not lose their landing spot.
        bool skipBreakpointOnce = false;
        if (_justResumed)
        {
            _justResumed = false;
            skipBreakpointOnce = true;
        }

        // Check if a pause was requested (Ctrl+C or pause button)
        if (_pauseRequested)
        {
            _pauseRequested = false;
            _resumeEvent.Reset();
            LastPausedLine = curLine;
            OnPause?.Invoke(this, new DebugPauseEventArgs
            {
                Reason = "pause",
                File = ResolveFileFromSourceMap(functionName, pc, mod),
                Line = curLine,
            });
            _resumeEvent.Wait();
            return true;
        }

        // Check breakpoints
        if (!skipBreakpointOnce && IsBreakpointHit(functionName, pc, out var file, out int line))
        {
            _resumeEvent.Reset();
            LastPausedLine = line;
            OnPause?.Invoke(this, new DebugPauseEventArgs
            {
                Reason = "breakpoint",
                File = file ?? "",
                Line = line,
            });
            _resumeEvent.Wait();
            return true;
        }

        // Check stepping
        if (ShouldStepPause(frameDepth, curLine))
        {
            _resumeEvent.Reset();
            ClearStep();
            LastPausedLine = curLine;
            OnPause?.Invoke(this, new DebugPauseEventArgs
            {
                Reason = "step",
                File = ResolveFileFromSourceMap(functionName, pc, mod),
                Line = curLine,
            });
            _resumeEvent.Wait();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Called by the DAP server to resume execution after a pause.
    /// </summary>
    public void Resume(StepMode? stepMode = null)
    {
        if (stepMode.HasValue)
        {
            switch (stepMode.Value)
            {
                case StepMode.StepIn: ConfigureStepIn(); break;
                case StepMode.StepOver: ConfigureStepOver(0); break;
                case StepMode.StepOut: ConfigureStepOut(0); break;
            }
        }
        _justResumed = true;
        _resumeEvent.Set();
    }

    // ── Events ─────────────────────────────────────────────────────

    /// <summary>Raised when the interpreter hits a pause point (breakpoint, step, or pause request).</summary>
    public event EventHandler<DebugPauseEventArgs>? OnPause;

    // ── Helpers ────────────────────────────────────────────────────

    private static string ResolveFileFromSourceMap(string functionName, uint pc, CompiledModule mod)
    {
        foreach (var func in mod.Functions)
        {
            if (func.DebugName != functionName) continue;
            if (func.SourceMap == null) continue;
            SourceMapEntry? best = null;
            foreach (var e in func.SourceMap)
            {
                if (e.Offset <= pc) best = e;
                else break;
            }
            if (best != null && !string.IsNullOrEmpty(best.Text) && File.Exists(best.Text)) return best.Text;
        }
        return "";
    }

    private static int ResolveLineFromSourceMap(string functionName, uint pc, CompiledModule mod)
    {
        foreach (var func in mod.Functions)
        {
            if (func.DebugName != functionName) continue;
            if (func.SourceMap == null) continue;
            SourceMapEntry? best = null;
            foreach (var e in func.SourceMap)
            {
                if (e.Offset <= pc) best = e;
                else break;
            }
            if (best != null) return best.Line;
        }
        return 0;
    }

    /// <summary>
    /// Builds a list of debug frames from the interpreter's frame stack.
    /// </summary>
    public static List<DebugFrame> BuildFrames(IReadOnlyList<Frame> frames, uint currentPc, CompiledModule mod)
    {
        var result = new List<DebugFrame>(frames.Count);
        for (int i = frames.Count - 1; i >= 0; i--)
        {
            var f = frames[i];
            var pc = i == frames.Count - 1 ? currentPc : f.RetPc;
            string file = "";
            int line = 0;

            if (f.Func.SourceMap != null)
            {
                SourceMapEntry? best = null;
                foreach (var e in f.Func.SourceMap)
                {
                    if (e.Offset <= pc) best = e;
                    else break;
                }
                if (best != null)
                {
                    line = best.Line;
                    if (!string.IsNullOrEmpty(best.Text) && File.Exists(best.Text)) file = best.Text;
                }
            }

            result.Add(new DebugFrame
            {
                Name = f.Func.DebugName,
                File = file,
                Line = line,
                FrameIndex = (uint)i,
                Func = f.Func,
                Locals = f.Locals,
                ArgCount = (int)f.Func.NumParams
            });
        }
        return result;
    }
}
