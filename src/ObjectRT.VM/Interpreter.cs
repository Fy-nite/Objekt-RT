using System.Buffers;
using System.Runtime.InteropServices;
using ObjektRT.Core.Model;

namespace ObjectRT.VM;

public class Frame
{
    public CompiledFunction Func { get; set; } = null!;
    public uint Pc;
    public Value[] Locals = [];
    public uint StackBase;
    public uint RetPc;
    public uint RetFunc;

    /// <summary>
    /// When true, Locals was rented from ArrayPool and must be returned on Ret.
    /// False for frames that use the shared _localsScratch buffer.
    /// </summary>
    public bool LocalsRented;
}

/// <summary>
/// Exception handler context pushed when entering a try block.
/// </summary>
internal sealed class ExceptionFrame
{
    /// <summary>Catch handler bytecodes (from CatchRecord.Body).</summary>
    public byte[][] CatchBodies = [];
    /// <summary>Exception type indices matching each catch body.</summary>
    public ushort[] CatchTypeIndices = [];
    /// <summary>Finally block bytecode, null if none.</summary>
    public byte[]? FinallyBody;
    /// <summary>Which block of this handler is currently executing.</summary>
    public HandlerPhase Phase = HandlerPhase.TryBody;
    /// <summary>After the finally runs, the pending exception must propagate outward.</summary>
    public bool PendingRethrow;
    /// <summary>The exception value to re-throw after a finally completes.</summary>
    public Value PendingException;
    /// <summary>Stack depth to restore to when entering a catch handler.</summary>
    public int StackBase;
    /// <summary>Outer code array to resume after the handler completes.</summary>
    public byte[] OuterCode = [];
    /// <summary>Outer PC to resume at after the handler completes.</summary>
    public uint OuterPc;
}

internal enum HandlerPhase
{
    TryBody,
    CatchBody,
    FinallyBody,
}

/// <summary>
/// Stack-based bytecode interpreter. Extends <see cref="ExecutorBase"/> for
/// shared heap/statics/strings/native-handler state.
/// </summary>
public sealed class Interpreter : ExecutorBase
{
    private readonly List<Value> _stack = new(4096);
    private readonly List<Frame> _frames = new(256);
    private Value[] _localsScratch = Array.Empty<Value>();
    private string _currentFuncName = "";
    private uint _currentPc;
    private bool _trace;
    private long _maxSteps;          // 0 = unlimited
    private long _stepsExecuted;
    private readonly bool _traceEnvEnabled;  // read once, not per-instruction

    // ── Exception handling ──────────────────────────────────────────
    private readonly Stack<ExceptionFrame> _exceptionHandlers = new();

    public Interpreter(CompiledModule mod) : base(mod) { _traceEnvEnabled = Environment.GetEnvironmentVariable("ORTRT_TRACE") == "1"; }
    public Interpreter(CompiledModule mod, ExecutorState? shared) : base(mod, shared) { _traceEnvEnabled = Environment.GetEnvironmentVariable("ORTRT_TRACE") == "1"; }

    public bool Trace { get => _trace; set => _trace = value; }

    /// <summary>
    /// Optional instruction budget for this interpreter. When non-zero,
    /// execution aborts with <see cref="VmErrorKind.StepBudgetExceeded"/> once
    /// more than <see cref="MaxSteps"/> bytecode instructions have been
    /// dispatched within a single top-level call (<c>RunFunction</c>). 0 (the
    /// default) means unlimited and adds no overhead to the dispatch loop.
    /// Useful for sandboxing untrusted content scripts.
    /// </summary>
    public long MaxSteps
    {
        get => _maxSteps;
        set => _maxSteps = Math.Max(0, value);
    }

    /// <summary>True when a VM function is currently executing on this interpreter.</summary>
    public bool IsExecuting => _frames.Count > 0;

    /// <summary>Debug state for breakpoints, stepping, and pause/resume. Null when not debugging.</summary>
    public InterpreterDebugState? DebugState { get; set; }

    /// <summary>Get the current frame stack (for debug inspection). Read-only snapshot.</summary>
    public IReadOnlyList<Frame> Frames => _frames;

    /// <summary>Get the current PC (for debug inspection).</summary>
    public uint CurrentPc => _currentPc;

    public override void Reset(bool clearHeap = false, bool clearStatics = false)
    {
        _stack.Clear();
        _frames.Clear();
        _currentFuncName = "";
        _exceptionHandlers.Clear();
        if (clearHeap) Heap.Clear();
        if (clearStatics) Array.Fill(StaticFields, Value.Nil());
    }

    public override Result<Value> RunFunction(uint funcIdx, Value[] args)
    {
        if (funcIdx >= Mod.Functions.Count)
            return Err(VmErrorKind.InvalidFunctionIndex, $"function index {funcIdx} out of bounds");

        var func = Mod.GetFunction(funcIdx);
        _currentFuncName = func.DebugName;
        CallCounts?.AddOrUpdate(func.DebugName, 1, (_, c) => c + 1);

        _stepsExecuted = 0;

        int localsLen = (int)(func.NumParams + func.NumLocals + 1);
        if (_localsScratch.Length < localsLen)
            _localsScratch = new Value[localsLen];
        Array.Fill(_localsScratch, Value.Nil(), 0, localsLen);

        var frame = new Frame { Func = func, Pc = 0, StackBase = (uint)_stack.Count, Locals = _localsScratch, RetFunc = uint.MaxValue };

        // Copy args into locals. Bound by localsLen rather than NumParams so an
        // instance-method receiver (passed as arg 0 — the IR declares 'this' as
        // the first parameter) and any reflection-supplied args land in the
        // frame's local slots, mirroring how callvirt populates a frame.
        for (int i = 0; i < args.Length && i < localsLen; i++)
            frame.Locals[i] = args[i];

        _frames.Add(frame);
        return Execute();
    }

    // ── Dispatch loop ──────────────────────────────────────────────

