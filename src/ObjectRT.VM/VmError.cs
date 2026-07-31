using System.Runtime.CompilerServices;

namespace ObjectRT.VM;

public enum VmErrorKind
{
    StackUnderflow,
    StackOverflow,
    TypeMismatch,
    NotAnObject,
    InvalidValueTag,
    InvalidFieldIndex,
    InvalidFunctionIndex,
    InvalidTypeIndex,
    InvalidStringIndex,
    InvalidObjectHandle,
    OutOfBounds,
    InvalidOpcode,
    MalformedBytecode,
    CodeOutOfBounds,
    DivisionByZero,
    UnresolvedField,
    UnresolvedMethod,
    UnresolvedType,
    UnresolvedEntryPoint,
    DuplicateFunction,
    FunctionNotFound,
    RuntimeError,
    InternalError,
}

public static class VmErrorKindExtensions
{
    public static string ToDisplayString(this VmErrorKind kind) => kind switch
    {
        VmErrorKind.StackUnderflow        => "StackUnderflow",
        VmErrorKind.StackOverflow         => "StackOverflow",
        VmErrorKind.TypeMismatch          => "TypeMismatch",
        VmErrorKind.NotAnObject           => "NotAnObject",
        VmErrorKind.InvalidValueTag       => "InvalidValueTag",
        VmErrorKind.InvalidFieldIndex     => "InvalidFieldIndex",
        VmErrorKind.InvalidFunctionIndex  => "InvalidFunctionIndex",
        VmErrorKind.InvalidTypeIndex      => "InvalidTypeIndex",
        VmErrorKind.InvalidStringIndex    => "InvalidStringIndex",
        VmErrorKind.InvalidObjectHandle   => "InvalidObjectHandle",
        VmErrorKind.OutOfBounds           => "OutOfBounds",
        VmErrorKind.InvalidOpcode         => "InvalidOpcode",
        VmErrorKind.MalformedBytecode     => "MalformedBytecode",
        VmErrorKind.CodeOutOfBounds       => "CodeOutOfBounds",
        VmErrorKind.DivisionByZero        => "DivisionByZero",
        VmErrorKind.UnresolvedField       => "UnresolvedField",
        VmErrorKind.UnresolvedMethod      => "UnresolvedMethod",
        VmErrorKind.UnresolvedType        => "UnresolvedType",
        VmErrorKind.UnresolvedEntryPoint  => "UnresolvedEntryPoint",
        VmErrorKind.DuplicateFunction     => "DuplicateFunction",
        VmErrorKind.FunctionNotFound      => "FunctionNotFound",
        VmErrorKind.RuntimeError          => "RuntimeError",
        VmErrorKind.InternalError         => "InternalError",
        _                                 => "Unknown",
    };
}

// ── VmError ────────────────────────────────────────────────────────────

public class VmError
{
    public VmErrorKind Kind { get; }
    public string Message { get; }
    public string Location { get; }
    public uint Pc { get; }

    public VmError(VmErrorKind kind, string message)
    {
        Kind = kind;
        Message = message;
        Location = "";
        Pc = 0;
    }

    public VmError(VmErrorKind kind, string message, string location, uint pc = 0)
    {
        Kind = kind;
        Message = message;
        Location = location;
        Pc = pc;
    }

    public override string ToString()
    {
        var s = $"{Kind.ToDisplayString()}: {Message}";
        if (!string.IsNullOrEmpty(Location))
            s += $" (at {Location})";
        if (Pc != 0)
            s += $" [pc={Pc}]";
        return s;
    }
}

// ── Result<T> — Rust-style Result type ─────────────────────────────────

public readonly struct Result<T>
{
    private readonly T? _value;
    private readonly VmError? _error;
    private readonly bool _isOk;

    private Result(T value)
    {
        _value = value;
        _error = null;
        _isOk = true;
    }

    private Result(VmError error)
    {
        _value = default;
        _error = error;
        _isOk = false;
    }

    public bool IsOk => _isOk;
    public bool IsError => !_isOk;

    public T Value => _isOk
        ? _value!
        : throw new InvalidOperationException($"Result is error: {_error!.Message}");

    public VmError Error => !_isOk
        ? _error!
        : throw new InvalidOperationException("Result is ok, not error");

    public static implicit operator Result<T>(T value) => new(value);
    public static implicit operator Result<T>(VmError error) => new(error);
}
