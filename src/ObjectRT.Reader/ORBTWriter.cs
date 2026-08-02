using ObjectRT.Abstractions;

namespace ObjectRT.Reader;

/// <summary>Writes an ORBTModule to the ORBT binary format.</summary>
public class ORBTWriter
{
    private readonly List<byte> _data = new();

    public byte[] WriteModule(ORBTModule mod)
    {
        _data.Clear();
        WriteHeader(mod);
        WriteStringPool(mod);
        WriteTypeTable(mod);
        WriteImportTable(mod);
        WriteExportTable(mod);
        WriteMetadataBlock(mod);
        return _data.ToArray();
    }

    private void WriteU8(byte v) => _data.Add(v);
    private void WriteU16(ushort v) { _data.Add((byte)v); _data.Add((byte)(v >> 8)); }
    private void WriteI32(int v) => WriteU32((uint)v);
    private void WriteU32(uint v)
    {
        _data.Add((byte)v); _data.Add((byte)(v >> 8));
        _data.Add((byte)(v >> 16)); _data.Add((byte)(v >> 24));
    }
    private void WriteString(string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        WriteU16((ushort)bytes.Length);
        _data.AddRange(bytes);
    }

    private void WriteHeader(ORBTModule mod)
    {
        WriteU32(0x4F524254); // "ORBT"
        WriteU8(mod.FormatVersion);
        WriteString(mod.ModuleName);
        WriteU16(mod.Version.Major);
        WriteU16(mod.Version.Minor);
        WriteU16(mod.Version.Patch);
    }

    private void WriteStringPool(ORBTModule mod)
    {
        WriteU16((ushort)mod.StringPool.Count);
        for (int i = 0; i < mod.StringPool.Count; i++)
            WriteString(mod.StringPool.Strings[i]);
    }

    private void WriteTypeTable(ORBTModule mod)
    {
        WriteU16((ushort)mod.Types.Count);
        foreach (var type in mod.Types)
        {
            WriteU8((byte)type.Kind);
            WriteU16(type.NameIndex);
            WriteU16(type.NamespaceIndex);
            WriteU8((byte)type.Access);
            WriteU8((byte)type.Flags);
            WriteI32(type.BaseTypeIndex);

            WriteU16(type.InterfaceCount);
            foreach (var ifIdx in type.InterfaceIndices)
                WriteU16(ifIdx);

            WriteU16(type.FieldCount);
            foreach (var f in type.Fields)
            {
                WriteU16(f.NameIndex);
                WriteU16(f.TypeIndex);
            }

            WriteU16(type.MethodCount);
            foreach (var m in type.Methods)
                WriteMethodHeader(m);

            // Attributes (type-level)
            WriteU16((ushort)type.Attributes.Count);
            foreach (var attr in type.Attributes)
            {
                WriteU16(attr.NameIndex);
                WriteU16((ushort)attr.ArgIndices.Count);
                foreach (var ai in attr.ArgIndices)
                    WriteU16(ai);
            }
        }
    }

    private void WriteMethodHeader(MethodRecord m)
    {
        WriteU16(m.NameIndex);
        WriteU16(m.SignatureIndex);
        WriteU8((byte)m.Access);
        WriteU8((byte)m.Flags);

        WriteU16(m.ParamCount);
        foreach (var p in m.Params)
        {
            WriteU16(p.NameIndex);
            WriteU16(p.TypeIndex);
        }

        WriteU16(m.LocalCount);
        foreach (var l in m.Locals)
        {
            WriteU16(l.NameIndex);
            WriteU16(l.TypeIndex);
        }

        WriteU16(m.LabelCount);
        foreach (var lb in m.Labels)
        {
            WriteU16(lb.NameIndex);
            WriteU32(lb.PcOffset);
        }

        // Method-level attributes
        WriteU16((ushort)m.Attributes.Count);
        foreach (var attr in m.Attributes)
        {
            WriteU16(attr.NameIndex);
            WriteU16((ushort)attr.ArgIndices.Count);
            foreach (var ai in attr.ArgIndices)
                WriteU16(ai);
        }

        // Raw instruction data (inline, matching reader format where InstrCount
        // determines how many instructions to decode from the stream).
        WriteU32(m.InstrCount);
        _data.AddRange(m.RawInstructionData);
    }

    private void WriteImportTable(ORBTModule mod)
    {
        WriteU16((ushort)mod.Imports.Count);
        foreach (var imp in mod.Imports)
        {
            WriteU16(imp.ModuleIndex);
            WriteU16(imp.SymbolIndex);
            WriteU16((ushort)imp.Kind);
            WriteU8(imp.Flags);
        }
    }

    private void WriteExportTable(ORBTModule mod)
    {
        WriteU16((ushort)mod.Exports.Count);
        foreach (var exp in mod.Exports)
        {
            WriteU16(exp.NameIndex);
            WriteU16((ushort)exp.Kind);
            WriteU32(exp.LocalIndex);
            WriteU16(exp.ModuleIndex);
        }
    }

    private void WriteMetadataBlock(ORBTModule mod)
    {
        // Write 0-length block (metadata is informational, not needed for
        // roundtrip compilation). The reader reads a U16 length first.
        WriteU16(0);
    }
}