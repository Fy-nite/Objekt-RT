using ObjektRT.Core.Model;

namespace ObjectRT.Runtime;

/// <summary>
/// An <see cref="INativeResolver"/> that reads <c>@NativeBinding</c> attributes
/// from module metadata and dispatches calls to registered host objects.
///
/// This is the IR-level equivalent of <see cref="InterfaceHostResolver"/>:
/// instead of requiring C# interfaces with <c>[IRHostBinding]</c>, the module
/// itself declares its host bindings via annotations:
///
/// <code>
/// @NativeBinding("MonoGame.Screen")
/// class ScreenBindings {
///     static method Clear(color: int32) -> void { ret }
///     static method Width() -> int32 { ret }
/// }
/// </code>
///
/// The host registers an implementation with
/// <c>rt.RegisterHost("MonoGame.Screen", impl, typeof(IScreen))</c> — the same
/// API as <see cref="InterfaceHostResolver"/> uses. The difference is that
/// dispatch is driven by the module's own metadata rather than an external
/// source generator.
///
/// Both resolvers coexist: <c>InterfaceHostResolver</c> handles host interfaces
/// with generated dispatch, and this resolver handles IR-declared bindings.
/// </summary>
public sealed class NativeBindingResolver : INativeResolver
{
    // hostBindingName → (host instance, interface type)
    private readonly Dictionary<string, (object Host, Type InterfaceType)> _hosts = new(StringComparer.Ordinal);

    // qualifiedName → Func<object?[], object?> (cached delegates)
    private readonly Dictionary<string, Func<object?[], object?>> _cache = new(StringComparer.Ordinal);

    public void RegisterHost(string name, object host, Type interfaceType)
    {
        _hosts[name] = (host, interfaceType);
        _cache.Clear(); // host changed, drop cached delegates
    }

    public void UnregisterHost(string name)
    {
        _hosts.Remove(name);
        _cache.Clear();
    }

    /// <inheritdoc />
    public Delegate? TryResolve(string qualifiedName, object?[] args)
    {
        if (_cache.TryGetValue(qualifiedName, out var cached))
            return cached;

        var dot = qualifiedName.LastIndexOf('.');
        if (dot < 0) return null;

        var hostName = qualifiedName[..dot];
        var methodName = qualifiedName[(dot + 1)..];

        if (!_hosts.TryGetValue(hostName, out var entry))
            return null;

        var iface = entry.InterfaceType;
        var methods = iface.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(m => m.Name == methodName)
            .ToArray();

        if (methods.Length == 0) return null;

        var best = methods.Length == 1
            ? methods[0]
            : methods.FirstOrDefault(m => m.GetParameters().Length == args.Length) ?? methods[0];

        var host = entry.Host;
        Func<object?[], object?> fn = a =>
        {
            try
            {
                return best.Invoke(host, a);
            }
            catch (System.Reflection.TargetInvocationException tie)
            {
                throw tie.InnerException ?? tie;
            }
        };

        _cache[qualifiedName] = fn;
        return fn;
    }

    /// <summary>
    /// Scan the module for classes marked with <c>@NativeBinding("Name")</c> and
    /// auto-register their methods against the given host tables.
    /// Returns the number of binding classes found.
    /// </summary>
    public int ScanModule(ORBTModule mod, Action<string>? logger = null)
    {
        int count = 0;
        foreach (var type in mod.Types)
        {
            string? bindingName = null;
            foreach (var attr in type.Attributes)
            {
                var attrName = mod.Resolve(attr.NameIndex);
                if (attrName == "NativeBinding" && attr.ArgIndices.Count > 0)
                {
                    bindingName = mod.Resolve(attr.ArgIndices[0]);
                    // Unquote if needed
                    if (bindingName.StartsWith("\"") && bindingName.EndsWith("\""))
                        bindingName = bindingName[1..^1];
                    break;
                }
            }

            if (bindingName == null) continue;

            logger?.Invoke($"@NativeBinding(\"{bindingName}\") on {mod.Resolve(type.NameIndex)}");
            count++;
        }
        return count;
    }

    /// <summary>Clear the host registry and dispatch cache.</summary>
    public void Reset()
    {
        _hosts.Clear();
        _cache.Clear();
    }
}