    private Result<Value> Execute()
    {
        while (_frames.Count > 0)
        {
            var frame = _frames[^1];
            _currentFuncName = frame.Func.DebugName;
            var code = frame.Func.Code;
            int codeSize = code.Length;
            uint pc = frame.Pc;

        blockEntry:; // re-entered with an embedded block's code/pc (try/catch/finally)
            while (pc < codeSize)
            {
                if (_maxSteps != 0 && ++_stepsExecuted > _maxSteps)
                    return Err(VmErrorKind.StepBudgetExceeded,
                        $"instruction budget exceeded ({_maxSteps} steps)");
                _currentPc = pc; // instruction start — used for error reporting

                // ── Debug hook: check breakpoints, stepping, pause ──
                if (DebugState != null && DebugState.CheckPause(frame.Func.DebugName, pc, _frames.Count, Mod))
                {
                    // Resumed after pause — refresh frame reference (may have been reset)
                    frame = _frames[^1];
                    code = frame.Func.Code;
                    codeSize = code.Length;
                    pc = frame.Pc;
                    _currentFuncName = frame.Func.DebugName;
                }

                ushort op = ReadOpcode(code, ref pc);
                if (_trace || _traceEnvEnabled)
                    Console.Error.WriteLine($"; {_currentFuncName} pc={pc - 1} op=0x{op:X2} stack={_stack.Count}");

                switch ((Opcode)op)
                {
                    case Opcode.Nop: break;
                    case Opcode.LdcI4: case Opcode.Ldc: { int v = ReadI32(code, ref pc); Push(Value.FromI4(v)); break; }
                    case Opcode.LdcI8: { long v = ReadI64(code, ref pc); Push(Value.FromI8(v)); break; }
                    case Opcode.LdcR4: { float v = ReadF32(code, ref pc); Push(Value.FromR4(v)); break; }
                    case Opcode.LdcR8: { double v = ReadF64(code, ref pc); Push(Value.FromR8(v)); break; }
                    case Opcode.Ldstr: { ushort si = ReadU16(code, ref pc); Push(Value.FromStr(si)); break; }
                    case Opcode.Ldarg: { ushort idx = ReadU16(code, ref pc); Push(frame.Locals[idx]); break; }
                    case Opcode.Starg: { ushort idx = ReadU16(code, ref pc); frame.Locals[idx] = Pop(); break; }
                    case Opcode.Ldloc: { ushort idx = ReadU16(code, ref pc); Push(frame.Locals[frame.Func.NumParams + idx]); break; }
                    case Opcode.Stloc: { ushort idx = ReadU16(code, ref pc); frame.Locals[frame.Func.NumParams + idx] = Pop(); break; }

                    case Opcode.Add: { var b = Pop(); var a = Pop(); Push(I4Arith(a, b, I4Add, I8Add, R4Add, R8Add)); break; }
                    case Opcode.Sub: { var b = Pop(); var a = Pop(); Push(I4Arith(a, b, I4Sub, I8Sub, R4Sub, R8Sub)); break; }
                    case Opcode.Mul: { var b = Pop(); var a = Pop(); Push(I4Arith(a, b, I4Mul, I8Mul, R4Mul, R8Mul)); break; }
                    case Opcode.Div:
                    {
                        var b = Pop(); var a = Pop();
                        if ((a.Tag == ValueTag.I4 || a.Tag == ValueTag.I8) && IsZero(b))
                            return Err(VmErrorKind.DivisionByZero, "division by zero");
                        Push(I4Arith(a, b, I4Div, I8Div, R4Div, R8Div));
                        break;
                    }
                    case Opcode.Rem:
                    {
                        var b = Pop(); var a = Pop();
                        if ((a.Tag == ValueTag.I4 || a.Tag == ValueTag.I8) && IsZero(b))
                            return Err(VmErrorKind.DivisionByZero, "remainder by zero");
                        Push(I4Arith(a, b, I4Rem, I8Rem, R4Rem, R8Rem));
                        break;
                    }
                    case Opcode.Neg: { Push(Negate(Pop())); break; }

                    case Opcode.And: { int b = Pop().I4, a = Pop().I4; Push(Value.FromI4(a & b)); break; }
                    case Opcode.Or:  { int b = Pop().I4, a = Pop().I4; Push(Value.FromI4(a | b)); break; }
                    case Opcode.Xor: { int b = Pop().I4, a = Pop().I4; Push(Value.FromI4(a ^ b)); break; }
                    case Opcode.Not: { int v = Pop().I4; Push(Value.FromI4(v == 0 ? 1 : 0)); break; }

                    case Opcode.Ceq: { var b = Pop(); var a = Pop(); Push(Value.FromI4(CompareEquals(a, b) ? 1 : 0)); break; }
                    case Opcode.Cne: { var b = Pop(); var a = Pop(); Push(Value.FromI4(CompareEquals(a, b) ? 0 : 1)); break; }
                    case Opcode.Cgt: { var b = Pop(); var a = Pop(); Push(Value.FromI4(NumericCompare(a, b) > 0 ? 1 : 0)); break; }
                    case Opcode.Cge: { var b = Pop(); var a = Pop(); Push(Value.FromI4(NumericCompare(a, b) >= 0 ? 1 : 0)); break; }
                    case Opcode.Clt: { var b = Pop(); var a = Pop(); Push(Value.FromI4(NumericCompare(a, b) < 0 ? 1 : 0)); break; }
                    case Opcode.Cle: { var b = Pop(); var a = Pop(); Push(Value.FromI4(NumericCompare(a, b) <= 0 ? 1 : 0)); break; }

                    case Opcode.Dup: { Push(Peek()); break; }
                    case Opcode.Pop: { Pop(); break; }
                    case Opcode.Ldnull: { Push(Value.Nil()); break; }

                    case Opcode.Ldfld:
                    {
                        ushort fi = ReadU16(code, ref pc);
                        if (fi >= Mod.Fields.Count) return Err(VmErrorKind.InvalidFieldIndex, $"ldfld invalid index {fi}");
                        var fld = Mod.Fields[(int)fi];
                        var obj = Pop();
                        if (obj.Tag != ValueTag.Obj) return Err(VmErrorKind.NotAnObject, "ldfld on non-object");
                        uint h = obj.AsObj();
                        if (h >= Heap.Count || fld.Offset + 16 > Heap[(int)h].Length) return Err(VmErrorKind.OutOfBounds, "ldfld oob");
                        Push(MemoryMarshal.Read<Value>(Heap[(int)h].AsSpan((int)fld.Offset, 16)));
                        break;
                    }
                    case Opcode.Stfld:
                    {
                        ushort fi = ReadU16(code, ref pc);
                        if (fi >= Mod.Fields.Count) return Err(VmErrorKind.InvalidFieldIndex, $"stfld invalid index {fi}");
                        var fld = Mod.Fields[(int)fi];
                        var val = Pop(); var obj = Pop();
                        if (obj.Tag != ValueTag.Obj) return Err(VmErrorKind.NotAnObject, "stfld on non-object");
                        uint h = obj.AsObj();
                        if (h >= Heap.Count || fld.Offset + 16 > Heap[(int)h].Length) return Err(VmErrorKind.OutOfBounds, "stfld oob");
                        MemoryMarshal.Write(Heap[(int)h].AsSpan((int)fld.Offset, 16), in val);
                        break;
                    }
                    case Opcode.Ldsfld:
                        { ushort fi = ReadU16(code, ref pc); if (fi >= StaticFields.Length) return Err(VmErrorKind.InvalidFieldIndex, "ldsfld oob"); Push(StaticFields[fi]); break; }
                    case Opcode.Stsfld:
                        { ushort fi = ReadU16(code, ref pc); if (fi >= StaticFields.Length) return Err(VmErrorKind.InvalidFieldIndex, "stsfld oob"); StaticFields[fi] = Pop(); break; }

                    case Opcode.Call:
                    case Opcode.Callvirt:
                    case Opcode.NativeCall:
                    {
                        ushort si = ReadU16(code, ref pc);
                        ushort argc = ReadU16(code, ref pc);
                        string name = Mod.GetString(si);
                        CompiledFunction? nativeStub = null;

                        // Delegate dispatch: `callvirt Delegate.Invoke` pops a
                        // receiver (a Delegate object) plus argc args, reads the
                        // target method name from its 'target' field and the
                        // captured environment from its 'closure' field, then
                        // calls that module function with the args. Capturing
                        // lambdas take the closure object as their first param,
                        // so it is prepended to the argument list. The compiler
                        // pushes args first, then the receiver.
                        if (op == (ushort)Opcode.Callvirt && name == "Delegate.Invoke")
                        {
                            var recv = Pop();
                            if (recv.Tag != ValueTag.Obj)
                                return Err(VmErrorKind.NotAnObject, "callvirt Delegate.Invoke on non-object");

                            var popped = new Value[argc];
                            for (int ai = argc - 1; ai >= 0; ai--) popped[ai] = Pop();
                            var (targetName, closureVal, hasClosure) = ReadDelegate(Mod, State, recv.AsObj());
                            if (targetName == null)
                                return Err(VmErrorKind.InvalidObjectHandle, "delegate handle invalid");

                            if (!Mod.FunctionMap.TryGetValue(targetName, out var tfi))
                                return Err(VmErrorKind.UnresolvedMethod, $"delegate target '{targetName}' not found");
                            var tcallee = Mod.GetFunction(tfi);
                            int totalArgs = argc + (hasClosure ? 1 : 0);
                            if (tcallee.NumParams != totalArgs)
                                return Err(VmErrorKind.TypeMismatch, $"delegate target '{targetName}' expects {tcallee.NumParams} args, got {totalArgs}");
                            var dargs = new Value[totalArgs];
                            if (hasClosure) dargs[0] = closureVal;
                            for (int ai = argc - 1; ai >= 0; ai--) dargs[ai + (hasClosure ? 1 : 0)] = popped[ai];
                            int tlocalsLen = (int)(tcallee.NumParams + tcallee.NumLocals + 1);
                            var tlocals = ArrayPool<Value>.Shared.Rent(tlocalsLen);
                            Array.Fill(tlocals, Value.Nil(), 0, tlocalsLen);
                            for (int ai = (int)tcallee.NumParams - 1; ai >= 0; ai--) tlocals[ai] = dargs[ai];
                            CallCounts?.AddOrUpdate(tcallee.DebugName, 1, (_, c) => c + 1);
                            _frames.Add(new Frame { Func = tcallee, Pc = 0, StackBase = (uint)_stack.Count, Locals = tlocals, LocalsRented = true, RetFunc = frame.Func.SelfIndex, RetPc = pc });
                            goto nextFrame;
                        }

                        if (op != (ushort)Opcode.NativeCall)
                        {
                            // Inheritance-aware: "Derived.Method" resolves to the
                            // most-derived declaration — the base chain is walked
                            // when the named type doesn't declare the method.
                            uint cfi = Mod.ResolveFunction(name);

                            // Virtual dispatch (callvirt only): refine through
                            // the RECEIVER's concrete type chain, so a call on
                            // a base/interface-typed variable lands on the
                            // most-derived override. Falls back to static
                            // resolution when the receiver isn't a module
                            // object of a related type.
                            if (op == (ushort)Opcode.Callvirt)
                            {
                                uint refined = ResolveVirtual(name, argc);
                                if (refined != uint.MaxValue) cfi = refined;
                            }

                            if (cfi == uint.MaxValue) { /* no module function — fall through to native */ }
                            else
                            {
                                var callee = Mod.GetFunction(cfi);
                                // Empty or single-ret body (e.g. @DllImport placeholder) — fall through to native.
                                if (callee.Code.Length <= 2) { nativeStub = callee; /* fall through */ }
                                else
                                {
                                    int localsLen = (int)(callee.NumParams + callee.NumLocals + 1);
                                    var locals = ArrayPool<Value>.Shared.Rent(localsLen);
                                    // Clear only the portion we use (avoids touching pooled excess)
                                    Array.Fill(locals, Value.Nil(), 0, localsLen);
                                    for (int ai = (int)callee.NumParams - 1; ai >= 0; ai--) locals[ai] = Pop();
                                    CallCounts?.AddOrUpdate(callee.DebugName, 1, (_, c) => c + 1);
                                    _frames.Add(new Frame { Func = callee, Pc = 0, StackBase = (uint)_stack.Count, Locals = locals, LocalsRented = true, RetFunc = frame.Func.SelfIndex, RetPc = pc });
                                    goto nextFrame;
                                }
                            }
                        }

                        var handler = NativeCallHandler;
                        if (handler == null) return Err(VmErrorKind.UnresolvedMethod, $"call '{name}': no native handler");
                        if (_stack.Count < argc) return Err(VmErrorKind.StackUnderflow, $"call '{name}': need {argc} args, have {_stack.Count}");

                        var args = new object?[argc];
                        var paramTypes = nativeStub?.ParamTypeNames;
                        for (int ai = argc - 1; ai >= 0; ai--)
                        {
                            var v = Pop();
                            // Struct params flow to the bridge as C-layout bytes
                            // (the bridge converts them to blittable C# structs).
                            if (paramTypes is { Length: > 0 } && ai < paramTypes.Length && StructMarshaller.IsStructType(Mod, paramTypes[ai]))
                            {
                                var packed = StructMarshaller.Pack(Mod, this, paramTypes[ai], v);
                                if (packed.IsError) return packed.Error;
                                args[ai] = packed.Value;
                            }
                            else
                            {
                                args[ai] = ValueToObject(v);
                            }
                        }
                        object? result;
                        try { result = handler(name, args); }
                        catch (Exception ex) { return Err(VmErrorKind.RuntimeError, $"call '{name}': {ex.Message}"); }
                        // Struct returns arrive as C-layout bytes; unpack them
                        // into a fresh heap object (handling nested structs).
                        if (nativeStub?.ReturnTypeName is string rtn && StructMarshaller.IsStructType(Mod, rtn))
                        {
                            if (result is byte[] packedBytes)
                            {
                                var unpacked = StructMarshaller.Unpack(Mod, this, rtn, packedBytes);
                                if (unpacked.IsError) return unpacked.Error;
                                Push(Value.FromObj(unpacked.Value));
                            }
                            else
                            {
                                Push(MarshalValue(result));
                            }
                        }
                        else
                        {
                            Push(MarshalValue(result));
                        }
                        break;
                    }

                    case Opcode.Ret:
                    {
                        // The return value lives in THIS frame's region of the
                        // shared stack ([StackBase .. count)). Values below
                        // StackBase belong to the caller and must not be read
                        // as the return value — a void method that leaves its
                        // region empty returns nil, not the caller's residue.
                        var retval = _stack.Count > frame.StackBase ? _stack[^1] : Value.Nil();
                        if (_stack.Count > frame.StackBase) _stack.RemoveAt(_stack.Count - 1);
                        // Return rented locals array to pool
                        if (frame.LocalsRented)
                            ArrayPool<Value>.Shared.Return(frame.Locals);
                        uint rf = frame.RetFunc, rp = frame.RetPc;
                        _frames.RemoveAt(_frames.Count - 1);
                        if (_frames.Count == 0) { Push(retval); return retval; }
                        Push(retval); _frames[^1].Pc = rp;
                        goto nextFrame;
                    }

                    case Opcode.Br:   { int off = ReadI32(code, ref pc); pc = (uint)((int)pc + off); break; }
                    case Opcode.Brfalse: { int off = ReadI32(code, ref pc); if (!Pop().IsTruthy()) pc = (uint)((int)pc + off); break; }
                    case Opcode.Brtrue:  { int off = ReadI32(code, ref pc); if (Pop().IsTruthy()) pc = (uint)((int)pc + off); break; }

                    case Opcode.Newobj:
                        { ushort ti = ReadU16(code, ref pc); var ar = AllocObject(ti); if (ar.IsError) return ar.Error; Push(Value.FromObj(ar.Value)); break; }
                    case Opcode.Newarr:
                        {
                            // Operand is the element type name (v1 arrays are
                            // untyped CLR object arrays) — informational only.
                            ReadU16(code, ref pc);
                            var len = Pop();
                            if (len.Tag != ValueTag.I4 || len.I4 < 0)
                                return Err(VmErrorKind.TypeMismatch, "newarr: length must be a non-negative int");
                            var arr = new object[len.I4];
                            Push(Value.FromObj(State.InternExternal(arr)));
                            break;
                        }
                    case Opcode.Ldelem:
                        {
                            var index = Pop();
                            var arrVal = Pop();
                            if (GetExternalArray(arrVal) is not System.Array arr)
                                return Err(VmErrorKind.NotAnObject, "ldelem on non-array");
                            if (index.Tag != ValueTag.I4 || index.I4 < 0 || index.I4 >= arr.Length)
                                return Err(VmErrorKind.OutOfBounds, $"ldelem index {(index.Tag == ValueTag.I4 ? index.I4.ToString() : "?")} out of bounds ({arr.Length})");
                            Push(MarshalValue(arr.GetValue(index.I4)));
                            break;
                        }
                    case Opcode.Stelem:
                        {
                            var val = Pop();
                            var index = Pop();
                            var arrVal = Pop();
                            if (GetExternalArray(arrVal) is not System.Array arr)
                                return Err(VmErrorKind.NotAnObject, "stelem on non-array");
                            if (index.Tag != ValueTag.I4 || index.I4 < 0 || index.I4 >= arr.Length)
                                return Err(VmErrorKind.OutOfBounds, $"stelem index {(index.Tag == ValueTag.I4 ? index.I4.ToString() : "?")} out of bounds ({arr.Length})");
                            arr.SetValue(ValueToObject(val), index.I4);
                            break;
                        }
                    case Opcode.Ldlen:
                        {
                            var arrVal = Pop();
                            if (GetExternalArray(arrVal) is not System.Array arr)
                                return Err(VmErrorKind.NotAnObject, "ldlen on non-array");
                            Push(Value.FromI4(arr.Length));
                            break;
                        }
                    case Opcode.Conv: case Opcode.Castclass: { ReadU16(code, ref pc); break; }
                    case Opcode.Isinst:
                    {
                        ushort typeIdx = ReadU16(code, ref pc);
                        var val = Pop();
                        if (val.Tag != ValueTag.Obj)
                        {
                            Push(Value.FromI4(0));
                            break;
                        }
                        uint handle = val.AsObj();
                        if (ExecutorState.IsExternal(handle) || !State.TryGetObjectType(handle, out int objType))
                        {
                            Push(Value.FromI4(0));
                            break;
                        }
                        // Walk the type chain to check if objType is or derives from typeIdx
                        int cur = objType;
                        bool match = false;
                        int depthLimit = 64;
                        while (cur >= 0 && depthLimit-- > 0)
                        {
                            if (cur == typeIdx) { match = true; break; }
                            var t = Mod.Types[cur];
                            if (t.InterfaceNames != null)
                            {
                                foreach (var iname in t.InterfaceNames)
                                {
                                    if (Mod.TryFindTypeIndex(iname) == typeIdx) { match = true; break; }
                                }
                                if (match) break;
                            }
                            cur = t.BaseType;
                        }
                        Push(Value.FromI4(match ? 1 : 0));
                        break;
                    }

                    case Opcode.If: case Opcode.While:
                        { byte ck = code[pc++]; if (ck == 0x01) pc++; else if (ck >= 0x02) { uint len = ReadU32(code, ref pc); pc += len; } break; }
                    case Opcode.Try:
                    {
                        uint tl = ReadU32(code, ref pc);
                        var tryBlock = new byte[tl];
                        if (tl > 0) { Array.Copy(code, pc, tryBlock, 0, (int)tl); pc += tl; }
                        ushort cc = ReadU16(code, ref pc);
                        var catchBodies = new byte[cc][];
                        var catchTypeIndices = new ushort[cc];
                        for (ushort ci = 0; ci < cc; ci++)
                        {
                            catchTypeIndices[ci] = ReadU16(code, ref pc);
                            uint bl = ReadU32(code, ref pc);
                            catchBodies[ci] = new byte[bl];
                            if (bl > 0) { Array.Copy(code, pc, catchBodies[ci], 0, (int)bl); pc += bl; }
                        }
                        bool hasFinally = code[pc++] != 0;
                        byte[]? finallyBody = null;
                        if (hasFinally)
                        {
                            uint fl = ReadU32(code, ref pc);
                            finallyBody = new byte[fl];
                            if (fl > 0) { Array.Copy(code, pc, finallyBody, 0, (int)fl); pc += fl; }
                        }

                        var ef = new ExceptionFrame
                        {
                            OuterCode = code,
                            OuterPc = pc,
                            StackBase = (int)_stack.Count,
                            CatchBodies = catchBodies,
                            CatchTypeIndices = catchTypeIndices,
                            FinallyBody = finallyBody,
                        };
                        _exceptionHandlers.Push(ef);

                        if (tl > 0)
                        {
                            code = tryBlock;
                            codeSize = (int)tl;
                            pc = 0;
                        }
                        break;
                    }
                    case Opcode.Throw:
                    {
                        var exVal = Pop();
                        if (_exceptionHandlers.Count > 0)
                        {
                            var handled = TryDispatchThrow(exVal, ref frame, ref code, ref codeSize, ref pc);
                            if (handled != null)
                                return Err(handled.Value.Kind, handled.Value.Message);
                            break; // continue the dispatch loop in the catch/finally body
                        }
                        return Err(VmErrorKind.RuntimeError,
                            $"Unhandled exception: {FormatValue(exVal)}");
                    }
                    case Opcode.Break: case Opcode.Continue: break;
                    default: break;
                }
                frame.Pc = pc;
            }

            // ── Embedded block completion (try/catch/finally) ──────
            // When an embedded block (try body, catch body, or finally body)
            // finishes, decide what to run next based on the handler's phase.
            if (_exceptionHandlers.Count > 0)
            {
                var ef = _exceptionHandlers.Peek();
                if (code != ef.OuterCode) // we're still inside an embedded block
                {
                    switch (ef.Phase)
                    {
                        case HandlerPhase.TryBody:
                        case HandlerPhase.CatchBody:
                            // Normal completion of try/catch body: run finally if present.
                            if (ef.FinallyBody != null && ef.FinallyBody.Length > 0)
                            {
                                ef.Phase = HandlerPhase.FinallyBody;
                                code = ef.FinallyBody;
                                codeSize = code.Length;
                                pc = 0;
                                frame.Pc = 0;
                                goto blockEntry;
                            }
                            // No finally — finish this handler, resume outer.
                            _exceptionHandlers.Pop();
                            code = ef.OuterCode;
                            codeSize = code.Length;
                            pc = ef.OuterPc;
                            frame.Pc = pc;
                            goto blockEntry;

                        case HandlerPhase.FinallyBody:
                            // Finally completed. Pop the handler.
                            _exceptionHandlers.Pop();
                            if (ef.PendingRethrow)
                            {
                                // The finally ran because of an exception — propagate it outward.
                                var pending = ef.PendingException;
                                var err = TryDispatchThrow(pending, ref frame, ref code, ref codeSize, ref pc);
                                if (err != null) return Err(err.Value.Kind, err.Value.Message);
                                goto blockEntry;
                            }
                            code = ef.OuterCode;
                            codeSize = code.Length;
                            pc = ef.OuterPc;
                            frame.Pc = pc;
                            goto blockEntry;
                    }
                }
            }

            if (_frames.Count > 0) _frames.RemoveAt(_frames.Count - 1);
        nextFrame:;
        }
        return Value.Nil();
    }

