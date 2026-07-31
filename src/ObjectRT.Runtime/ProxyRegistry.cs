namespace ObjectRT.Runtime;

/// <summary>
/// Registry populated at startup by <c>[ModuleInitializer]</c> methods emitted
/// by the ObjectRT.SourceGenerator. <see cref="Runtime.Bind{T}"/> looks up
/// proxies here — no reflection scanning at runtime.
/// </summary>
public static class ProxyRegistry
{
    private static readonly Dictionary<Type, Func<IRRuntimeBinding, object>> _factories = new();

    /// <summary>
    /// Register a proxy factory for an interface type.
    /// Called automatically by generated <c>[ModuleInitializer]</c> code.
    /// </summary>
    public static void Register<T>(Func<IRRuntimeBinding, T> factory) where T : class
        => _factories[typeof(T)] = rt => factory(rt);

    /// <summary>
    /// Try to create a proxy instance for the given interface type.
    /// Returns false if no factory was registered (e.g. source generator didn't run).
    /// </summary>
    public static bool TryCreate<T>(IRRuntimeBinding rt, out T? proxy) where T : class
    {
        if (_factories.TryGetValue(typeof(T), out var factory))
        {
            proxy = (T)factory(rt);
            return true;
        }
        proxy = null;
        return false;
    }
}
