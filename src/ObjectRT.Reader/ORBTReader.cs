using ObjectRT.Abstractions;

namespace ObjectRT.Reader;

/// <summary>Reads ORBT binary module format into an ORBTModule.</summary>
public class ORBTReader
{
    private readonly BinaryStream _stream;
    private byte _formatVersion;

    public ORBTReader(BinaryStream stream)
    {
        _stream = stream;
    }

    public ORBTModule ReadModule()
    {
        var mod = new ORBTModule();
        ReadHeader(mod);
        ReadStringPool(mod);
        ReadTypeTable(mod);
        ReadImportTable(mod);
        ReadExportTable(mod);
        ReadMetadataBlock(mod);
        ReadMethodBodies(mod);
        return mod;
    }

    private void ReadHeader(ORBTModule mod)
    {
        // Magic: 4 bytes "ORBT"
        uint magic = _stream.ReadU32();
        if (magic != 0x4F524254)
            throw new InvalidDataException($"Invalid ORBT magic: expected 0x4F524254, got 0x{magic:X8}");

        mod.FormatVersion = _stream.ReadU8();
        if (mod.FormatVersion != 0x01 && mod.FormatVersion != 0x02)
            throw new InvalidDataException($"Unsupported ORBT version: {mod.FormatVersion}");
        _formatVersion = mod.FormatVersion;

        mod.ModuleName = _stream.ReadString();

        ushort maj = _stream.ReadU16();
        ushort min = _stream.ReadU16();
        ushort pat = _stream.ReadU16();
        mod.Version = new ModuleVersion(maj, min, pat);
    }

    private void ReadStringPool(ORBTModule mod)
    {
        ushort count = _stream.ReadU16();
        for (ushort i = 0; i < count; i++)
            mod.StringPool.Strings.Add(_stream.ReadString());
    }

    private void ReadTypeTable(ORBTModule mod)
    {
        ushort count = _stream.ReadU16();
        for (ushort i = 0; i < count; i++)
        {
            var type = new TypeRecord
            {
                Kind = (TypeKind)_stream.ReadU8(),
                NameIndex = _stream.ReadU16(),
                NamespaceIndex = _stream.ReadU16(),
                Access = (MemberAccess)_stream.ReadU8(),
                Flags = (TypeFlags)_stream.ReadU8(),
                BaseTypeIndex = _stream.ReadI32(),
            };

            // Interfaces
            type.InterfaceCount = _stream.ReadU16();
            for (ushort j = 0; j < type.InterfaceCount; j++)
                type.InterfaceIndices.Add(_stream.ReadU16());

            // Fields
            type.FieldCount = _stream.ReadU16();
            for (ushort j = 0; j < type.FieldCount; j++)
                type.Fields.Add(ReadFieldRecord());

            // Methods
            type.MethodCount = _stream.ReadU16();
            for (ushort j = 0; j < type.MethodCount; j++)
                type.Methods.Add(ReadMethodRecord(mod.StringPool));

            // Type-level attributes (v1 extension)
            type.Attributes = ReadAttributeList(mod.StringPool);

            mod.Types.Add(type);
        }
    }

    private FieldRecord ReadFieldRecord()
    {
        var name = _stream.ReadU16();
        var type = _stream.ReadU16();
        // v0x02 modules carry a per-field static flag byte AFTER name+type.
        bool isStatic = _formatVersion >= 0x02 && _stream.ReadU8() != 0;
        return new FieldRecord(name, type, isStatic);
    }

    private ParameterRecord ReadParamRecord()
    {
        return new ParameterRecord(_stream.ReadU16(), _stream.ReadU16());
    }

    private LocalRecord ReadLocalRecord()
    {
        return new LocalRecord(_stream.ReadU16(), _stream.ReadU16());
    }

    private LabelRecord ReadLabelRecord()
    {
        return new LabelRecord(_stream.ReadU16(), _stream.ReadU32());
    }