    // ── Exception handling ───────────────────────────────────────────

    /// <summary>
    /// Searches the exception handler stack for a matching catch handler,
    /// restores the stack, pushes the exception, and switches execution to
    /// the catch body (or finally body). Returns null when a handler was
    /// found and execution should continue in the dispatch loop; returns an
    /// error when the exception is unhandled.
    /// </summary>
    private (VmErrorKind Kind, string Message)? TryDispatchThrow(Value exVal, ref Frame frame,
        ref byte[] code, ref int codeSize, ref uint pc)
    {
        while (_exceptionHandlers.Count > 0)
        {
            var ef = _exceptionHandlers.Pop();

            // If we're already inside this handler's catch/finally body (a rethrow),
            // it cannot catch itself. Run its finally if not yet, then propagate outward.
            if (ef.Phase == HandlerPhase.CatchBody)
            {
                if (ef.FinallyBody != null && ef.FinallyBody.Length > 0)
                {
                    ef.Phase = HandlerPhase.FinallyBody;
                    ef.PendingRethrow = true;
                    ef.PendingException = exVal;
                    code = ef.FinallyBody;
                    codeSize = code.Length;
                    pc = 0;
                    frame.Pc = 0;
                    _exceptionHandlers.Push(ef);
                    return null;
                }
                continue; // no finally — keep unwinding outward
            }

            if (ef.Phase == HandlerPhase.FinallyBody)
            {
                // Exception thrown inside the finally body: propagate outward.
                continue;
            }

            // Phase == TryBody — the try block threw. Restore the stack.
            while (_stack.Count > ef.StackBase)
                _stack.RemoveAt(_stack.Count - 1);

            // Find a matching catch handler (first match wins; type index 0 = catch-all)
            for (int ci = 0; ci < ef.CatchBodies.Length; ci++)
            {
                if (ef.CatchTypeIndices[ci] == 0 || ef.CatchBodies[ci].Length > 0)
                {
                    // Push the exception value for the catch variable
                    Push(exVal);
                    ef.Phase = HandlerPhase.CatchBody;
                    ef.PendingRethrow = false;

                    // Execute the catch body
                    code = ef.CatchBodies[ci];
                    codeSize = code.Length;
                    pc = 0;
                    frame.Pc = 0;
                    _exceptionHandlers.Push(ef);
                    return null;
                }
            }

            // No matching catch — if there's a finally, run it then re-throw
            if (ef.FinallyBody != null && ef.FinallyBody.Length > 0)
            {
                ef.Phase = HandlerPhase.FinallyBody;
                ef.PendingRethrow = true;
                ef.PendingException = exVal;
                code = ef.FinallyBody;
                codeSize = code.Length;
                pc = 0;
                frame.Pc = 0;
                _exceptionHandlers.Push(ef);
                return null;
            }

            // Continue searching outer handlers
        }

        return (VmErrorKind.RuntimeError,
            $"Unhandled exception: {FormatValue(exVal)}");
    }

