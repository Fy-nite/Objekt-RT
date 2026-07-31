using ObjectRT.VM;

namespace ObjectRT.Runtime;

/// <summary>
/// A late-bound handle to an ObjectRT class, used by generated proxy classes
/// to dispatch calls into the VM. You can also use this directly without
/// a proxy if you prefer dynamic invocation.
/// </summary>
public sealed class IRRuntimeBinding
{
    private readonly Runtime _runtime;
    private readonly string _className;

    /// <param name="runtime">The Runtime instance to dispatch through.</param>
    /// <param name="className">The ObjectRT class name this binding targets.</param>
    public IRRuntimeBinding(Runtime runtime, string className)
    {
        _runtime = runtime;
        _className = className;
    }

    /// <summary>
    /// Invoke a method on this class binding and return a strongly-typed result.
    /// </summary>
    public TResult Invoke<TResult>(string methodName, params object?[] args)
    {
        string qualifiedName = methodName.Contains('.')
            ? methodName
            : $"{_className}.{methodName}";

        return _runtime.CallMethod<TResult>(qualifiedName, args);
    }
}
