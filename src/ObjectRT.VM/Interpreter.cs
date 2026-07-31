using System.Runtime.InteropServices;
using ObjectRT.Abstractions;

namespace ObjectRT.VM;

internal class Frame
{
    public CompiledFunction Func { get; set; } = null!;
    public uint Pc;
    public Value[] Locals = [];
    public uint StackBase;
    public uint RetPc;
    public uint RetFunc;
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
    private bool _trace;

    public Interpreter(CompiledModule mod) : base(mod) { }

    public bool Trace { get => _trace; set => _trace = value; }

    public override void Reset(bool clearHeap = false, bool clearStatics = false)
    {
        _stack.Clear();
        _frames.Clear();
        _currentFuncName = "";
        if (clearHeap) Heap.Clear();
        if (clearStatics) Array.Fill(StaticFields, Value.Nil());
    }

    public override Result<Value> RunFunction(uint funcIdx, Value[] args)
    {
        if (funcIdx >= Mod.Functions.Count)
            return Err(VmErrorKind.InvalidFunctionIndex, $"function index {funcIdx} out of bounds");

        var func = Mod.GetFunction(funcIdx);
        _currentFuncName = func.DebugName;

        int localsLen = (int)(func.NumParams + func.NumLocals + 1);
        if (_localsScratch.Length < localsLen)
            _localsScratch = new Value[localsLen];
        Array.Fill(_localsScratch, Value.Nil(), 0, localsLen);

        var frame = new Frame { Func = func, Pc = 0, StackBase = (uint)_stack.Count, Locals = _localsScratch, RetFunc = uint.MaxValue };

        for (int i = 0; i < args.Length && i < func.NumParams; i++)
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

            while (pc < codeSize)
            {
                ushort op = ReadOpcode(code, ref pc);

                switch ((Opcode)op)
                {
                    case Opcode.Nop: break;
                    case Opcode.LdcI4: case Opcode.Ldc: { int v = ReadI32(code, ref pc); Push(Value.FromI4(v)); break; }
                    case Opcode.LdcI8: { long v = ReadI64(code, ref pc); Push(Value.FromI8(v)); break; }
                    case Opcode.LdcR4: { float v = ReadF32(code, ref pc); Push(Value.FromR4(v)); break; }
                    case Opcode.LdcR8: { double v = ReadF64(code, ref pc); Push(Value.FromR8(v)); break; }
                    case Opcode.Ldstr: { ushort si = ReadU16(code, ref pc); Push(Value.FromStr(InternString(Mod.GetString(si)))); break; }
                    case Opcode.Ldarg: { ushort idx = ReadU16(code, ref pc); Push(frame.Locals[idx]); break; }
                    case Opcode.Starg: { ushort idx = ReadU16(code, ref pc); frame.Locals[idx] = Pop(); break; }
                    case Opcode.Ldloc: { ushort idx = ReadU16(code, ref pc); Push(frame.Locals[frame.Func.NumParams + idx]); break; }
                    case Opcode.Stloc: { ushort idx = ReadU16(code, ref pc); frame.Locals[frame.Func.NumParams + idx] = Pop(); break; }

                    case Opcode.Add: { var b = Pop(); var a = Pop(); Push(Arith(a, b, (x, y) => x + y, (x, y) => x + y, (x, y) => x + y, (x, y) => x + y)); break; }
                    case Opcode.Sub: { var b = Pop(); var a = Pop(); Push(Arith(a, b, (x, y) => x - y, (x, y) => x - y, (x, y) => x - y, (x, y) => x - y)); break; }
                    case Opcode.Mul: { var b = Pop(); var a = Pop(); Push(Arith(a, b, (x, y) => x * y, (x, y) => x * y, (x, y) => x * y, (x, y) => x * y)); break; }
                    case Opcode.Div: { var b = Pop(); var a = Pop(); Push(Arith(a, b, (x, y) => y != 0 ? x / y : 0, (x, y) => x / y, (x, y) => x / y, (x, y) => x / y)); break; }
                    case Opcode.Rem: { var b = Pop(); var a = Pop(); Push(Arith(a, b, (x, y) => y != 0 ? x % y : 0, (x, y) => x % y, (x, y) => x % y, (x, y) => x % y)); break; }
                    case Opcode.Neg: { Push(Negate(Pop())); break; }

                    case Opcode.And: { int b = Pop().I4, a = Pop().I4; Push(Value.FromI4(a & b)); break; }
                    case Opcode.Or:  { int b = Pop().I4, a = Pop().I4; Push(Value.FromI4(a | b)); break; }
                    case Opcode.Xor: { int b = Pop().I4, a = Pop().I4; Push(Value.FromI4(a ^ b)); break; }
                    case Opcode.Not: { Push(Value.FromI4(~Pop().I4)); break; }

                    case Opcode.Ceq: { var b = Pop(); var a = Pop(); Push(Value.FromI4(NumericCompare(a, b) == 0 ? 1 : 0)); break; }
                    case Opcode.Cne: { var b = Pop(); var a = Pop(); Push(Value.FromI4(NumericCompare(a, b) != 0 ? 1 : 0)); break; }
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

                        if (op != (ushort)Opcode.NativeCall && Mod.FunctionMap.TryGetValue(name, out var cfi))
                        {
                            var callee = Mod.GetFunction(cfi);
                            if (callee.Code.Length == 0) { Push(Value.Nil()); break; }
                            var locals = new Value[callee.NumParams + callee.NumLocals + 1];
                            Array.Fill(locals, Value.Nil());
                            for (int ai = (int)callee.NumParams - 1; ai >= 0; ai--) locals[ai] = Pop();
                            _frames.Add(new Frame { Func = callee, Pc = 0, StackBase = (uint)_stack.Count, Locals = locals, RetFunc = frame.Func.SelfIndex, RetPc = pc });
                            goto nextFrame;
                        }

                        var handler = NativeCallHandler;
                        if (handler == null) return Err(VmErrorKind.UnresolvedMethod, $"call '{name}': no native handler");
                        if (_stack.Count < argc) return Err(VmErrorKind.StackUnderflow, $"call '{name}': need {argc} args, have {_stack.Count}");

                        var args = new object?[argc];
                        for (int ai = argc - 1; ai >= 0; ai--) args[ai] = ValueToObject(Pop());
                        object? result;
                        try { result = handler(name, args); }
                        catch (Exception ex) { return Err(VmErrorKind.RuntimeError, $"call '{name}': {ex.Message}"); }
                        Push(MarshalValue(result));
                        break;
                    }

                    case Opcode.Ret:
                    {
                        var retval = _stack.Count > 0 ? _stack[^1] : Value.Nil();
                        if (_stack.Count > 0) _stack.RemoveAt(_stack.Count - 1);
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
                    case Opcode.Newarr: { ReadU16(code, ref pc); Push(Value.Nil()); break; }
                    case Opcode.Ldelem: { Pop(); Pop(); Push(Value.Nil()); break; }
                    case Opcode.Stelem: { Pop(); Pop(); Pop(); break; }
                    case Opcode.Conv: case Opcode.Castclass: case Opcode.Isinst: { ReadU16(code, ref pc); break; }

                    case Opcode.If: case Opcode.While:
                        { byte ck = code[pc++]; if (ck == 0x01) pc++; else if (ck >= 0x02) { uint len = ReadU32(code, ref pc); pc += len; } break; }
                    case Opcode.Try:
                        { uint tl = ReadU32(code, ref pc); pc += tl; ushort cc = ReadU16(code, ref pc); for (ushort ci = 0; ci < cc; ci++) { ReadU16(code, ref pc); uint bl = ReadU32(code, ref pc); pc += bl; } if (code[pc++] != 0) { uint fl = ReadU32(code, ref pc); pc += fl; } break; }
                    case Opcode.Throw: case Opcode.Break: case Opcode.Continue: break;
                    default: break;
                }
                frame.Pc = pc;
            }
            if (_frames.Count > 0) _frames.RemoveAt(_frames.Count - 1);
        nextFrame:;
        }
        return Value.Nil();
    }

    // ── Stack ops ──────────────────────────────────────────────────

    private void Push(Value v) => _stack.Add(v);
    private Value Pop() { var v = _stack[^1]; _stack.RemoveAt(_stack.Count - 1); return v; }
    private Value Peek(int depth = 0) => _stack[^(1 + depth)];

    // ── Arithmetic helpers ─────────────────────────────────────────

    public static readonly Func<int,int,int> I4Add = (x,y)=>x+y, I4Sub=(x,y)=>x-y, I4Mul=(x,y)=>x*y, I4Div=(x,y)=>y!=0?x/y:0, I4Rem=(x,y)=>y!=0?x%y:0;
    public static readonly Func<long,long,long> I8Add = (x,y)=>x+y, I8Sub=(x,y)=>x-y, I8Mul=(x,y)=>x*y, I8Div=(x,y)=>x/y, I8Rem=(x,y)=>x%y;
    public static readonly Func<float,float,float> R4Add = (x,y)=>x+y, R4Sub=(x,y)=>x-y, R4Mul=(x,y)=>x*y, R4Div=(x,y)=>x/y, R4Rem=(x,y)=>x%y;
    public static readonly Func<double,double,double> R8Add = (x,y)=>x+y, R8Sub=(x,y)=>x-y, R8Mul=(x,y)=>x*y, R8Div=(x,y)=>x/y, R8Rem=(x,y)=>x%y;

    public static Value Arith(Value a, Value b,
        Func<int, int, int> opI4, Func<long, long, long> opI8,
        Func<float, float, float> opR4, Func<double, double, double> opR8)
    {
        if (a.Tag == ValueTag.I4 && b.Tag == ValueTag.I4) return Value.FromI4(opI4(a.I4, b.I4));
        if (a.Tag == ValueTag.I8 && b.Tag == ValueTag.I8) return Value.FromI8(opI8(a.I8, b.I8));
        if (a.Tag == ValueTag.R4 && b.Tag == ValueTag.R4) return Value.FromR4(opR4(a.R4, b.R4));
        if (a.Tag == ValueTag.R8 && b.Tag == ValueTag.R8) return Value.FromR8(opR8(a.R8, b.R8));
        return Value.FromR8(opR8(ToDouble(a), ToDouble(b)));
    }

    public static Value Negate(Value v) => v.Tag switch { ValueTag.I4 => Value.FromI4(-v.I4), ValueTag.I8 => Value.FromI8(-v.I8), ValueTag.R4 => Value.FromR4(-v.R4), ValueTag.R8 => Value.FromR8(-v.R8), _ => Value.Nil() };
    public static double ToDouble(Value v) => v.Tag switch { ValueTag.I4 => v.I4, ValueTag.I8 => v.I8, ValueTag.R4 => v.R4, ValueTag.R8 => v.R8, _ => 0 };
    public static int NumericCompare(Value a, Value b) => (a.Tag == ValueTag.I4 && b.Tag == ValueTag.I4) ? a.I4.CompareTo(b.I4) : (a.Tag == ValueTag.I8 && b.Tag == ValueTag.I8) ? a.I8.CompareTo(b.I8) : ToDouble(a).CompareTo(ToDouble(b));

    // ── Bytecode read helpers ──────────────────────────────────────

    internal static ushort ReadOpcode(byte[] code, ref uint pc) { int table = 0; while (code[pc] == 0xFF) { table++; pc++; } return (ushort)(table * 256 + code[pc++]); }
    internal static ushort ReadU16(byte[] code, ref uint pc) { ushort v = (ushort)(code[pc] | (code[pc + 1] << 8)); pc += 2; return v; }
    internal static uint ReadU32(byte[] code, ref uint pc) { uint v = (uint)(code[pc] | (code[pc + 1] << 8) | (code[pc + 2] << 16) | (code[pc + 3] << 24)); pc += 4; return v; }
    internal static int ReadI32(byte[] code, ref uint pc) => (int)ReadU32(code, ref pc);
    internal static long ReadI64(byte[] code, ref uint pc) { uint lo = ReadU32(code, ref pc), hi = ReadU32(code, ref pc); return (long)(lo | ((ulong)hi << 32)); }
    internal static float ReadF32(byte[] code, ref uint pc) => BitConverter.Int32BitsToSingle(ReadI32(code, ref pc));
    internal static double ReadF64(byte[] code, ref uint pc) => BitConverter.Int64BitsToDouble(ReadI64(code, ref pc));

    private VmError Err(VmErrorKind kind, string msg) => new(kind, msg, _currentFuncName);
}
