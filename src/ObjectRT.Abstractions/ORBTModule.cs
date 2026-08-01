using System.Text;

namespace ObjectRT.Abstractions;

// ── String pool ────────────────────────────────────────────────────────────

public class StringPool
{
    public List<string> Strings { get; } = new();

    public string Get(ushort index) =>
        Strings[(int)index];

    public ushort Add(string s)
    {
        var idx = (ushort)Strings.Count;
        Strings.Add(s);
        return idx;
    }

    public int Count => Strings.Count;
}

// ── Records within types ───────────────────────────────────────────────────

public record AttributeRecord(ushort NameIndex, List<ushort> ArgIndices);

public record FieldRecord(ushort NameIndex, ushort TypeIndex);

public record ParameterRecord(ushort NameIndex, ushort TypeIndex);

public record LocalRecord(ushort NameIndex, ushort TypeIndex);

public record LabelRecord(ushort NameIndex, uint PcOffset);

public record MethodRecord
{
    public ushort NameIndex { get; set; }
    public ushort SignatureIndex { get; set; }
    public MemberAccess Access { get; set; } = MemberAccess.Public;
    public MethodFlags Flags { get; set; } = MethodFlags.None;
    public ushort ParamCount { get; set; }
    public List<ParameterRecord> Params { get; set; } = new();
    public ushort LocalCount { get; set; }
    public List<LocalRecord> Locals { get; set; } = new();
    public ushort LabelCount { get; set; }
    public List<LabelRecord> Labels { get; set; } = new();
    public uint InstrCount { get; set; }
    public List<Instruction> Instructions { get; set; } = new();
    public byte[] RawInstructionData { get; set; } = Array.Empty<byte>();
    public List<AttributeRecord> Attributes { get; set; } = new();
}

public record TypeRecord
{
    public TypeKind Kind { get; set; } = TypeKind.Class;
    public ushort NameIndex { get; set; }
    public ushort NamespaceIndex { get; set; }
    public MemberAccess Access { get; set; } = MemberAccess.Public;
    public TypeFlags Flags { get; set; } = TypeFlags.None;
    public int BaseTypeIndex { get; set; } = -1;
    public ushort InterfaceCount { get; set; }
    public List<ushort> InterfaceIndices { get; set; } = new();
    public ushort FieldCount { get; set; }
    public List<FieldRecord> Fields { get; set; } = new();
    public ushort MethodCount { get; set; }
    public List<MethodRecord> Methods { get; set; } = new();
    public List<AttributeRecord> Attributes { get; set; } = new();
}

// ── Import/Export ─────────────────────────────────────────────────────────

public record ImportEntry(
    ushort ModuleIndex,
    ushort SymbolIndex,
    ImportKind Kind,
    byte Flags
);

public record ExportEntry(
    ushort NameIndex,
    ImportKind Kind,
    uint LocalIndex,
    ushort ModuleIndex
);

// ── Metadata ───────────────────────────────────────────────────────────────

public record MetadataEntry(string Key, object Value);

public record MetadataBlock
{
    public List<MetadataEntry> Entries { get; set; } = new();
    public string SpecVersion { get; set; } = "";
    public List<string> Require { get; set; } = new();
    public List<string> Optional { get; set; } = new();
}

// ── Module version ─────────────────────────────────────────────────────────

public record ModuleVersion(ushort Major, ushort Minor, ushort Patch);

// ── The module itself ──────────────────────────────────────────────────────

public class ORBTModule
{
    public string ModuleName { get; set; } = "";
    public byte FormatVersion { get; set; }
    public ModuleVersion Version { get; set; } = new(0, 0, 0);

    public StringPool StringPool { get; set; } = new();
    public List<TypeRecord> Types { get; set; } = new();
    public List<ImportEntry> Imports { get; set; } = new();
    public List<ExportEntry> Exports { get; set; } = new();
    public MetadataBlock Metadata { get; set; } = new();

    public string Resolve(ushort stringIndex) => StringPool.Get(stringIndex);
}

