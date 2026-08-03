using System.Globalization;
using ObjectRT.Abstractions;

namespace ObjectRT.VM;

/// <summary>
/// Compiles an ORBTModule (tooling representation) into a CompiledModule
/// (VM-friendly flat representation with resolved indices).
/// </summary>
public class ModuleCompiler
{
    // ── Resolution tables ──────────────────────────────────────────

    private readonly Dictionary<string, uint> _typeMap = new();
    private readonly Dictionary<string, uint> _fieldMap = new();
    private readonly List<ResolvedFunction> _resolvedFuncs = new();
    private readonly Dictionary<string, uint> _funcMap = new();

    private struct ResolvedFunction
    {
        public string FullName;
        public uint OldMethodIdx;
        public uint NewIndex;
    }

    // ── Per-function compilation state ─────────────────────────────

    private class CompileState
    {
        public List<byte> Code { get; } = new();
        public uint MaxStackDepth;
        public uint CurrentDepth;
        public string? Error;

        public void Reset()
        {
            Code.Clear();
            MaxStackDepth = 0;
            CurrentDepth = 0;
            Error = null;
        }
    }

    // ── Public API ─────────────────────────────────────────────────

    public Result<CompiledModule> Compile(ORBTModule src)
    {
        var mod = new CompiledModule();

        BuildResolutionTables(src);

        // 2. Compile types
        mod.Types.Capacity = src.Types.Count;
        mod.Fields.Capacity = Math.Max(_fieldMap.Count, 1);
        mod.Functions.Capacity = Math.Max(_resolvedFuncs.Count, 1);
        mod.FunctionMap.Clear();

        uint fieldIdx = 0;

        foreach (var srcType in src.Types)
        {
            var vmt = new VMType
            {
                DebugName = src.Resolve(srcType.NameIndex),
                Kind = (VMTypeKind)(byte)srcType.Kind,
                BaseType = srcType.BaseTypeIndex,
                FieldOffset = fieldIdx,
                FieldCount = srcType.FieldCount,
                MethodCount = srcType.MethodCount,
                InstanceSize = (uint)srcType.FieldCount * VmConstants.FieldSlotSize,
            };

            // Find method offset in the function table
            for (int mi = 0; mi < srcType.Methods.Count; mi++)
            {
                string fname = MethodFullName(src, srcType, srcType.Methods[mi]);
                if (_funcMap.TryGetValue(fname, out var funcIdx) && mi == 0)
                    vmt.MethodOffset = funcIdx;
            }

            mod.Types.Add(vmt);

            // Collect fields
            for (int fi = 0; fi < srcType.Fields.Count; fi++)
            {
                var srcField = srcType.Fields[fi];
                var vmf = new VMField
                {
                    DebugName = src.Resolve(srcField.NameIndex),
                    TypeIndex = 0,
                    Offset = (uint)fi * VmConstants.FieldSlotSize,
                };
                mod.Fields.Add(vmf);
                fieldIdx++;
            }
        }

        // 3. Compile functions
        foreach (var rf in _resolvedFuncs)
        {
            bool found = false;
            foreach (var srcType in src.Types)
            {
                for (int mi = 0; mi < srcType.Methods.Count; mi++)
                {
                    string fname = MethodFullName(src, srcType, srcType.Methods[mi]);
                    if (fname == rf.FullName)
                    {
                        var cfResult = CompileMethod(src, srcType, srcType.Methods[mi], rf.FullName);
                        if (cfResult.IsError)
                            return new VmError(VmErrorKind.UnresolvedField,
                                $"compilation of '{rf.FullName}' failed: {cfResult.Error.Message}");

                        var cf = cfResult.Value;
                        cf.SelfIndex = rf.NewIndex;
                        mod.Functions.Add(cf);
                        mod.FunctionMap[rf.FullName] = rf.NewIndex;
                        found = true;
                        break;
                    }
                }
                if (found) break;
            }

            if (!found)
            {
                // Fallback — empty function that just returns
                var cf = new CompiledFunction
                {
                    DebugName = rf.FullName,
                    SelfIndex = rf.NewIndex,
                    Code = new byte[] { (byte)Opcode.Ret },
                };
                mod.Functions.Add(cf);
                mod.FunctionMap[rf.FullName] = rf.NewIndex;
            }
        }

        // 4. Copy string table
        mod.Strings = new List<string>(src.StringPool.Strings);

        // 5. Set entry point — look for any "*.Main" function
        string entryName = $"{src.ModuleName}.Main";
        if (mod.FunctionMap.TryGetValue(entryName, out var entryIdx))
            mod.EntryFunction = entryIdx;
        else if (mod.FunctionMap.TryGetValue("Main", out entryIdx))
            mod.EntryFunction = entryIdx;
        else {
            // Search for any function named "*.Main"
            foreach (var kv in mod.FunctionMap)
            {
                var name = kv.Key;
                if (name.EndsWith(".Main") || name == "Main")
                {
                    mod.EntryFunction = kv.Value;
                    goto found;
                }
            }
            // Fallback: first function
            if (mod.Functions.Count > 0)
                mod.EntryFunction = 0;
            found:;
        }

        return mod;
    }

