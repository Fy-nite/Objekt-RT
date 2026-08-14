using System.Collections.Generic;
using System.Linq;
using System.Text;
using ObjectRT.Abstractions;

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
    StepBudgetExceeded,
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
        VmErrorKind.StepBudgetExceeded    => "StepBudgetExceeded",
        _                                 => "Unknown",
    };
}

// ── VmError ────────────────────────────────────────────────────────────

/// <summary>
/// ANSI color codes for the rich error report. Enabled by default when stderr
/// is a TTY; disabled when the NO_COLOR env var is set or the stream isn't a
/// TTY. Use <see cref="VmError.FormatDetailed(bool)"/> to override.
/// </summary>
public static class ErrorColors
{
    public const string Red = "\u001b[31;1m";      // bold red — error heading
    public const string Yellow = "\u001b[33;1m";   // yellow — location / stack
    public const string Cyan = "\u001b[36;1m";     // cyan — source line numbers
    public const string Dim = "\u001b[2m";         // dim — IR details
    public const string Green = "\u001b[32;1m";    // green — caret
    public const string Reset = "\u001b[0m";

    private static bool? _cached;

    /// <summary>
    /// Whether ANSI color is enabled: true unless NO_COLOR is set or stderr is
    /// redirected to a non-terminal (heuristic — Windows' TERM var).
    /// </summary>
    public static bool Enabled
    {
        get
        {
            if (_cached is bool v) return v;
            var noColor = Environment.GetEnvironmentVariable("NO_COLOR");
            var term = Environment.GetEnvironmentVariable("TERM");
            bool isTty = !string.IsNullOrEmpty(term) || !Console.IsErrorRedirected;
            _cached = string.IsNullOrEmpty(noColor) && isTty;
            return _cached.Value;
        }
    }

    /// <summary>Forces the color decision (used by tests / host apps).</summary>
    public static void SetEnabled(bool? enabled) => _cached = enabled;
}

public class VmError
{
    public VmErrorKind Kind { get; }
    public string Message { get; }
    public string Location { get; }
    public uint Pc { get; set; }

    /// <summary>The IR instruction mnemonic that failed (e.g. "div", "call").</summary>
    public string Opcode { get; set; } = "";
    /// <summary>Original-source mapping for the failing bytecode offset.</summary>
    public SourceMapEntry? Source { get; set; }
    /// <summary>Call stack, innermost first ("Program.Main" → ... → caller).</summary>
    public List<string> CallStack { get; set; } = new();

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

    public override string ToString() => FormatDetailed();

    /// <summary>Renders the detailed report, honoring <see cref="ErrorColors.Enabled"/>.</summary>
    public string FormatDetailed() => FormatDetailed(ErrorColors.Enabled);

    /// <summary>
    /// Renders a detailed, human-friendly error report: the error, the source
    /// location (from `#line` metadata) with a caret, the failing IR
    /// instruction, and the call stack.
    /// </summary>
    public string FormatDetailed(bool color)
    {
        var sb = new StringBuilder();
        string R = color ? ErrorColors.Reset : "";

        var kindText = Kind.ToDisplayString();
        var msgText = Message;
        if (color)
        {
            kindText = ErrorColors.Red + kindText + R;
            msgText = ErrorColors.Yellow + msgText + R;
        }
        sb.AppendLine($"runtime error: {kindText}: {msgText}");

        var indent = "  ";
        var hasIr = !string.IsNullOrEmpty(Opcode) || Pc != 0;

        if (!string.IsNullOrEmpty(Location) || hasIr)
        {
            var where = new StringBuilder();
            if (!string.IsNullOrEmpty(Location))
            {
                var loc = Location;
                if (color) loc = ErrorColors.Yellow + loc + R;
                where.Append("at ").Append(loc);
            }
            if (hasIr)
            {
                var pcText = Pc != 0 ? $"pc=0x{Pc:X}" : "";
                var opText = !string.IsNullOrEmpty(Opcode) ? Opcode : "";
                var ir = string.Join(" · ", new[] { pcText, opText }.Where(s => s.Length > 0));
                if (color) ir = ErrorColors.Dim + ir + R;
                where.Append(where.Length > 0 ? "  [" : "[").Append(ir).Append(']');
            }
            var whereLine = indent + "└─ " + where;
            if (color) whereLine = ErrorColors.Yellow + whereLine + R;
            sb.AppendLine(whereLine);
        }

        if (Source != null)
        {
            var lineNum = Source.Line;
            var srcText = Source.Text ?? "";
            var r = color ? ErrorColors.Reset : "";
            var lineTag = color ? ErrorColors.Cyan + lineNum.ToString() + r : lineNum.ToString();
            sb.AppendLine(indent + "└─ " + (color ? ErrorColors.Yellow : "") + "source line "
                + lineTag + (Source.Column > 0 ? (color ? ErrorColors.Yellow : "") + ":" + Source.Column : "")
                + r);
            sb.AppendLine(indent + "   " + lineTag + (color ? ErrorColors.Cyan : "") + " | " + r + srcText);
            if (Source.Column > 0)
            {
                // Clamp the caret to the source text so it never overshoots.
                int caretCol = Math.Clamp(Source.Column, 1, srcText.Length + 1);
                var caret = new string(' ', caretCol - 1) + "^";
                if (color) caret = ErrorColors.Green + caret + r;
                sb.AppendLine(indent + "     " + caret);
            }
        }

        if (CallStack.Count > 0)
        {
            var r = color ? ErrorColors.Reset : "";
            var stack = string.Join(" → ", CallStack);
            if (color) stack = ErrorColors.Yellow + stack + r;
            sb.Append(indent + "└─ " + (color ? ErrorColors.Yellow : "") + "stack: " + r + stack);
        }

        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// A VM fault that escapes the interpreter as an exception (e.g. stack
/// underflow from <c>Pop()</c>). Carries a rich <see cref="VmError"/> so the
/// formatted report survives the exception boundary.
/// </summary>
public sealed class VmRuntimeException : Exception
{
    public VmError Error { get; }

    public VmRuntimeException(VmError error) : base(error.FormatDetailed())
    {
        Error = error;
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