public static class OrbtModuleExtensions
{
    /// <summary>Dump the module to a string (like the C++ dump() function).</summary>
    public static string Dump(this ORBTModule mod, bool verbose = false)
    {
        var sb = new StringBuilder();

        void Line(string s = "") => sb.AppendLine(s);

        Line(";; ObjectRT ORBT Module");
        Line($";; File format: ORBT v{mod.FormatVersion}");
        Line($"module {mod.ModuleName} version {mod.Version.Major}.{mod.Version.Minor}.{mod.Version.Patch}");
        Line();

        // Metadata
        if (mod.Metadata.Entries.Count > 0 || !string.IsNullOrEmpty(mod.Metadata.SpecVersion)
            || mod.Metadata.Require.Count > 0 || mod.Metadata.Optional.Count > 0)
        {
            Line(".metadata {");
            if (!string.IsNullOrEmpty(mod.Metadata.SpecVersion))
                Line($"    spec objectrt = \"{mod.Metadata.SpecVersion}\"");
            if (mod.Metadata.Require.Count > 0)
            {
                Line("    require [");
                foreach (var f in mod.Metadata.Require)
                    Line($"        {f},");
                Line("    ]");
            }
            if (mod.Metadata.Optional.Count > 0)
            {
                Line("    optional [");
                foreach (var f in mod.Metadata.Optional)
                    Line($"        {f},");
                Line("    ]");
            }
            Line("}");
            Line();
        }

        // Imports
        if (mod.Imports.Count > 0)
        {
            Line($";; Imports ({mod.Imports.Count})");
            for (int i = 0; i < mod.Imports.Count; i++)
            {
                var imp = mod.Imports[i];
                var line = $";;   [{i}] {mod.Resolve(imp.ModuleIndex)}.{mod.Resolve(imp.SymbolIndex)}";
                if ((imp.Flags & 0x01) != 0) line += " (optional)";
                Line(line);
            }
            Line();
        }

        // Exports
        if (mod.Exports.Count > 0)
        {
            Line($";; Exports ({mod.Exports.Count})");
            for (int i = 0; i < mod.Exports.Count; i++)
            {
                var exp = mod.Exports[i];
                Line($";;   [{i}] {mod.Resolve(exp.ModuleIndex)}.{mod.Resolve(exp.NameIndex)} -> local[{exp.LocalIndex}]");
            }
            Line();
        }

        // Types
        foreach (var type in mod.Types)
        {
            if ((type.Flags & TypeFlags.Abstract) != 0) sb.Append("abstract ");
            if ((type.Flags & TypeFlags.Sealed) != 0) sb.Append("sealed ");

            sb.Append($"{type.Kind.ToDisplayString()} {mod.Resolve(type.NameIndex)}");
            if (type.BaseTypeIndex >= 0 && type.BaseTypeIndex < mod.Types.Count)
                sb.Append($" : {mod.Resolve(mod.Types[type.BaseTypeIndex].NameIndex)}");
            Line(" {");

            // Fields
            foreach (var field in type.Fields)
            {
                Line($"    {type.Access.ToDisplayString()} field {mod.Resolve(field.NameIndex)}: {mod.Resolve(field.TypeIndex)}");
            }

            // Methods
            foreach (var method in type.Methods)
            {
                Line();
                sb.Append($"    {method.Access.ToDisplayString()}");
                if ((method.Flags & MethodFlags.Static) != 0) sb.Append(" static");
                if ((method.Flags & MethodFlags.Virtual) != 0) sb.Append(" virtual");
                if ((method.Flags & MethodFlags.Override) != 0) sb.Append(" override");
                if ((method.Flags & MethodFlags.Abstract) != 0) sb.Append(" abstract");
                sb.Append($" method {mod.Resolve(method.NameIndex)}(");

                for (int p = 0; p < method.Params.Count; p++)
                {
                    if (p > 0) sb.Append(", ");
                    sb.Append($"{mod.Resolve(method.Params[p].NameIndex)}: {mod.Resolve(method.Params[p].TypeIndex)}");
                }
                sb.Append(")");

                if (method.SignatureIndex < mod.StringPool.Count)
                    sb.Append($" /* sig: {mod.Resolve(method.SignatureIndex)} */");

                Line(" {");

                // Locals
                foreach (var local in method.Locals)
                    Line($"        local {mod.Resolve(local.NameIndex)}: {mod.Resolve(local.TypeIndex)}");

                // Labels
                if (method.Labels.Count > 0)
                {
                    Line();
                    foreach (var label in method.Labels)
                        Line($"        ;; label {mod.Resolve(label.NameIndex)} @ pc={label.PcOffset}");
                }

                // Instructions
                if (verbose)
                {
                    Line();
                    foreach (var instr in method.Instructions)
                    {
                        var opStr = OperandToString(instr.Operand, mod.StringPool);
                        Line($"        {instr.Opcode.ToDisplayString()}{(opStr.Length > 0 ? " " + opStr : "")}");
                    }
                }
                else
                {
                    Line($"        ;; {method.InstrCount} instruction(s)");
                }

                Line("    }");
            }

            Line("}");
            Line();
        }

        // String pool dump in verbose mode
        if (verbose && mod.StringPool.Count > 0)
        {
            Line($";; String pool ({mod.StringPool.Count} entries)");
            for (int i = 0; i < mod.StringPool.Count; i++)
                Line($";;   [{i}] \"{mod.StringPool.Get((ushort)i)}\"");
        }

        return sb.ToString();
    }

    private static string OperandToString(Operand operand, StringPool pool)
    {
        return operand switch
        {
            OperandNone      => "",
            OperandI4 v      => v.Value.ToString(),
            OperandI8 v      => v.Value.ToString(),
            OperandR4 v      => v.Value.ToString(),
            OperandR8 v      => v.Value.ToString(),
            OperandString v  => v.StringIndex < pool.Count ? $"\"{pool.Get(v.StringIndex)}\"" : $"<string[{v.StringIndex}]>",
            OperandIndex v   => v.Index.ToString(),
            OperandFieldRef v => v.StringIndex < pool.Count ? pool.Get(v.StringIndex) : $"<field[{v.StringIndex}]>",
            OperandMethodRef v => v.StringIndex < pool.Count ? pool.Get(v.StringIndex) : $"<method[{v.StringIndex}]>",
            OperandTypeRef v => v.StringIndex < pool.Count ? pool.Get(v.StringIndex) : $"<type[{v.StringIndex}]>",
            OperandBranch v  => $"offset({v.PcOffset})",
            ConditionOperand v => v.Kind switch
            {
                ConditionKind.Stack      => "stack",
                ConditionKind.Binary     => $"binary(0x{v.Comparison:X})",
                ConditionKind.Expression => $"expr({(v.EmbeddedData?.Length ?? 0)} bytes)",
                ConditionKind.Block      => $"block({(v.EmbeddedData?.Length ?? 0)} bytes)",
                _                        => "<unknown condition>",
            },
            ExceptionHandlerOperand v => $"try({(v.TryBlock?.Length ?? 0)} bytes, {(v.CatchRecords?.Length ?? 0)} catches{(v.HasFinally ? ", finally" : "")})",
            _                        => "<operand>",
        };
    }
}
