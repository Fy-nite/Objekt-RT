using System.Collections.Concurrent;
using System.Reflection;

namespace ObjectRT.Runtime;

/// <summary>
/// An <see cref="INativeResolver"/> that dispatches script calls to host
/// objects through interface contracts.
///
/// Register an implementation of a host interface with
/// <see cref="RegisterHost{T}"/> or <see cref="RegisterHost(string, object, Type)"/>.
/// Script calls of the form <c>callnative BindingName.MethodName(...)</c> are
/// resolved against the registered host.
///
/// Dispatch strategy (in order):
///   1. Source-generated hardwired adapter from <see cref="HostDispatchRegistry"/>
///      — strongly typed casts, no reflection, NativeAOT-safe.
///   2. Reflection fallback against the registered interface — convenient
///      when the source generator isn't wired up. Disable with
///      <see cref="AllowReflection"/> = false for NativeAOT.
/// </summary>
public sealed class InterfaceHostResolver : INativeResolver
{
    private readonly Dictionary<string, (object Host, Type? InterfaceType)> _hosts = new(StringComparer.Ordinal);

    // Cache: qualifiedName → host-bound delegate or not-found sentinel.
    private readonly ConcurrentDictionary<string, object> _cache = new(StringComparer.Ordinal);

    private static readonly object s_notFoundSentinel = new();

    /// <summary>
    /// When false, <see cref="TryResolve"/> skips the reflection fallback and
    /// only uses source-generated dispatch. Set this for NativeAOT builds
    /// where reflection may be trimmed.
    /// </summary>
    public bool AllowReflection { get; set; } = true;

    /// <summary>Register a host implementation under its binding name.</summary>
    /// <param name="name">Name scripts use, e.g. "MonoGame.Screen".</param>
    /// <param name="host">The host implementation instance.</param>
    public void RegisterHost<T>(string name, T host) where T : class
    {
        var t = typeof(T);
        if (!t.IsInterface)
            throw new ArgumentException($"RegisterHost requires an interface type; '{t.Name}' is not an interface.");
        RegisterHost(name, host, t);
    }

    /// <summary>
    /// Register a host implementation under its binding name with an explicit
    /// interface type (useful when the host is registered by its concrete type).
    /// </summary>
    public void RegisterHost(string name, object host, Type interfaceType)
    {
        if (!interfaceType.IsInterface)
            throw new ArgumentException($"RegisterHost requires an interface type; '{interfaceType.Name}' is not an interface.");
        if (!interfaceType.IsInstanceOfType(host))
            throw new ArgumentException($"Host '{host.GetType().Name}' does not implement '{interfaceType.Name}'.");

        _hosts[name] = (host, interfaceType);
        _cache.Clear(); // host may have changed; drop cached delegates
    }

    /// <summary>Remove a host registration (e.g. when hot-reloading).</summary>
    public void UnregisterHost(string name)
    {
        _hosts.Remove(name);
        _cache.Clear();
    }

    /// <inheritdoc />
    public Delegate? TryResolve(string qualifiedName, object?[] args)
    {
        if (_cache.TryGetValue(qualifiedName, out var cached))
            return cached as Delegate;

        var dot = qualifiedName.LastIndexOf('.');
        if (dot < 0)
        {
            _cache.TryAdd(qualifiedName, s_notFoundSentinel);
            return null;
        }

        var hostName = qualifiedName[..dot];
        var methodName = qualifiedName[(dot + 1)..];

        if (!_hosts.TryGetValue(hostName, out var entry))
        {
            _cache.TryAdd(qualifiedName, s_notFoundSentinel);
            return null;
        }

        // 1. Source-generated hardwired dispatch (NativeAOT-safe).
        if (HostDispatchRegistry.TryGet(hostName, methodName, out var generated))
        {
            object host = entry.Host;
            var fast = new Func<object?[], object?>(a => generated(host, a));
            _cache.TryAdd(qualifiedName, fast);
            return fast;
        }

        // 2. Reflection fallback on the interface.
        if (!AllowReflection || entry.InterfaceType is null)
        {
            _cache.TryAdd(qualifiedName, s_notFoundSentinel);
            return null;
        }

        var iface = entry.InterfaceType;
        var methods = iface.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == methodName)
            .ToArray();

        MethodInfo? best = methods.Length switch
        {
            1 => methods[0],
            > 1 => methods.FirstOrDefault(m => m.GetParameters().Length == args.Length) ?? methods[0],
            _ => null,
        };

        if (best == null)
        {
            _cache.TryAdd(qualifiedName, s_notFoundSentinel);
            return null;
        }

        var host2 = entry.Host;
        var reflected = new Func<object?[], object?>(a =>
        {
            try
            {
                return best.Invoke(host2, a);
            }
            catch (TargetInvocationException tie)
            {
                throw tie.InnerException ?? tie;
            }
        });
        _cache.TryAdd(qualifiedName, reflected);
        return reflected;
    }

    /// <summary>Clear the host registry and dispatch cache.</summary>
    public void Reset()
    {
        _hosts.Clear();
        _cache.Clear();
    }
}