    /// <summary>Formats a Value for display in error messages.</summary>
    private static string FormatValue(Value v) => v.Tag switch
    {
        ValueTag.Nil => "nil",
        ValueTag.I4 => v.I4.ToString(),
        ValueTag.I8 => v.I8.ToString(),
        ValueTag.R4 => v.R4.ToString(),
        ValueTag.R8 => v.R8.ToString(),
        ValueTag.Str => $"\"{v.AsStr()}\"",
        ValueTag.Obj => $"object@{v.AsObj()}",
        _ => "<unknown>"
    };

    // ── Delegate dispatch (shared by the interpreter case and threads) ──

    /// <summary>Resolves an external object handle to a CLR <see cref="System.Array"/>, or null.</summary>
    private System.Array? GetExternalArray(Value v)
    {
        if (v.Tag != ValueTag.Obj || !ExecutorState.IsExternal(v.AsObj()))
            return null;
        return State.GetExternal(v.AsObj()) as System.Array;
    }

    /// <summary>
    /// Virtual dispatch: for a callvirt "Type.Method(argc args)" whose receiver
    /// (arg 0, the deepest of the pushed arguments) is a module heap object,
    /// walk the RECEIVER's concrete type chain looking for a real override of
    /// Method. The refined target is used only when the receiver's chain passes
    /// through the named type — i.e. the receiver IS-A Type — so unrelated
    /// static calls are never hijacked. Returns uint.MaxValue when no better
    /// target exists and the caller should keep its static resolution.
    ///
    /// Uses the pre-built vtable for O(1) dispatch when available, falling back
    /// to the legacy chain-walk for modules compiled without vtables.
    /// </summary>
    private uint ResolveVirtual(string name, ushort argc)
    {
        if (argc == 0 || _stack.Count < argc) return uint.MaxValue;

        var recv = _stack[_stack.Count - argc];   // arg 0: pushed first, deepest
        if (recv.Tag != ValueTag.Obj) return uint.MaxValue;
        uint handle = recv.AsObj();
        if (ExecutorState.IsExternal(handle)) return uint.MaxValue;
        if (!State.TryGetObjectType(handle, out int typeIdx)) return uint.MaxValue;

        int dot = name.LastIndexOf('.');
        if (dot <= 0 || dot >= name.Length - 1) return uint.MaxValue;
        string methodName = name[(dot + 1)..];
        string typeName = name[..dot];

        // Fast path: use the pre-built vtable (O(1) lookup, zero allocation)
        uint result = Mod.ResolveVirtualMethod(typeIdx, methodName);
        if (result != uint.MaxValue)
        {
            // Verify the receiver IS-A the call's named type (same semantics as legacy path)
            int staticTypeIdx = Mod.TryFindTypeIndex(typeName);
            if (staticTypeIdx < 0) return uint.MaxValue; // unresolvable: trust vtable only for known types
            // Walk chain to check relationship (no HashSet needed — chain is short and bounded)
            int cur = typeIdx;
            int depthLimit = 64; // safety: types can't be more than 64 levels deep
            while (cur >= 0 && depthLimit-- > 0)
            {
                if (cur == staticTypeIdx) return result;
                var t = Mod.Types[cur];
                if (t.InterfaceNames != null)
                {
                    foreach (var iname in t.InterfaceNames)
                    {
                        if (Mod.TryFindTypeIndex(iname) == staticTypeIdx) return result;
                    }
                }
                cur = t.BaseType;
            }
            return uint.MaxValue;
        }

        // Slow path fallback: legacy type-chain walk (for modules without vtables)
        int staticTypeIdx2 = Mod.TryFindTypeIndex(typeName);
        bool related = staticTypeIdx2 < 0;
        uint? firstCandidate = null;
        int cur2 = typeIdx;
        int depthLimit2 = 64;
        while (cur2 >= 0 && depthLimit2-- > 0)
        {
            var t = Mod.Types[cur2];
            if (cur2 == staticTypeIdx2) related = true;
            if (!related && t.InterfaceNames != null)
            {
                foreach (var iname in t.InterfaceNames)
                {
                    if (iname == typeName || Mod.TryFindTypeIndex(iname) == staticTypeIdx2)
                    {
                        related = true;
                        break;
                    }
                }
            }
            if (firstCandidate == null && Mod.FunctionMap.TryGetValue($"{t.DebugName}.{methodName}", out uint idx))
            {
                var candidate = Mod.Functions[(int)idx];
                if (candidate.NumParams == argc && candidate.Code.Length > 2)
                    firstCandidate = idx;
            }
            cur2 = t.BaseType;
        }
        return related && firstCandidate != null ? firstCandidate.Value : uint.MaxValue;
    }

