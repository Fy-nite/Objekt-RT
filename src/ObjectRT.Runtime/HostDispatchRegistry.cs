namespace ObjectRT.Runtime;

/// <summary>
/// Registry of hardwired host dispatch adapters, populated at startup by the
/// source generator for interfaces marked with <see cref="IRHostBindingAttribute"/>.
///
/// Each entry maps (host binding name, method name) to a strongly-typed
/// invoker that casts the registered host instance to its interface and calls
/// the method directly — zero reflection. This is what keeps host bindings
/// functional when the runtime is compiled with NativeAOT / dynamic code
/// disabled (see <c>InterfaceHostResolver.AllowReflection = false</c>).
/// </summary>
public static class HostDispatchRegistry
{
    private static readonly Dictionary<string, Dictionary<string, Func<object, object?[], object?>>>
        s_byHost = new(StringComparer.Ordinal);

    /// <summary>Register a generated invoker for a host method.</summary>
    public static void Register(string hostName, string methodName, Func<object, object?[], object?> invoker)
    {
        lock (s_byHost)
        {
            if (!s_byHost.TryGetValue(hostName, out var methods))
            {
                methods = new Dictionary<string, Func<object, object?[], object?>>(StringComparer.Ordinal);
                s_byHost[hostName] = methods;
            }
            methods[methodName] = invoker;
        }
    }

    /// <summary>
    /// Look up a generated invoker. Returns false when no generated adapter
    /// exists for this host/method (e.g. the generator wasn't wired up).
    /// </summary>
    public static bool TryGet(string hostName, string methodName, out Func<object, object?[], object?>? invoker)
    {
        invoker = null;
        lock (s_byHost)
        {
            return s_byHost.TryGetValue(hostName, out var methods)
                   && methods.TryGetValue(methodName, out invoker);
        }
    }

    /// <summary>Whether any generated adapter is registered under a host name.</summary>
    public static bool HasHost(string hostName)
    {
        lock (s_byHost)
        {
            return s_byHost.ContainsKey(hostName);
        }
    }
}
