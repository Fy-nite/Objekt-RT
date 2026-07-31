using System.Runtime.InteropServices;
using ObjectRT.Abstractions;

namespace ObjectRT.VM;

// ── Call frame ──────────────────────────────────────────────────────────

internal class Frame
{
    public CompiledFunction Func { get; set; } = null!;
    public uint Pc;                // program counter
    public Value[] Locals = [];    // args + locals
    public uint StackBase;         // index into the global stack
    public uint RetPc;             // return offset in caller
    public uint RetFunc;           // caller function index
}

/// <summary>
/// Minimal stack-based interpreter for CompiledModule.
/// Iterative dispatch loop reading raw bytecode — no decoded Instruction structs, no variant dispatch.
/// </summary>
public class Interpreter
{
    private readonly CompiledModule _mod;
    private bool _trace;

    /// <summary>
    /// Optional handler invoked by the <c>callnative</c> opcode. Receives the
    /// method name and marshaled args, returns a CLR result (or throws).
    /// </summary>
    private Func<string, object?[], object?>? _nativeCall;

    /// <summary>Handler for <c>callnative</c> dispatch; set by the host Runtime.</summary>
    public Func<string, object?[], object?>? NativeCallHandler
    {
        get => _nativeCall;
        set => _nativeCall = value;
    }

    // Execution stack (global, grows up)
    private readonly List<Value> _stack = new(4096);

    // Call frame stack
    private readonly List<Frame> _frames = new(256);

    // Heap — each object is a byte buffer sized by the type's instance_size
    private readonly List<byte[]> _heap = new();

    // Static field storage
    private readonly Value[] _staticFields;

    // Interned string table (handles, mirroring the heap-object pattern —
    // the Value struct is LayoutKind.Explicit so it cannot hold CLR refs).
    private readonly Dictionary<string, uint> _stringMap = new(StringComparer.Ordinal);
    private readonly List<string?> _strings = new();

    // Reusable locals array per frame — resized on demand, cleared between calls
    private Value[] _localsScratch = Array.Empty<Value>();

    // Current function name for error context
    private string _currentFuncName = "";

    public Interpreter(CompiledModule mod)
    {
        _mod = mod;
        _staticFields = new Value[mod.Fields.Count];
        Array.Fill(_staticFields, Value.Nil());
    }

    /// <summary>
    /// Reset execution state without re-allocating internal arrays.
    /// Call this between top-level invocations when reusing an Interpreter.
    /// Keeps heap and static fields intact if you want state persistence,
    /// or clears them if you want a fully fresh start.
    /// </summary>
    public void Reset(bool clearHeap = false, bool clearStatics = false)
    {
        _stack.Clear();
        _frames.Clear();
        _currentFuncName = "";
        if (clearHeap) _heap.Clear();
        if (clearStatics)
        {
            Array.Fill(_staticFields, Value.Nil());
        }
    }

    public bool Trace { get => _trace; set => _trace = value; }

    // ── Run ───────────────────────────────────────────────────────

    public Result<Value> Run()
    {
        if (!_mod.HasEntry)
            return new VmError(VmErrorKind.UnresolvedEntryPoint, "module has no entry point");

        _stack.Clear();
        _frames.Clear();
        return RunFunction(_mod.EntryFunction);
    }

    public Result<Value> RunFunction(uint funcIdx)
    {
        return RunFunction(funcIdx, Array.Empty<Value>());
    }

    /// <summary>Run a specific function by index with the given argument values.</summary>
    public Result<Value> RunFunction(uint funcIdx, Value[] args)
    {
        if (funcIdx >= _mod.Functions.Count)
            return new VmError(VmErrorKind.InvalidFunctionIndex,
                $"function index {funcIdx} out of bounds ({_mod.Functions.Count})");

        var func = _mod.GetFunction(funcIdx);
        _currentFuncName = func.DebugName;

        // Reuse scratch locals array — resize if needed, fill with nils
        int localsLen = (int)(func.NumParams + func.NumLocals + 1);
        if (_localsScratch.Length < localsLen)
            _localsScratch = new Value[localsLen];
        Array.Fill(_localsScratch, Value.Nil(), 0, localsLen);

        var frame = new Frame
        {
            Func = func,
            Pc = 0,
            StackBase = (uint)_stack.Count,
            Locals = _localsScratch,
            RetFunc = uint.MaxValue,
            RetPc = 0,
        };

        // Copy arguments into frame locals
        for (int i = 0; i < args.Length && i < func.NumParams; i++)
            frame.Locals[i] = args[i];

        _frames.Add(frame);

        return Execute();
    }