    /// <summary>
    /// Equality for ceq/cne. Strings compare by value; anything else falls back
    /// to numeric comparison (which is also the fallback for mixed types).
    /// </summary>
    private bool CompareEquals(Value a, Value b)
    {
        if (a.Tag == ValueTag.Str || b.Tag == ValueTag.Str)
        {
            if (a.Tag != ValueTag.Str || b.Tag != ValueTag.Str)
                return false;
            // Fast path: interned strings with the same handle are identical
            if (a.AsStr() == b.AsStr()) return true;
            return string.Equals(GetStringValue(a.AsStr()), GetStringValue(b.AsStr()), StringComparison.Ordinal);
        }
        return NumericCompare(a, b) == 0;
    }

    /// <summary>
    /// Reads a delegate value's target method name and closure object from the
    /// shared heap. Returns (null, _, _) when the handle is not a valid Delegate.
    /// </summary>
    public static (string? Target, Value Closure, bool HasClosure) ReadDelegate(CompiledModule mod, ExecutorState state, uint handle)
    {
        if (handle >= state.Heap.Count)
            return (null, default, false);

        // Use cached Delegate type index instead of O(n) linear scan
        int delegateTypeIdx = mod.DelegateTypeIdx;
        if (delegateTypeIdx < 0)
            return (null, default, false);
        var dt = mod.Types[delegateTypeIdx];
        if (dt.FieldCount < 2 || dt.FieldOffset + 2 > (uint)mod.Fields.Count)
            return (null, default, false);
        var targetField = mod.Fields[(int)dt.FieldOffset];      // target
        var closureField = mod.Fields[(int)dt.FieldOffset + 1]; // closure

        if (targetField.Offset + 16 > (uint)state.Heap[(int)handle].Length)
            return (null, default, false);
        var targetVal = MemoryMarshal.Read<Value>(state.Heap[(int)handle].AsSpan((int)targetField.Offset, 16));
        if (targetVal.Tag != ValueTag.Str)
            return (null, default, false);
        string target = state.GetStringValue(targetVal.AsStr()) ?? "";

        bool hasClosure = false;
        Value closure = Value.Nil();
        if (closureField.Offset + 16 <= (uint)state.Heap[(int)handle].Length)
        {
            closure = MemoryMarshal.Read<Value>(state.Heap[(int)handle].AsSpan((int)closureField.Offset, 16));
            hasClosure = closure.Tag == ValueTag.Obj;
        }
        return (target, closure, hasClosure);
    }

