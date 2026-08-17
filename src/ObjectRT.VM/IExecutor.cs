using ObjektRT.Core.Model;

namespace ObjectRT.VM;

/// <summary>
/// Pluggable execution engine for a <see cref="CompiledModule"/>.
///
/// A single implementation runs all functions for one module; the host
/// <see cref="Interpreter"/> wires native-call dispatch and string interning
/// before any script execution. Implementations:
///   - <see cref="Interpreter"/> — iterative bytecode dispatch loop
///   - <see cref="ReflectionJit"/> — compile-on-load via System.Reflection.Emit
///   - LLVMSharpJit (future) — NativeAOT-safe compiled methods
/// </summary>
public interface IExecutor
{
    /// <summary>Handler for native (host) method dispatch.</summary>
    Func<string, object?[], object?>? NativeCallHandler { get; set; }

    /// <summary>Get or intern a string handle.</summary>
    uint InternString(string s);

    /// <summary>Resolve a string handle to its CLR string.</summary>
    string? GetStringValue(uint idx);

    /// <summary>Marshal a CLR value into a VM value.</summary>
    Value MarshalValue(object? val);

    /// <summary>Unbox a VM value to a CLR object.</summary>
    object? ValueToObject(Value v);

    /// <summary>Run a specific function by index with the given argument values.</summary>
    Result<Value> RunFunction(uint funcIdx, Value[] args);

    /// <summary>Run the module entry point.</summary>
    Result<Value> Run();

    /// <summary>
    /// Reset execution state between top-level calls. Stack and frames are
    /// cleared. The heap and static fields are preserved by default.
    /// </summary>
    void Reset(bool clearHeap = false, bool clearStatics = false);

    /// <summary>
    /// Whether a specific function has been compiled by the JIT engine.
    /// The interpreter returns true for all functions (always available);
    /// <see cref="ReflectionJit"/> returns true when Roslyn has finished.
    /// </summary>
    bool IsCompiled(uint funcIdx) => true;
}