    // ── Heap allocation ───────────────────────────────────────────

    private Result<uint> AllocObject(uint typeIdx)
    {
        if (typeIdx >= _mod.Types.Count)
            return new VmError(VmErrorKind.InvalidTypeIndex,
                $"type index {typeIdx} out of bounds ({_mod.Types.Count})");

        var type = _mod.GetType(typeIdx);
        var data = new byte[type.InstanceSize];
        uint handle = (uint)_heap.Count;
        _heap.Add(data);
        return handle;
    }

    // ── Iterative dispatch loop ───────────────────────────────────

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
                if (_trace)
                {
                    Console.Error.WriteLine($"  [{_currentFuncName} {pc}] ");
                }

                ushort op = ReadOpcode(code, ref pc);

                switch ((Opcode)op)
                {
                    case Opcode.Nop:
                        break;

                    // ── Load constant ──────────────────────────────
                    case Opcode.LdcI4:
                    case Opcode.Ldc:
                    {
                        int v = ReadI32(code, ref pc);
                        Push(Value.FromI4(v));
                        break;
                    }
                    case Opcode.LdcI8:
                    {
                        long v = ReadI64(code, ref pc);
                        Push(Value.FromI8(v));
                        break;
                    }
                    case Opcode.LdcR4:
                    {
                        float v = ReadF32(code, ref pc);
                        Push(Value.FromR4(v));
                        break;
                    }
                    case Opcode.LdcR8:
                    {
                        double v = ReadF64(code, ref pc);
                        Push(Value.FromR8(v));
                        break;
                    }

                    // ── Load string ────────────────────────────────
                    case Opcode.Ldstr:
                    {
                        ushort si = ReadU16(code, ref pc);
                        if (_trace)
                            Console.Error.WriteLine($"  ldstr \"{_mod.GetString(si)}\"");
                        Push(Value.FromStr(InternString(_mod.GetString(si))));
                        break;
                    }

                    // ── Argument access ────────────────────────────
                    case Opcode.Ldarg:
                    {
                        ushort idx = ReadU16(code, ref pc);
                        Push(frame.Locals[idx]);
                        break;
                    }
                    case Opcode.Starg:
                    {
                        ushort idx = ReadU16(code, ref pc);
                        frame.Locals[idx] = Pop();
                        break;
                    }

                    // ── Local variable access ───────────────────────
                    case Opcode.Ldloc:
                    {
                        ushort idx = ReadU16(code, ref pc);
                        Push(frame.Locals[frame.Func.NumParams + idx]);
                        break;
                    }
                    case Opcode.Stloc:
                    {
                        ushort idx = ReadU16(code, ref pc);
                        frame.Locals[frame.Func.NumParams + idx] = Pop();
                        break;
                    }

                    // ── Arithmetic (tag-aware) ─────────────────────
                    case Opcode.Add: { var b = Pop(); var a = Pop(); Push(Arith(a, b, (x, y) => x + y, (x, y) => x + y, (x, y) => x + y, (x, y) => x + y)); break; }
                    case Opcode.Sub: { var b = Pop(); var a = Pop(); Push(Arith(a, b, (x, y) => x - y, (x, y) => x - y, (x, y) => x - y, (x, y) => x - y)); break; }
                    case Opcode.Mul: { var b = Pop(); var a = Pop(); Push(Arith(a, b, (x, y) => x * y, (x, y) => x * y, (x, y) => x * y, (x, y) => x * y)); break; }
                    case Opcode.Div: { var b = Pop(); var a = Pop(); Push(Arith(a, b, (x, y) => y != 0 ? x / y : 0, (x, y) => x / y, (x, y) => x / y, (x, y) => x / y)); break; }
                    case Opcode.Rem: { var b = Pop(); var a = Pop(); Push(Arith(a, b, (x, y) => y != 0 ? x % y : 0, (x, y) => x % y, (x, y) => x % y, (x, y) => x % y)); break; }
                    case Opcode.Neg: { Push(Negate(Pop())); break; }

                    // ── Bitwise ────────────────────────────────────
                    case Opcode.And: { int b = Pop().I4, a = Pop().I4; Push(Value.FromI4(a & b)); break; }
                    case Opcode.Or:  { int b = Pop().I4, a = Pop().I4; Push(Value.FromI4(a | b)); break; }
                    case Opcode.Xor: { int b = Pop().I4, a = Pop().I4; Push(Value.FromI4(a ^ b)); break; }
                    case Opcode.Not: { Push(Value.FromI4(~Pop().I4)); break; }

                    // ── Comparison (tag-aware) ─────────────────────
                    case Opcode.Ceq: { var b = Pop(); var a = Pop(); Push(Value.FromI4(NumericCompare(a, b) == 0 ? 1 : 0)); break; }
                    case Opcode.Cne: { var b = Pop(); var a = Pop(); Push(Value.FromI4(NumericCompare(a, b) != 0 ? 1 : 0)); break; }
                    case Opcode.Cgt: { var b = Pop(); var a = Pop(); Push(Value.FromI4(NumericCompare(a, b) > 0 ? 1 : 0)); break; }
                    case Opcode.Cge: { var b = Pop(); var a = Pop(); Push(Value.FromI4(NumericCompare(a, b) >= 0 ? 1 : 0)); break; }
                    case Opcode.Clt: { var b = Pop(); var a = Pop(); Push(Value.FromI4(NumericCompare(a, b) < 0 ? 1 : 0)); break; }
                    case Opcode.Cle: { var b = Pop(); var a = Pop(); Push(Value.FromI4(NumericCompare(a, b) <= 0 ? 1 : 0)); break; }

                    // ── Stack manipulation ─────────────────────────
                    case Opcode.Dup:    { Push(Peek()); break; }
                    case Opcode.Pop:    { Pop(); break; }
                    case Opcode.Ldnull: { Push(Value.Nil()); break; }

                    // ── Field access ───────────────────────────────
                    case Opcode.Ldfld:
                    {
                        ushort fi = ReadU16(code, ref pc);
                        if (fi >= _mod.Fields.Count)
                            return Err(VmErrorKind.InvalidFieldIndex, $"ldfld invalid field index {fi}", pc);
                        var field = _mod.Fields[(int)fi];
                        var obj = Pop();
                        if (obj.Tag != ValueTag.Obj)
                            return Err(VmErrorKind.NotAnObject, "ldfld on non-object", pc);
                        uint h = obj.AsObj();
                        if (h >= _heap.Count || field.Offset + 16 > _heap[(int)h].Length)
                            return Err(VmErrorKind.OutOfBounds, "ldfld out of bounds", pc);
                        var span = _heap[(int)h].AsSpan((int)field.Offset, 16);
                        var v = MemoryMarshal.Read<Value>(span);
                        Push(v);
                        break;
                    }
                    case Opcode.Stfld:
                    {
                        ushort fi = ReadU16(code, ref pc);
                        if (fi >= _mod.Fields.Count)
                            return Err(VmErrorKind.InvalidFieldIndex, $"stfld invalid field index {fi}", pc);
                        var field = _mod.Fields[(int)fi];
                        var val = Pop();
                        var obj = Pop();
                        if (obj.Tag != ValueTag.Obj)
                            return Err(VmErrorKind.NotAnObject, "stfld on non-object", pc);
                        uint h = obj.AsObj();
                        if (h >= _heap.Count || field.Offset + 16 > _heap[(int)h].Length)
                            return Err(VmErrorKind.OutOfBounds, "stfld out of bounds", pc);
                        var span = _heap[(int)h].AsSpan((int)field.Offset, 16);
                        MemoryMarshal.Write(span, in val);
                        break;
                    }
                    case Opcode.Ldsfld:
                    {
                        ushort fi = ReadU16(code, ref pc);
                        if (fi >= _staticFields.Length)
                            return Err(VmErrorKind.InvalidFieldIndex, $"ldsfld invalid static field index {fi}", pc);
                        Push(_staticFields[fi]);
                        break;
                    }
                    case Opcode.Stsfld:
                    {
                        ushort fi = ReadU16(code, ref pc);
                        if (fi >= _staticFields.Length)
                            return Err(VmErrorKind.InvalidFieldIndex, $"stsfld invalid static field index {fi}", pc);
                        _staticFields[fi] = Pop();
                        break;
                    }

                    // ── Call (module function first, then native fallback) ──
                    case Opcode.Call:
                    case Opcode.Callvirt:
                    {
                        ushort si = ReadU16(code, ref pc);
                        ushort argc = ReadU16(code, ref pc);
                        string name = _mod.GetString(si);

                        // 1. Script function defined in the module.
                        if (_mod.FunctionMap.TryGetValue(name, out var fi))
                        {
                            if (fi >= _mod.Functions.Count)
                                return Err(VmErrorKind.InvalidFunctionIndex, $"call invalid function index {fi}", pc);

                            var callee = _mod.GetFunction(fi);
                            if (callee.Code.Length == 0)
                            {
                                Push(Value.Nil());
                                break;
                            }

                            // Pop arguments from the stack into the callee's locals
                            var locals = new Value[callee.NumParams + callee.NumLocals + 1];
                            Array.Fill(locals, Value.Nil());
                            for (int ai = (int)callee.NumParams - 1; ai >= 0; ai--)
                                locals[ai] = Pop();

                            var calleeFrame = new Frame
                            {
                                Func = callee,
                                Pc = 0,
                                StackBase = (uint)_stack.Count,
                                Locals = locals,
                                RetFunc = frame.Func.SelfIndex,
                                RetPc = pc,
                            };
                            _frames.Add(calleeFrame);
                            goto nextFrame;
                        }

                        // 2. Native / host method fallback.
                        var handler = _nativeCall;
                        if (handler == null)
                            return Err(VmErrorKind.UnresolvedMethod,
                                $"call '{name}' but no native call handler is registered", pc);

                        if (_stack.Count < argc)
                            return Err(VmErrorKind.StackUnderflow,
                                $"call '{name}' needs {argc} args but the stack has {_stack.Count}", pc);

                        // Pop args in reverse so args[0] is the first argument.
                        var args = new object?[argc];
                        for (int ai = argc - 1; ai >= 0; ai--)
                            args[ai] = ValueToObject(Pop());

                        object? result;
                        try
                        {
                            result = handler(name, args);
                        }
                        catch (Exception ex)
                        {
                            return Err(VmErrorKind.RuntimeError,
                                $"call '{name}' threw: {ex.Message}", pc);
                        }
                        Push(MarshalValue(result));
                        break;
                    }

                    // ── Native call (host-resolved) ──────────────────
                    case Opcode.NativeCall:
                    {
                        ushort si = ReadU16(code, ref pc);
                        ushort argc = ReadU16(code, ref pc);
                        string name = _mod.GetString(si);

                        var handler = _nativeCall;
                        if (handler == null)
                            return Err(VmErrorKind.UnresolvedMethod,
                                $"callnative '{name}' but no native call handler is registered", pc);

                        if (_stack.Count < argc)
                            return Err(VmErrorKind.StackUnderflow,
                                $"callnative '{name}' needs {argc} args but the stack has {_stack.Count}", pc);

                        // Pop args in reverse so args[0] is the first argument.
                        var args = new object?[argc];
                        for (int ai = argc - 1; ai >= 0; ai--)
                            args[ai] = ValueToObject(Pop());

                        object? result;
                        try
                        {
                            result = handler(name, args);
                        }
                        catch (Exception ex)
                        {
                            return Err(VmErrorKind.RuntimeError,
                                $"callnative '{name}' threw: {ex.Message}", pc);
                        }
                        Push(MarshalValue(result));
                        break;
                    }

                    // ── Return ───────────────────────────────────────
                    case Opcode.Ret:
                    {
                        var retval = _stack.Count > 0 ? _stack[^1] : Value.Nil();
                        if (_stack.Count > 0) _stack.RemoveAt(_stack.Count - 1);

                        uint retFunc = frame.RetFunc;
                        uint retPcVal = frame.RetPc;
                        _frames.RemoveAt(_frames.Count - 1);

                        if (_frames.Count == 0)
                        {
                            Push(retval);
                            return retval;
                        }

                        Push(retval);
                        _frames[^1].Pc = retPcVal;
                        goto nextFrame;
                    }

                    // ── Branches ──────────────────────────────────────
                    case Opcode.Br:
                    {
                        int off = ReadI32(code, ref pc);
                        pc = (uint)((int)pc + off);
                        break;
                    }
                    case Opcode.Brfalse:
                    {
                        int off = ReadI32(code, ref pc);
                        bool taken = !Pop().IsTruthy();
                        if (taken) pc = (uint)((int)pc + off);
                        break;
                    }
                    case Opcode.Brtrue:
                    {
                        int off = ReadI32(code, ref pc);
                        bool taken = Pop().IsTruthy();
                        if (taken) pc = (uint)((int)pc + off);
                        break;
                    }

                    // ── Object ops ──────────────────────────────────
                    case Opcode.Newobj:
                    {
                        ushort ti = ReadU16(code, ref pc);
                        if (ti >= _mod.Types.Count)
                            return Err(VmErrorKind.InvalidTypeIndex, $"newobj invalid type index {ti}", pc);
                        var allocResult = AllocObject(ti);
                        if (allocResult.IsError) return allocResult.Error;
                        Push(Value.FromObj(allocResult.Value));
                        break;
                    }
                    case Opcode.Newarr: { ReadU16(code, ref pc); Push(Value.Nil()); break; }
                    case Opcode.Ldelem: { Pop(); Pop(); Push(Value.Nil()); break; }
                    case Opcode.Stelem: { Pop(); Pop(); Pop(); break; }

                    // ── Type ops (stubs) ─────────────────────────────
                    case Opcode.Conv:      { ReadU16(code, ref pc); break; }
                    case Opcode.Castclass: { ReadU16(code, ref pc); break; }
                    case Opcode.Isinst:    { ReadU16(code, ref pc); break; }

                    // ── Structured control flow (skip embedded blocks) ─
                    case Opcode.If:
                    case Opcode.While:
                    {
                        byte ck = code[pc++];
                        if (ck == 0x01) pc++;         // binary comparison byte
                        else if (ck >= 0x02) { uint len = ReadU32(code, ref pc); pc += len; }
                        break;
                    }
                    case Opcode.Try:
                    {
                        uint tl = ReadU32(code, ref pc); pc += tl;
                        ushort cc = ReadU16(code, ref pc);
                        for (ushort ci = 0; ci < cc; ci++)
                        {
                            ReadU16(code, ref pc); // type index
                            uint bl = ReadU32(code, ref pc); pc += bl;
                        }
                        if (code[pc++] != 0) { uint fl = ReadU32(code, ref pc); pc += fl; }
                        break;
                    }
                    case Opcode.Throw:
                    case Opcode.Break:
                    case Opcode.Continue:
                        break;

                    default:
                        break;
                }

