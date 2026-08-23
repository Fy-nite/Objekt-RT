using System.Threading;
using System.Threading.Tasks;
using ObjektRT.Core.Model;
using ObjectRT.VM;

namespace ObjectRT.Dap;

/// <summary>A program the adapter can execute: a compiled module plus a ready interpreter.</summary>
public sealed class DapProgram
{
    /// <summary>The interpreter to run. The adapter assigns <c>DebugState</c> before running.</summary>
    public required Interpreter Interpreter { get; init; }

    /// <summary>The compiled module backing <see cref="Interpreter"/> (source maps, frames).</summary>
    public required CompiledModule Module { get; init; }
}

/// <summary>
/// Loads (typically: compiles) the launch target for a <see cref="DapServer"/>.
/// Implemented by hosts so the adapter itself stays language-agnostic: a
/// frontend compiles its own sources and wires any host handlers it needs,
/// then hands back an interpreter.
/// </summary>
public interface IDapProgramLoader
{
    /// <summary>
    /// Prepares the program at <paramref name="program"/> for execution.
    /// Throws <see cref="DapLoadException"/> when loading fails in an expected
    /// way (compile errors, missing files) — the adapter reports the message on
    /// stderr and terminates the session. Any other exception is treated as an
    /// adapter crash.
    /// </summary>
    Task<DapProgram> LoadAsync(string program, CancellationToken ct);
}

/// <summary>An expected failure while loading a program (compile errors, unreadable input).</summary>
public sealed class DapLoadException : Exception
{
    public DapLoadException(string message) : base(message) { }
}