    private MethodRecord ReadMethodRecord(StringPool pool)
    {
        var method = new MethodRecord
        {
            NameIndex = _stream.ReadU16(),
            SignatureIndex = _stream.ReadU16(),
            Access = (MemberAccess)_stream.ReadU8(),
            Flags = (MethodFlags)_stream.ReadU8(),
        };

        // Parameters
        method.ParamCount = _stream.ReadU16();
        for (ushort j = 0; j < method.ParamCount; j++)
            method.Params.Add(ReadParamRecord());

        // Locals
        method.LocalCount = _stream.ReadU16();
        for (ushort j = 0; j < method.LocalCount; j++)
            method.Locals.Add(ReadLocalRecord());

        // Labels
        method.LabelCount = _stream.ReadU16();
        for (ushort j = 0; j < method.LabelCount; j++)
            method.Labels.Add(ReadLabelRecord());

        // Method-level attributes (v1 extension)
        method.Attributes = ReadAttributeList(pool);

        // Instructions
        method.InstrCount = _stream.ReadU32();
        int dataStart = _stream.Position;

        method.Instructions.Capacity = (int)method.InstrCount;
        for (uint j = 0; j < method.InstrCount; j++)
        {
            uint pc = (uint)(_stream.Position - dataStart);
            method.Instructions.Add(ReadInstruction(pool, pc));
        }

        int dataEnd = _stream.Position;
        _stream.Seek(dataStart);
        method.RawInstructionData = _stream.ReadBytes(dataEnd - dataStart);

        return method;
    }

    private Instruction ReadInstruction(StringPool pool, uint pc)
    {
        var opcode = ReadOpcode();
        var operand = ReadOperand(opcode, pool);
        return new Instruction(opcode, operand, pc);
    }

    private Opcode ReadOpcode()
    {
        int table = 0;
        while (true)
        {
            byte b = _stream.ReadU8();
            if (b == 0xFF)
            {
                table++;
                if (table > 255)
                    throw new InvalidDataException("Opcode table overflow (max 256 tables)");
                continue;
            }
            return (Opcode)(table * 256 + b);
        }
    }

    private Operand ReadOperand(Opcode opcode, StringPool pool)
    {
        return opcode switch
        {
            // No operand
            Opcode.Nop or Opcode.Add or Opcode.Sub or Opcode.Mul
                or Opcode.Div or Opcode.Rem or Opcode.Neg
                or Opcode.Ceq or Opcode.Cne or Opcode.Cgt or Opcode.Cge
                or Opcode.Clt or Opcode.Cle or Opcode.And or Opcode.Xor or Opcode.Or
                or Opcode.Not or Opcode.Dup or Opcode.Pop or Opcode.Ldnull
                or Opcode.Ret or Opcode.Break or Opcode.Continue or Opcode.Throw
                or Opcode.Ldelem or Opcode.Stelem
                => new OperandNone(),

            // Immediate values
            Opcode.LdcI4 or Opcode.Ldc => new OperandI4(_stream.ReadI32()),
            Opcode.LdcI8 => new OperandI8(_stream.ReadI64()),
            Opcode.LdcR4 => new OperandR4(_stream.ReadR4()),
            Opcode.LdcR8 => new OperandR8(_stream.ReadR8()),

            // String constant
            Opcode.Ldstr => new OperandString(_stream.ReadU16()),

            // Index-based (args, locals)
            Opcode.Ldarg or Opcode.Starg or Opcode.Ldloc or Opcode.Stloc
                => new OperandIndex(_stream.ReadU16()),

            // Field reference (string pool index)
            Opcode.Ldfld or Opcode.Stfld or Opcode.Ldsfld or Opcode.Stsfld
                => new OperandFieldRef(_stream.ReadU16()),

            // Method reference (name string index + param count, runtime-resolved)
            Opcode.Call or Opcode.Callvirt or Opcode.NativeCall
                => new OperandNativeCall(_stream.ReadU16(), _stream.ReadU16()),

            // Object creation
            Opcode.Newobj or Opcode.Newarr
                => new OperandString(_stream.ReadU16()),

            // Type reference
            Opcode.Conv or Opcode.Castclass or Opcode.Isinst
                => new OperandTypeRef(_stream.ReadU16()),

            // Branch
            Opcode.Br or Opcode.Brtrue or Opcode.Brfalse
                => new OperandBranch(_stream.ReadI32()),

            // Structured control flow
            Opcode.If or Opcode.While => ReadConditionOperand(),
            Opcode.Try => ReadExceptionHandler(),

            _ => new OperandNone(),
        };
    }

    private ConditionOperand ReadConditionOperand()
    {
        var kind = (ConditionKind)_stream.ReadU8();
        return kind switch
        {
            ConditionKind.Stack => new ConditionOperand(kind),
            ConditionKind.Binary => new ConditionOperand(kind, _stream.ReadU8()),
            ConditionKind.Expression or ConditionKind.Block
                => new ConditionOperand(kind, 0, _stream.ReadBytes((int)_stream.ReadU32())),
            _ => throw new InvalidDataException($"Unknown condition kind: {kind}"),
        };
    }