    // ── Resolution table builder ───────────────────────────────────

    private void BuildResolutionTables(ORBTModule src)
    {
        _typeMap.Clear();
        _fieldMap.Clear();
        _resolvedFuncs.Clear();
        _funcMap.Clear();

        foreach (var type in src.Types)
        {
            string tname = src.Resolve(type.NameIndex);
            _typeMap[tname] = (uint)_typeMap.Count;

            foreach (var field in type.Fields)
            {
                string fname = FieldFullName(src, type, field);
                _fieldMap[fname] = (uint)_fieldMap.Count;
            }

            foreach (var method in type.Methods)
            {
                var rf = new ResolvedFunction
                {
                    FullName = MethodFullName(src, type, method),
                    OldMethodIdx = (uint)_resolvedFuncs.Count,
                    NewIndex = (uint)_resolvedFuncs.Count,
                };
                _funcMap[rf.FullName] = rf.NewIndex;
                _resolvedFuncs.Add(rf);
            }
        }
    }

    // ── Name helpers ───────────────────────────────────────────────

    private static string MethodFullName(ORBTModule src, TypeRecord type, MethodRecord method)
        => $"{src.Resolve(type.NameIndex)}.{src.Resolve(method.NameIndex)}";

    private static string FieldFullName(ORBTModule src, TypeRecord type, FieldRecord field)
        => $"{src.Resolve(type.NameIndex)}.{src.Resolve(field.NameIndex)}";

    // ── Compile a single method ────────────────────────────────────

