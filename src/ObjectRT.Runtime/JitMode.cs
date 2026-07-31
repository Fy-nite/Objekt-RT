namespace ObjectRT.Runtime;

/// <summary>
/// Execution backend for the ObjectRT VM.
/// </summary>
public enum JitMode
{
    /// <summary>Iterative bytecode dispatch loop (default).</summary>
    Interpreter = 0,

    /// <summary>Compile each function to a System.Reflection.Emit DynamicMethod on load.
    /// Fast startup, JIT-quality execution speed. Requires
    /// <see cref="System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported"/>.</summary>
    Reflection = 1,

    /// <summary>(Planned) Compile via LLVMSharp to native code. NativeAOT-safe.</summary>
    LLVM = 2,
}