    private ExceptionHandlerOperand ReadExceptionHandler()
    {
        uint tryLen = _stream.ReadU32();
        var tryBlock = _stream.ReadBytes((int)tryLen);

        ushort catchCount = _stream.ReadU16();
        var catches = new CatchRecord[catchCount];
        for (int i = 0; i < catchCount; i++)
        {
            ushort typeIdx = _stream.ReadU16();
            uint bodyLen = _stream.ReadU32();
            catches[i] = new CatchRecord(typeIdx, _stream.ReadBytes((int)bodyLen));
        }

        bool hasFinally = _stream.ReadU8() != 0;
        byte[]? finallyBlock = null;
        if (hasFinally)
        {
            uint finallyLen = _stream.ReadU32();
            finallyBlock = _stream.ReadBytes((int)finallyLen);
        }

        return new ExceptionHandlerOperand(tryBlock, catches, hasFinally, finallyBlock);
    }

    private void ReadImportTable(ORBTModule mod)
    {
        ushort count = _stream.ReadU16();
        for (ushort i = 0; i < count; i++)
        {
            mod.Imports.Add(new ImportEntry(
                _stream.ReadU16(),
                _stream.ReadU16(),
                (ImportKind)_stream.ReadU8(),
                _stream.ReadU8()
            ));
        }
    }

    private void ReadExportTable(ORBTModule mod)
    {
        ushort count = _stream.ReadU16();
        for (ushort i = 0; i < count; i++)
        {
            mod.Exports.Add(new ExportEntry(
                _stream.ReadU16(),
                (ImportKind)_stream.ReadU8(),
                _stream.ReadU32(),
                _stream.ReadU16()
            ));
        }
    }

    private void ReadMetadataBlock(ORBTModule mod)
    {
        ushort blockLength = _stream.ReadU16();
        if (blockLength == 0)
        {
            mod.Metadata = new MetadataBlock();
            return;
        }

        int endPos = _stream.Position + blockLength;

        while (_stream.Position < endPos)
        {
            ushort keyIndex = _stream.ReadU16();
            byte valueKind = _stream.ReadU8();

            string key = keyIndex < mod.StringPool.Count
                ? mod.StringPool.Get(keyIndex)
                : "<unknown>";

            if (valueKind == 0x01)
            {
                string val = _stream.ReadString();
                mod.Metadata.Entries.Add(new MetadataEntry(key, val));

                if (key == "spec")
                    mod.Metadata.SpecVersion = val;
            }
            else if (valueKind == 0x02)
            {
                ushort entryCount = _stream.ReadU16();
                var list = new List<string>((int)entryCount);
                for (int i = 0; i < entryCount; i++)
                    list.Add(_stream.ReadString());

                mod.Metadata.Entries.Add(new MetadataEntry(key, list));

                if (key == "require")
                    mod.Metadata.Require = list;
                else if (key == "optional")
                    mod.Metadata.Optional = list;
            }
            else
            {
                throw new InvalidDataException($"Unknown metadata value kind: {valueKind}");
            }
        }
    }

    private List<AttributeRecord> ReadAttributeList(StringPool pool)
    {
        ushort count = _stream.ReadU16();
        var attrs = new List<AttributeRecord>(count);
        for (ushort i = 0; i < count; i++)
        {
            ushort nameIdx = _stream.ReadU16();
            ushort argCount = _stream.ReadU16();
            var args = new List<ushort>(argCount);
            for (ushort j = 0; j < argCount; j++)
                args.Add(_stream.ReadU16());
            attrs.Add(new AttributeRecord(nameIdx, args));
        }
        return attrs;
    }

    private void ReadMethodBodies(ORBTModule mod)
    {
        // Method bodies are read inline as part of type/method records.
        // Nothing more to do here.
    }
}

/// <summary>High-level convenience for reading ORBT files.</summary>
public static class OrbtFileReader
{
    public static ORBTModule ReadFile(string path)
    {
        var stream = new BinaryStream(path);
        var reader = new ORBTReader(stream);
        return reader.ReadModule();
    }

    /// <summary>Read an ORBT module from an in-memory byte array.</summary>
    public static ORBTModule ReadBytes(byte[] data)
    {
        var stream = new BinaryStream(data);
        var reader = new ORBTReader(stream);
        return reader.ReadModule();
    }
}