    private Result<CompiledFunction> CompileMethod(ORBTModule src, TypeRecord type, MethodRecord method, string fullName)
    {
        // Build name→position mapping for args and locals
        var argNameToPos = new Dictionary<string, ushort>(StringComparer.Ordinal);
        for (ushort pi = 0; pi < method.Params.Count; pi++)
        {
            string pname = src.Resolve(method.Params[pi].NameIndex);
            argNameToPos[pname] = pi;
        }
        var localNameToPos = new Dictionary<string, ushort>(StringComparer.Ordinal);
        for (ushort li = 0; li < method.Locals.Count; li++)
        {
            string lname = src.Resolve(method.Locals[li].NameIndex);
            localNameToPos[lname] = li;
        }
        var func = new CompiledFunction
        {
            DebugName = fullName,
            NumParams = method.ParamCount,
            NumLocals = method.LocalCount,
            MaxStack = 0,
        };
        func.SourceMap = method.LineMappings;

        var state = new CompileState();

        // If we have raw bytecode but no decoded instructions, decode them now
        // so we can resolve references (call, ldfld, etc.) properly.
        if (method.Instructions.Count == 0 && method.RawInstructionData.Length > 0)
        {
            // Decode raw bytecode into Instruction structs using BinaryStream
            try
            {
                var decoded = DecodeRawBytecode(method.RawInstructionData, src);
                if (decoded.Count > 0)
                {
                    method.Instructions = decoded;
                }
                else
                {
                    // Passthrough as-is (no references to resolve)
                    func.Code = method.RawInstructionData;
                    func.MaxStack = 8;
                    return func;
                }
            }
            catch
            {
                // Passthrough on decode failure
                func.Code = method.RawInstructionData;
                func.MaxStack = 8;
                return func;
            }
        }

        if (method.Instructions.Count == 0)
        {
            func.Code = new byte[] { (byte)Opcode.Ret };
            return func;
        }

        // ── Pass 1: compute new PC for each instruction ──────────────
        var newPcByIdx = new List<uint>(method.Instructions.Count);
        uint newPc = 0;
        foreach (var instr in method.Instructions)
        {
            newPcByIdx.Add(newPc);
            newPc += GetOpcodeByteCount(instr.Opcode) + ComputeOperandSize(instr);
        }
        uint totalNewSize = newPc;

        // ── Pass 2: emit bytecode ────────────────────────────────────
        state.Code.Capacity = (int)totalNewSize;

        for (int i = 0; i < method.Instructions.Count; i++)
        {
            var instr = method.Instructions[i];

            // Emit opcode (variable-length for extension tables)
            EmitOpcode(state, (ushort)instr.Opcode);

            // Stack depth tracking
            switch (instr.Opcode)
            {
                case Opcode.LdcI4: case Opcode.LdcI8:
                case Opcode.LdcR4: case Opcode.LdcR8:
                case Opcode.Ldc: case Opcode.Ldstr:
                case Opcode.Ldarg: case Opcode.Ldloc:
                case Opcode.Ldfld: case Opcode.Ldsfld:
                case Opcode.Ldnull: case Opcode.Dup:
                case Opcode.Newobj: case Opcode.Newarr:
                    state.CurrentDepth++;
                    break;

                case Opcode.Starg: case Opcode.Stloc:
                case Opcode.Stfld: case Opcode.Stsfld:
                case Opcode.Pop: case Opcode.Ret:
                case Opcode.Throw:
                    if (state.CurrentDepth > 0) state.CurrentDepth--;
                    break;

                case Opcode.Add: case Opcode.Sub: case Opcode.Mul:
                case Opcode.Div: case Opcode.Rem:
                case Opcode.Ceq: case Opcode.Cne:
                case Opcode.Cgt: case Opcode.Cge:
                case Opcode.Clt: case Opcode.Cle:
                case Opcode.And: case Opcode.Xor: case Opcode.Or:
                    // pop 2, push 1 → net -1
                    if (state.CurrentDepth > 0) state.CurrentDepth--;
                    break;

                case Opcode.Ldelem:
                    // pop 2, push 1 → net -1
                    if (state.CurrentDepth > 0) state.CurrentDepth--;
                    break;

                case Opcode.Stelem:
                    // pop 3, push 0 → net -3
                    if (state.CurrentDepth > 0) state.CurrentDepth--;
                    if (state.CurrentDepth > 0) state.CurrentDepth--;
                    if (state.CurrentDepth > 0) state.CurrentDepth--;
                    break;

                case Opcode.Neg: case Opcode.Not:
                case Opcode.Call: case Opcode.Callvirt:
                case Opcode.Ldlen: // pop 1, push 1 → net 0
                    // neutral for now
                    break;
            }

            if (state.CurrentDepth > state.MaxStackDepth)
                state.MaxStackDepth = state.CurrentDepth;

            // ── Emit operand ─────────────────────────────────────────
            switch (instr.Opcode)
            {
                case Opcode.Nop: case Opcode.Add: case Opcode.Sub:
                case Opcode.Mul: case Opcode.Div: case Opcode.Rem:
                case Opcode.Neg: case Opcode.Ceq: case Opcode.Cne:
                case Opcode.Cgt: case Opcode.Cge: case Opcode.Clt:
                case Opcode.Cle: case Opcode.And: case Opcode.Xor:
                case Opcode.Or: case Opcode.Not: case Opcode.Dup:
                case Opcode.Pop: case Opcode.Ldnull: case Opcode.Ret:
                case Opcode.Break: case Opcode.Continue:
                case Opcode.Throw: case Opcode.Ldelem: case Opcode.Stelem:
                    break;

                case Opcode.LdcI4:
                case Opcode.Ldc:
                    EmitI32(state, ((OperandI4)instr.Operand).Value);
                    break;

                case Opcode.LdcI8:
                    EmitI64(state, ((OperandI8)instr.Operand).Value);
                    break;

                case Opcode.LdcR4:
                    EmitF32(state, ((OperandR4)instr.Operand).Value);
                    break;

                case Opcode.LdcR8:
                    EmitF64(state, ((OperandR8)instr.Operand).Value);
                    break;

                case Opcode.Ldstr:
                    EmitU16(state, ((OperandString)instr.Operand).StringIndex);
                    break;

                case Opcode.Ldarg: case Opcode.Starg:
                {
                    ushort rawIdx = ((OperandIndex)instr.Operand).Index;
                    // If the index is within the string pool, it might be a name — resolve to positional index
                    if (rawIdx < src.StringPool.Count)
                    {
                        string name = src.Resolve(rawIdx);
                        if (argNameToPos.TryGetValue(name, out var pos))
                            rawIdx = pos;
                    }
                    EmitU16(state, rawIdx);
                    break;
                }
                case Opcode.Ldloc: case Opcode.Stloc:
                {
                    ushort rawIdx = ((OperandIndex)instr.Operand).Index;
                    if (rawIdx < src.StringPool.Count)
                    {
                        string name = src.Resolve(rawIdx);
                        if (localNameToPos.TryGetValue(name, out var pos))
                            rawIdx = pos;
                    }
                    EmitU16(state, rawIdx);
                    break;
                }

                case Opcode.Ldfld: case Opcode.Stfld:
                case Opcode.Ldsfld: case Opcode.Stsfld:
                {
                    string fieldRef = src.Resolve(((OperandFieldRef)instr.Operand).StringIndex);
                    if (_fieldMap.TryGetValue(fieldRef, out var fi))
                        EmitU16(state, (ushort)fi);
                    else
                    {
                        state.Error ??= $"Unresolved field '{fieldRef}' in {fullName}";
                        EmitU16(state, 0);
                    }
                    break;
                }

                case Opcode.Call: case Opcode.Callvirt:
                case Opcode.NativeCall:
                {
                    // Method name string index + parameter count. Resolution is
                    // deferred to runtime: module function first, then the host
                    // native dispatch (ClrNativeResolver / InterfaceHostResolver).
                    var nc = (OperandNativeCall)instr.Operand;
                    EmitU16(state, nc.StringIndex);
                    EmitU16(state, nc.ParamCount);
                    break;
                }

                case Opcode.Newobj: case Opcode.Newarr:
                {
                    string typeRef = src.Resolve(((OperandString)instr.Operand).StringIndex);
                    // newarr's operand is the ELEMENT type name (e.g. "int32"),
                    // which is never a declared module type. The runtime arrays
                    // are untyped (CLR object arrays), so the operand is
                    // informational — don't require it to resolve.
                    if (instr.Opcode == Opcode.Newarr)
                    {
                        EmitU16(state, 0);
                    }
                    else if (_typeMap.TryGetValue(typeRef, out var ti))
                    {
                        EmitU16(state, (ushort)ti);
                    }
                    else
                    {
                        state.Error ??= $"Unresolved type '{typeRef}' in {fullName}";
                        EmitU16(state, 0);
                    }
                    break;
                }

                case Opcode.Conv: case Opcode.Castclass: case Opcode.Isinst:
                {
                    string typeRef = src.Resolve(((OperandTypeRef)instr.Operand).StringIndex);
                    if (_typeMap.TryGetValue(typeRef, out var ti))
                        EmitU16(state, (ushort)ti);
                    else
                    {
                        state.Error ??= $"Unresolved type '{typeRef}' in {fullName}";
                        EmitU16(state, 0);
                    }
                    break;
                }

                case Opcode.Br: case Opcode.Brtrue: case Opcode.Brfalse:
                {
                    var val = (OperandBranch)instr.Operand;
                    uint instrByteCount = GetOpcodeByteCount(instr.Opcode);
                    uint oldInstrSize = instrByteCount + 4;
                    uint oldTarget = instr.PcOffset + oldInstrSize + (uint)val.PcOffset;
                    uint newTarget = OldPcToNew(method.Instructions, newPcByIdx, oldTarget, totalNewSize);
                    uint newBranchPc = newPcByIdx[i];
                    uint newInstrSize = instrByteCount + 4;
                    int newOffset = (int)(newTarget - (newBranchPc + newInstrSize));
                    EmitI32(state, newOffset);
                    break;
                }

                case Opcode.If: case Opcode.While:
                {
                    var cond = (ConditionOperand)instr.Operand;
                    state.Code.Add((byte)cond.Kind);
                    if (cond.Kind == ConditionKind.Binary)
                        state.Code.Add(cond.Comparison);
                    else if (cond.Kind is ConditionKind.Expression or ConditionKind.Block)
                    {
                        EmitU32(state, (uint)(cond.EmbeddedData?.Length ?? 0));
                        if (cond.EmbeddedData != null)
                            state.Code.AddRange(cond.EmbeddedData);
                    }
                    break;
                }

                case Opcode.Try:
                {
                    var eh = (ExceptionHandlerOperand)instr.Operand;
                    EmitU32(state, (uint)(eh.TryBlock?.Length ?? 0));
                    if (eh.TryBlock != null) state.Code.AddRange(eh.TryBlock);
                    EmitU16(state, (ushort)(eh.CatchRecords?.Length ?? 0));
                    if (eh.CatchRecords != null)
                    {
                        foreach (var cr in eh.CatchRecords)
                        {
                            EmitU16(state, cr.TypeIndex);
                            EmitU32(state, (uint)(cr.Body?.Length ?? 0));
                            if (cr.Body != null) state.Code.AddRange(cr.Body);
                        }
                    }
                    state.Code.Add(eh.HasFinally ? (byte)1 : (byte)0);
                    if (eh.HasFinally && eh.FinallyBlock != null)
                    {
                        EmitU32(state, (uint)eh.FinallyBlock.Length);
                        state.Code.AddRange(eh.FinallyBlock);
                    }
                    break;
                }
            }
        }

        if (state.Error != null)
            return new VmError(VmErrorKind.UnresolvedField, state.Error);

        func.Code = state.Code.ToArray();
        func.MaxStack = state.MaxStackDepth + 8;
        return func;
    }

