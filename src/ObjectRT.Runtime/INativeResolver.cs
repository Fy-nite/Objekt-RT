namespace ObjectRT.Runtime;

/// <summary>
/// Pluggable resolver for native method dispatch.
///
/// Each resolver is tried in registration order during
/// <see cref="Runtime.CallMethod{T}"/> before falling back to the VM.
///
/// Built-in implementations:
///   <see cref="ClrNativeResolver"/> — reflection-based CLR interop
///   Future: LlvmNativeResolver, HostNativeResolver, ...
/// </summary>
public interface INativeResolver
{
    /// <summary>
    /// Try to resolve a qualified method name to a callable delegate.
    ///
    /// Return null if this resolver can't handle the method — the runtime
    /// will try the next resolver or fall back to the VM interpreter/JIT.
    /// </summary>
    /// <param name="qualifiedName">Full method path, e.g. "Calc.Add" or "System.Console.WriteLine".</param>
    /// <param name="args">Arguments that will be passed. May be empty.</param>
    /// <returns>A delegate to invoke, or null if unresolvable.</returns>
    Delegate? TryResolve(string qualifiedName, object?[] args);
}