                // Update frame PC after each instruction
                frame.Pc = pc;
            }

            // Function fell through without Ret
            if (_frames.Count > 0) _frames.RemoveAt(_frames.Count - 1);

            nextFrame:;
        }

        return Value.Nil();
    }

    // ── Value stack operations ───────────────────────────────────

    private void Push(Value v) => _stack.Add(v);

    private Value Pop()
    {
        if (_stack.Count == 0)
        {
            Console.Error.WriteLine($"VM BUG: stack underflow in {_currentFuncName}");
            throw new InvalidOperationException($"Stack underflow in {_currentFuncName}");
        }
        var v = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        return v;
    }

    private Value Peek(int depth = 0) => _stack[^ (1 + depth)];

    // ── String table ──────────────────────────────────────────────

    /// <summary>Intern a CLR string and return its handle.</summary>
    public uint InternString(string s)
    {
        if (_stringMap.TryGetValue(s, out var idx)) return idx;
        idx = (uint)_strings.Count;
        _strings.Add(s);
        _stringMap[s] = idx;
        return idx;
    }

    /// <summary>Resolve a string handle to its CLR string, or null.</summary>
    public string? GetStringValue(uint idx)
        => idx < _strings.Count ? _strings[(int)idx] : null;

    /// <summary>Marshal a CLR value into a VM value (strings get interned).</summary>
    public Value MarshalValue(object? val) => val switch
    {
        null => Value.Nil(),
        string s => Value.FromStr(InternString(s)),
        _ => Value.FromObject(val),
    };

    /// <summary>Unbox a VM value to a CLR object (strings resolve via the table).</summary>
    public object? ValueToObject(Value v) => v.Tag switch
    {
        ValueTag.Str => GetStringValue(v.AsStr()),
        _ => Value.ToObject(v),
    };

    // ── Tag-aware arithmetic ─────────────────────────────────────

    /// <summary>
    /// Numeric promotion for binary arithmetic: int+int stays int, long+long
    /// stays long, float+float stays float; anything mixed widens to double.
    /// </summary>
    private static Value Arith(Value a, Value b,
        Func<int, int, int> opI4, Func<long, long, long> opI8,
        Func<float, float, float> opR4, Func<double, double, double> opR8)
    {
        if (a.Tag == ValueTag.I4 && b.Tag == ValueTag.I4) return Value.FromI4(opI4(a.I4, b.I4));
        if (a.Tag == ValueTag.I8 && b.Tag == ValueTag.I8) return Value.FromI8(opI8(a.I8, b.I8));
        if (a.Tag == ValueTag.R4 && b.Tag == ValueTag.R4) return Value.FromR4(opR4(a.R4, b.R4));
        if (a.Tag == ValueTag.R8 && b.Tag == ValueTag.R8) return Value.FromR8(opR8(a.R8, b.R8));

        return Value.FromR8(opR8(ToDouble(a), ToDouble(b)));
    }

    private static Value Negate(Value v) => v.Tag switch
    {
        ValueTag.I4 => Value.FromI4(-v.I4),
        ValueTag.I8 => Value.FromI8(-v.I8),
        ValueTag.R4 => Value.FromR4(-v.R4),
        ValueTag.R8 => Value.FromR8(-v.R8),
        _           => Value.Nil(),
    };

    private static double ToDouble(Value v) => v.Tag switch
    {
        ValueTag.I4 => v.I4,
        ValueTag.I8 => v.I8,
        ValueTag.R4 => v.R4,
        ValueTag.R8 => v.R8,
        _           => 0,
    };

    private static int NumericCompare(Value a, Value b)
    {
        if (a.Tag == ValueTag.I4 && b.Tag == ValueTag.I4)
            return a.I4.CompareTo(b.I4);
        if (a.Tag == ValueTag.I8 && b.Tag == ValueTag.I8)
            return a.I8.CompareTo(b.I8);
        return ToDouble(a).CompareTo(ToDouble(b));
    }

    // ── Bytecode read helpers ─────────────────────────────────────

    /// <summary>Read a variable-length opcode. Consumes 0xFF prefix bytes
    /// for extension tables (like x86 opcode prefixes).</summary>
    private static ushort ReadOpcode(byte[] code, ref uint pc)
    {
        int table = 0;
        while (code[pc] == 0xFF)
        {
            table++;
            pc++;
        }
        return (ushort)(table * 256 + code[pc++]);
    }

    private static ushort ReadU16(byte[] code, ref uint pc)
    {
        ushort v = (ushort)(code[pc] | (code[pc + 1] << 8));
        pc += 2;
        return v;
    }

    private static uint ReadU32(byte[] code, ref uint pc)
    {
        uint v = (uint)(code[pc] | (code[pc + 1] << 8) | (code[pc + 2] << 16) | (code[pc + 3] << 24));
        pc += 4;
        return v;
    }

    private static int ReadI32(byte[] code, ref uint pc) => (int)ReadU32(code, ref pc);

    private static long ReadI64(byte[] code, ref uint pc)
    {
        uint lo = ReadU32(code, ref pc);
        uint hi = ReadU32(code, ref pc);
        return (long)(lo | ((ulong)hi << 32));
    }

    private static float ReadF32(byte[] code, ref uint pc)
    {
        int bits = ReadI32(code, ref pc);
        return BitConverter.Int32BitsToSingle(bits);
    }

    private static double ReadF64(byte[] code, ref uint pc)
    {
        long bits = ReadI64(code, ref pc);
        return BitConverter.Int64BitsToDouble(bits);
    }

    // ── Error helper ──────────────────────────────────────────────

    private VmError Err(VmErrorKind kind, string msg, uint pc) =>
        new(kind, msg, _currentFuncName, pc);

}
