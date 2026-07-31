namespace ObjectRT.Abstractions;

/// <summary>Base type for all instruction operands.</summary>
public abstract record Operand;

public record OperandNone : Operand;
public record OperandI4(int Value) : Operand;
public record OperandI8(long Value) : Operand;
public record OperandR4(float Value) : Operand;
public record OperandR8(double Value) : Operand;
public record OperandString(ushort StringIndex) : Operand;
public record OperandIndex(ushort Index) : Operand;
public record OperandFieldRef(ushort StringIndex) : Operand;
public record OperandMethodRef(ushort StringIndex) : Operand;
public record OperandTypeRef(ushort StringIndex) : Operand;

/// <summary>
/// Operand for the callnative opcode: a method name in the string pool plus
/// the number of arguments it expects (matched against the CLR method by
/// name + parameter count at runtime).
/// </summary>
public record OperandNativeCall(ushort StringIndex, ushort ParamCount) : Operand;
public record OperandBranch(int PcOffset) : Operand;

public enum ConditionKind : byte
{
    Stack      = 0x00,
    Binary     = 0x01,
    Expression = 0x02,
    Block      = 0x03,
}

public record ConditionOperand(
    ConditionKind Kind,
    byte Comparison = 0,
    byte[]? EmbeddedData = null
) : Operand;

public record CatchRecord(
    ushort TypeIndex,
    byte[] Body
);

public record ExceptionHandlerOperand(
    byte[] TryBlock,
    CatchRecord[] CatchRecords,
    bool HasFinally,
    byte[]? FinallyBlock = null
) : Operand;