    /// <summary>
    /// Runs a delegate value's target function on this executor (used as a
    /// thread entry point). Resolves the target/closure from the shared heap,
    /// prepends the closure when present, and runs the module function.
    /// </summary>
    public Result<Value> RunDelegate(uint handle, Value[] args)
    {
        var (targetName, closureVal, hasClosure) = ReadDelegate(Mod, State, handle);
        if (targetName == null)
            return new VmError(VmErrorKind.InvalidObjectHandle, "delegate handle invalid");
        if (!Mod.FunctionMap.TryGetValue(targetName, out var tfi))
            return new VmError(VmErrorKind.UnresolvedMethod, $"delegate target '{targetName}' not found");
        var callee = Mod.GetFunction(tfi);
        int totalArgs = args.Length + (hasClosure ? 1 : 0);
        if (callee.NumParams != totalArgs)
            return new VmError(VmErrorKind.TypeMismatch, $"delegate target '{targetName}' expects {callee.NumParams} args, got {totalArgs}");
        var dargs = new Value[totalArgs];
        if (hasClosure) dargs[0] = closureVal;
        for (int i = 0; i < args.Length; i++) dargs[i + (hasClosure ? 1 : 0)] = args[i];
        return RunFunction(tfi, dargs);
    }

    // ── Stack ops ──────────────────────────────────────────────────