    // ── Opcode byte count (variable-length for extension tables) ──

    /// <summary>Number of bytes the opcode encoding occupies in the bytecode stream.
    /// Table 0 = 1 byte, table 1 = 2 bytes (0xFF + opcode), etc.</summary>
    private static uint GetOpcodeByteCount(Opcode op)
    {
        uint val = (ushort)op;
        return 1 + (val / 256);
    }

    // ── Opcode emit ───────────────────────────────────────────────

    /// <summary>Emit a variable-length opcode: 0xFF prefix bytes + final byte.</summary>
    private static void EmitOpcode(CompileState s, ushort op)
    {
        var val = op;
        while (val >= 256)
        {
            s.Code.Add(0xFF);
            val -= 256;
        }
        s.Code.Add((byte)val);
    }

    // ── Old PC → new PC remapping ─────────────────────────────────

    private static uint OldPcToNew(List<Instruction> instructions, List<uint> newPcByIdx, uint oldPc, uint totalNewSize)
    {
        int lo = 0, hi = instructions.Count;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (instructions[mid].PcOffset < oldPc)
                lo = mid + 1;
            else
                hi = mid;
        }
        if (lo < instructions.Count && instructions[lo].PcOffset == oldPc)
            return newPcByIdx[lo];
        return lo >= instructions.Count ? totalNewSize : newPcByIdx[lo];
    }

    // ── Operand size ───────────────────────────────────────────────

    private static uint ComputeOperandSize(Instruction instr)
    {
        return instr.Opcode switch
        {
            Opcode.Nop or Opcode.Add or Opcode.Sub or Opcode.Mul
                or Opcode.Div or Opcode.Rem or Opcode.Neg
                or Opcode.Ceq or Opcode.Cne or Opcode.Cgt or Opcode.Cge
                or Opcode.Clt or Opcode.Cle
                or Opcode.And or Opcode.Xor or Opcode.Or
                or Opcode.Not or Opcode.Dup or Opcode.Pop
                or Opcode.Ldnull or Opcode.Ret or Opcode.Break
                or Opcode.Continue or Opcode.Throw
                or Opcode.Ldelem or Opcode.Stelem or Opcode.Ldlen
                => 0,

            Opcode.LdcI4 or Opcode.Ldc or Opcode.LdcR4 => 4,
            Opcode.LdcI8 or Opcode.LdcR8 => 8,

            Opcode.Ldarg or Opcode.Starg or Opcode.Ldloc or Opcode.Stloc
                or Opcode.Ldstr or Opcode.Newobj or Opcode.Newarr
                or Opcode.Conv or Opcode.Castclass or Opcode.Isinst
                or Opcode.Ldfld or Opcode.Stfld
                or Opcode.Ldsfld or Opcode.Stsfld
                => 2,

            Opcode.Call or Opcode.Callvirt => 4,
            Opcode.NativeCall => 4,
            Opcode.Br or Opcode.Brtrue or Opcode.Brfalse => 4,

            Opcode.If or Opcode.While => 5,
            Opcode.Try => 9,

            _ => 0,
        };
    }

    // ── Encoding helpers ───────────────────────────────────────────

    private static void EmitU16(CompileState s, ushort v)
    {
        s.Code.Add((byte)(v & 0xFF));
        s.Code.Add((byte)((v >> 8) & 0xFF));
    }

    private static void EmitU32(CompileState s, uint v)
    {
        s.Code.Add((byte)(v & 0xFF));
        s.Code.Add((byte)((v >> 8) & 0xFF));
        s.Code.Add((byte)((v >> 16) & 0xFF));
        s.Code.Add((byte)((v >> 24) & 0xFF));
    }

    private static void EmitI32(CompileState s, int v) => EmitU32(s, (uint)v);

    private static void EmitI64(CompileState s, long v)
    {
        EmitU32(s, (uint)(v & 0xFFFFFFFF));
        EmitU32(s, (uint)(((ulong)v >> 32) & 0xFFFFFFFF));
    }

    private static void EmitF32(CompileState s, float v)
    {
        int bits = BitConverter.SingleToInt32Bits(v);
        EmitI32(s, bits);
    }

    private static void EmitF64(CompileState s, double v)
    {
        long bits = BitConverter.DoubleToInt64Bits(v);
        EmitI64(s, bits);
    }

    // ── Decode raw bytecode into Instructions for resolution ───────

    private static List<Instruction> DecodeRawBytecode(byte[] raw, ORBTModule src)
    {
        var stream = new ObjectRT.Reader.BinaryStream(raw);
        var instructions = new List<Instruction>();

        var pool = src.StringPool;
        uint pc = 0;

        while (stream.Position < stream.Length)
        {
            uint startPc = pc;
            var opcode = ReadRawOpcode(stream, ref pc);
            var operand = ReadRawOperand(stream, opcode, pool, ref pc);
            instructions.Add(new Instruction(opcode, operand, startPc));
        }

        return instructions;
    }

    private static Opcode ReadRawOpcode(ObjectRT.Reader.BinaryStream s, ref uint pc)
    {
        int table = 0;
        while (true)
        {
            byte b = s.ReadU8(); pc++;
            if (b == 0xFF)
            {
                table++;
                if (table > 255)
                    throw new InvalidDataException("Opcode table overflow");
                continue;
            }
            return (Opcode)(table * 256 + b);
        }
    }

    private static Operand ReadRawOperand(ObjectRT.Reader.BinaryStream s, Opcode opcode, StringPool pool, ref uint pc)
    {
        switch (opcode)
        {
            case Opcode.Nop: case Opcode.Add: case Opcode.Sub: case Opcode.Mul:
            case Opcode.Div: case Opcode.Rem: case Opcode.Neg:
            case Opcode.Ceq: case Opcode.Cne: case Opcode.Cgt: case Opcode.Cge:
            case Opcode.Clt: case Opcode.Cle: case Opcode.And: case Opcode.Xor: case Opcode.Or:
            case Opcode.Not: case Opcode.Dup: case Opcode.Pop: case Opcode.Ldnull:
            case Opcode.Ret: case Opcode.Break: case Opcode.Continue: case Opcode.Throw:
            case Opcode.Ldelem: case Opcode.Stelem:
                return new OperandNone();

            case Opcode.LdcI4:
            case Opcode.Ldc:
                { var v = s.ReadI32(); pc += 4; return new OperandI4(v); }

            case Opcode.LdcI8:
                { var v = s.ReadI64(); pc += 8; return new OperandI8(v); }

            case Opcode.LdcR4:
                { var v = s.ReadR4(); pc += 4; return new OperandR4(v); }

            case Opcode.LdcR8:
                { var v = s.ReadR8(); pc += 8; return new OperandR8(v); }

            case Opcode.Ldstr:
            case Opcode.Newobj:
            case Opcode.Newarr:
                { var v = s.ReadU16(); pc += 2; return new OperandString(v); }

            case Opcode.Ldarg: case Opcode.Starg:
            case Opcode.Ldloc: case Opcode.Stloc:
                { var v = s.ReadU16(); pc += 2; return new OperandIndex(v); }

            case Opcode.Ldfld: case Opcode.Stfld:
            case Opcode.Ldsfld: case Opcode.Stsfld:
                { var v = s.ReadU16(); pc += 2; return new OperandFieldRef(v); }

            case Opcode.Call:
            case Opcode.Callvirt:
            case Opcode.NativeCall:
                {
                    var sIdx = s.ReadU16(); pc += 2;
                    var pCnt = s.ReadU16(); pc += 2;
                    return new OperandNativeCall(sIdx, pCnt);
                }

            case Opcode.Conv: case Opcode.Castclass: case Opcode.Isinst:
                { var v = s.ReadU16(); pc += 2; return new OperandTypeRef(v); }

            case Opcode.Br: case Opcode.Brtrue: case Opcode.Brfalse:
                { var v = s.ReadI32(); pc += 4; return new OperandBranch(v); }

            case Opcode.If: case Opcode.While:
                return ReadRawCondition(s, ref pc);

            case Opcode.Try:
                return ReadRawExceptionHandler(s, ref pc);

            default:
                return new OperandNone();
        }
    }

    private static ConditionOperand ReadRawCondition(ObjectRT.Reader.BinaryStream s, ref uint pc)
    {
        byte kind = s.ReadU8(); pc++;
        switch ((ConditionKind)kind)
        {
            case ConditionKind.Stack:
                return new ConditionOperand(ConditionKind.Stack);
            case ConditionKind.Binary:
                { byte cmp = s.ReadU8(); pc++; return new ConditionOperand(ConditionKind.Binary, cmp); }
            case ConditionKind.Expression:
            case ConditionKind.Block:
                {
                    uint len = s.ReadU32(); pc += 4;
                    var data = s.ReadBytes((int)len); pc += len;
                    return new ConditionOperand((ConditionKind)kind, 0, data);
                }
            default:
                return new ConditionOperand(ConditionKind.Stack);
        }
    }

    private static ExceptionHandlerOperand ReadRawExceptionHandler(ObjectRT.Reader.BinaryStream s, ref uint pc)
    {
        uint tryLen = s.ReadU32(); pc += 4;
        var tryBlock = s.ReadBytes((int)tryLen); pc += tryLen;

        ushort catchCount = s.ReadU16(); pc += 2;
        var catches = new CatchRecord[catchCount];
        for (int i = 0; i < catchCount; i++)
        {
            ushort typeIdx = s.ReadU16(); pc += 2;
            uint bodyLen = s.ReadU32(); pc += 4;
            var body = s.ReadBytes((int)bodyLen); pc += bodyLen;
            catches[i] = new CatchRecord(typeIdx, body);
        }

        bool hasFinally = s.ReadU8() != 0; pc++;
        byte[]? finallyBlock = null;
        if (hasFinally)
        {
            uint finallyLen = s.ReadU32(); pc += 4;
            finallyBlock = s.ReadBytes((int)finallyLen); pc += finallyLen;
        }

        return new ExceptionHandlerOperand(tryBlock, catches, hasFinally, finallyBlock);
    }
}

// ── Convenience wrapper ─────────────────────────────────────────────────

public static class VmCompiler
{
    public static Result<CompiledModule> Compile(ORBTModule src)
    {
        var compiler = new ModuleCompiler();
        return compiler.Compile(src);
    }
}
