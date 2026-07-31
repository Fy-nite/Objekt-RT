using System.Runtime.InteropServices;

namespace ObjectRT.VM;

public enum ValueTag : byte
{
    Nil = 0,
    I4  = 1,
    I8  = 2,
    R4  = 3,
    R8  = 4,
    Obj = 5, // heap object handle (placeholder)
    Str = 6, // CLR string reference
}

/// <summary>8-byte tagged union representing a VM stack value.</summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
public struct Value
{
    [FieldOffset(0)] public ValueTag Tag;
    [FieldOffset(1)] private readonly byte _pad1;
    [FieldOffset(2)] private readonly byte _pad2;
    [FieldOffset(3)] private readonly byte _pad3;
    [FieldOffset(4)] private readonly byte _pad4;
    [FieldOffset(5)] private readonly byte _pad5;
    [FieldOffset(6)] private readonly byte _pad6;
    [FieldOffset(7)] private readonly byte _pad7;
    [FieldOffset(8)] public int I4;
    [FieldOffset(8)] public long I8;
    [FieldOffset(8)] public float R4;
    [FieldOffset(8)] public double R8;
    [FieldOffset(8)] public ulong Raw;

    public static Value Nil() => new() { Tag = ValueTag.Nil, Raw = 0 };

    public static Value FromI4(int v) => new() { Tag = ValueTag.I4, I4 = v };
    public static Value FromI8(long v) => new() { Tag = ValueTag.I8, I8 = v };
    public static Value FromR4(float v) => new() { Tag = ValueTag.R4, R4 = v };
    public static Value FromR8(double v) => new() { Tag = ValueTag.R8, R8 = v };
    public static Value FromObj(uint v) => new() { Tag = ValueTag.Obj, Raw = v };

    /// <summary>
    /// A string value is a handle into the interpreter's interned string
    /// table (the VM heap pattern — no CLR reference can live in this struct
    /// because it is LayoutKind.Explicit).
    /// </summary>
    public static Value FromStr(uint v) => new() { Tag = ValueTag.Str, Raw = v };

    public uint AsObj() => (uint)Raw;

    public uint AsStr() => (uint)Raw;

    /// <summary>Truthiness used by brtrue/brfalse and if/while (stack).</summary>
    public bool IsTruthy() => Tag switch
    {
        ValueTag.Nil => false,
        ValueTag.Str => true, // interned strings are never null
        ValueTag.I4  => I4 != 0,
        ValueTag.I8  => I8 != 0,
        ValueTag.R4  => R4 != 0f,
        ValueTag.R8  => R8 != 0d,
        ValueTag.Obj => true,
        _            => false,
    };

    /// <summary>
    /// Box a CLR primitive into a VM value. Strings are handled by the
    /// interpreter's MarshalValue (they need interning), so they become Nil
    /// here; everything else unsupported also becomes Nil.
    /// </summary>
    public static Value FromObject(object? o) => o switch
    {
        null => Nil(),
        int i => FromI4(i),
        bool b => FromI4(b ? 1 : 0),
        long l => FromI8(l),
        float f => FromR4(f),
        double d => FromR8(d),
        _ => Nil(),
    };

    /// <summary>
    /// Unbox a VM value to a CLR object. Strings (Str tag) cannot be resolved
    /// without the interpreter's string table, so they come back as the raw
    /// handle; use Interpreter.ValueToObject for full round-tripping.
    /// </summary>
    public static object? ToObject(Value v) => v.Tag switch
    {
        ValueTag.Nil => null,
        ValueTag.I4  => v.I4,
        ValueTag.I8  => v.I8,
        ValueTag.R4  => v.R4,
        ValueTag.R8  => v.R8,
        ValueTag.Str => v.AsStr(),
        ValueTag.Obj => v.AsObj(),
        _            => null,
    };

    public override string ToString() => Tag switch
    {
        ValueTag.Nil => "nil",
        ValueTag.I4  => I4.ToString(),
        ValueTag.I8  => I8.ToString(),
        ValueTag.R4  => R4.ToString(),
        ValueTag.R8  => R8.ToString(),
        ValueTag.Obj => $"obj({AsObj()})",
        ValueTag.Str => $"str({AsStr()})",
        _            => "<?>",
    };
}