    private void Push(Value v) => _stack.Add(v);
    private Value Pop()
    {
        if (_stack.Count == 0)
            throw new VmRuntimeException(BuildError(VmErrorKind.StackUnderflow, "stack underflow"));
        var v = _stack[^1]; _stack.RemoveAt(_stack.Count - 1); return v;
    }
    private Value Peek(int depth = 0) => _stack[^(1 + depth)];

    // ── Arithmetic helpers ─────────────────────────────────────────

    public static readonly Func<int,int,int> I4Add = (x,y)=>x+y, I4Sub=(x,y)=>x-y, I4Mul=(x,y)=>x*y, I4Div=(x,y)=>y!=0?x/y:0, I4Rem=(x,y)=>y!=0?x%y:0;
    public static readonly Func<long,long,long> I8Add = (x,y)=>x+y, I8Sub=(x,y)=>x-y, I8Mul=(x,y)=>x*y, I8Div=(x,y)=>x/y, I8Rem=(x,y)=>x%y;
    public static readonly Func<float,float,float> R4Add = (x,y)=>x+y, R4Sub=(x,y)=>x-y, R4Mul=(x,y)=>x*y, R4Div=(x,y)=>x/y, R4Rem=(x,y)=>x%y;
    public static readonly Func<double,double,double> R8Add = (x,y)=>x+y, R8Sub=(x,y)=>x-y, R8Mul=(x,y)=>x*y, R8Div=(x,y)=>x/y, R8Rem=(x,y)=>x%y;

    /// <summary>Fast-path for int+int arithmetic — avoids delegate dispatch.</summary>
    private static Value I4Arith(Value a, Value b, Func<int,int,int> fallbackOp,
        Func<long,long,long> i8Op, Func<float,float,float> r4Op, Func<double,double,double> r8Op)
    {
        if (a.Tag == ValueTag.I4 && b.Tag == ValueTag.I4)
            return Value.FromI4(fallbackOp(a.I4, b.I4));
        return Arith(a, b, fallbackOp, i8Op, r4Op, r8Op);
    }

    public static Value Arith(Value a, Value b,
        Func<int, int, int> opI4, Func<long, long, long> opI8,
        Func<float, float, float> opR4, Func<double, double, double> opR8)
    {
        // Nil participates as a typed zero: `nil + 1` stays int (like a C#
        // static int field defaulting to 0), `nil + 1.5` stays double. Without
        // this, the mixed-type fallback below would turn every uninitialized
        // int counter into a double.
        if (a.Tag == ValueTag.Nil && b.Tag != ValueTag.Nil)
            a = b.Tag switch
            {
                ValueTag.I4 => Value.FromI4(0),
                ValueTag.I8 => Value.FromI8(0),
                ValueTag.R4 => Value.FromR4(0),
                _ => Value.FromR8(0),
            };
        if (b.Tag == ValueTag.Nil && a.Tag != ValueTag.Nil)
            b = a.Tag switch
            {
                ValueTag.I4 => Value.FromI4(0),
                ValueTag.I8 => Value.FromI8(0),
                ValueTag.R4 => Value.FromR4(0),
                _ => Value.FromR8(0),
            };

        if (a.Tag == ValueTag.I4 && b.Tag == ValueTag.I4) return Value.FromI4(opI4(a.I4, b.I4));
        if (a.Tag == ValueTag.I8 && b.Tag == ValueTag.I8) return Value.FromI8(opI8(a.I8, b.I8));
        if (a.Tag == ValueTag.R4 && b.Tag == ValueTag.R4) return Value.FromR4(opR4(a.R4, b.R4));
        if (a.Tag == ValueTag.R8 && b.Tag == ValueTag.R8) return Value.FromR8(opR8(a.R8, b.R8));
        return Value.FromR8(opR8(ToDouble(a), ToDouble(b)));
    }

    public static Value Negate(Value v) => v.Tag switch { ValueTag.I4 => Value.FromI4(-v.I4), ValueTag.I8 => Value.FromI8(-v.I8), ValueTag.R4 => Value.FromR4(-v.R4), ValueTag.R8 => Value.FromR8(-v.R8), _ => Value.Nil() };
    public static double ToDouble(Value v) => v.Tag switch { ValueTag.I4 => v.I4, ValueTag.I8 => v.I8, ValueTag.R4 => v.R4, ValueTag.R8 => v.R8, _ => 0 };
    public static bool IsZero(Value v) => v.Tag switch { ValueTag.I4 => v.I4 == 0, ValueTag.I8 => v.I8 == 0, ValueTag.R4 => v.R4 == 0f, ValueTag.R8 => v.R8 == 0d, _ => false };
    public static int NumericCompare(Value a, Value b) => (a.Tag == ValueTag.I4 && b.Tag == ValueTag.I4) ? a.I4.CompareTo(b.I4) : (a.Tag == ValueTag.I8 && b.Tag == ValueTag.I8) ? a.I8.CompareTo(b.I8) : ToDouble(a).CompareTo(ToDouble(b));

    // ── Bytecode read helpers ──────────────────────────────────────

    internal static ushort ReadOpcode(byte[] code, ref uint pc) { int table = 0; while (code[pc] == 0xFF) { table++; pc++; } return (ushort)(table * 256 + code[pc++]); }
    internal static ushort ReadU16(byte[] code, ref uint pc) { ushort v = (ushort)(code[pc] | (code[pc + 1] << 8)); pc += 2; return v; }
    internal static uint ReadU32(byte[] code, ref uint pc) { uint v = (uint)(code[pc] | (code[pc + 1] << 8) | (code[pc + 2] << 16) | (code[pc + 3] << 24)); pc += 4; return v; }
    internal static int ReadI32(byte[] code, ref uint pc) => (int)ReadU32(code, ref pc);
    internal static long ReadI64(byte[] code, ref uint pc) { uint lo = ReadU32(code, ref pc), hi = ReadU32(code, ref pc); return (long)(lo | ((ulong)hi << 32)); }
    internal static float ReadF32(byte[] code, ref uint pc) => BitConverter.Int32BitsToSingle(ReadI32(code, ref pc));
    internal static double ReadF64(byte[] code, ref uint pc) => BitConverter.Int64BitsToDouble(ReadI64(code, ref pc));

    private VmError Err(VmErrorKind kind, string msg) => BuildError(kind, msg);

    /// <summary>
    /// Builds a rich <see cref="VmError"/> from the current execution state:
    /// the failing IR instruction (opcode + pc), the original-source mapping
    /// (from `#line` metadata), and the call stack.
    /// </summary>
    private VmError BuildError(VmErrorKind kind, string msg)
    {
        var err = new VmError(kind, msg, _currentFuncName);

        // The failing IR instruction + original-source mapping.
        if (_frames.Count > 0)
        {
            var frame = _frames[^1];
            var code = frame.Func.Code;
            if (_currentPc < code.Length)
            {
                var p = _currentPc;
                err.Opcode = OpcodeExtensions.ToDisplayString((Opcode)ReadOpcode(code, ref p));
                err.Pc = _currentPc;
            }

            var map = frame.Func.SourceMap;
            if (map != null && map.Count > 0)
            {
                // Largest entry with Offset <= _currentPc.
                SourceMapEntry? best = null;
                foreach (var e in map)
                {
                    if (e.Offset <= _currentPc) best = e;
                    else break;
                }
                err.Source = best;
            }

            // Call stack, innermost first.
            var stack = new List<string>(_frames.Count);
            for (int i = _frames.Count - 1; i >= 0; i--)
            {
                var f = _frames[i];
                var name = f.Func.DebugName;
                var pc = i == _frames.Count - 1 ? _currentPc : f.RetPc;
                stack.Add(pc != 0 ? $"{name}@0x{pc:X}" : name);
            }
            err.CallStack = stack;
        }

        return err;
    }
}
