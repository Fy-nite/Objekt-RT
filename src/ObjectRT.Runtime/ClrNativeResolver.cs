using System.Collections.Concurrent;
using System.Reflection;

namespace ObjectRT.Runtime;

/// <summary>
/// An <see cref="INativeResolver"/> that uses CLR reflection to resolve
/// ObjectRT methods to real .NET methods on registered types.
///
/// Register types with <see cref="RegisterType{T}"/> or <see cref="RegisterType(string, Type)"/>.
/// Once registered, any ObjectRT call to "TypeName.MethodName" will be
/// dispatched via reflection to the matching .NET method.
///
/// Supports NativeAOT: disable via <see cref="AllowReflection"/> = false,
/// or inspect <see cref="RuntimeFeature.IsDynamicCodeSupported"/> at runtime.
/// </summary>
public sealed class ClrNativeResolver : INativeResolver
{
    private readonly Dictionary<string, Type> _typeMap = new(StringComparer.Ordinal);

    // Cache: qualifiedName → resolved method delegate
    // We use a sentinel object for "not found" so we can distinguish
    // between "not cached" (null) and "cached as not found".
    private readonly ConcurrentDictionary<string, object> _cache = new(StringComparer.Ordinal);

    private static readonly object s_notFoundSentinel = new();

    /// <summary>
    /// When false, <see cref="TryResolve"/> returns null without attempting
    /// reflection. Set this when running under NativeAOT where reflection
    /// is trimmed, or when you explicitly want VM-only dispatch.
    /// Default: true (reflection allowed).
    /// </summary>
    public bool AllowReflection { get; set; } = true;

    /// <summary>
    /// Register a CLR type so its static methods become callable from ObjectRT.
    /// </summary>
    /// <param name="name">Name used in ObjectRT code (e.g. "Console" or "MyApp.Helpers").</param>
    /// <param name="type">The CLR type whose static methods to expose.</param>
    public void RegisterType(string name, Type type)
    {
        _typeMap[name] = type;
    }

    /// <summary>
    /// Register a CLR type so its static methods become callable from ObjectRT.
    /// The type name defaults to <c>typeof(T).Name</c>.
    /// </summary>
    public void RegisterType<T>(string? name = null)
    {
        RegisterType(name ?? typeof(T).Name, typeof(T));
    }

    /// <summary>Register all public static methods from a CLR type directly.</summary>
    public void RegisterTypeWithMethods(string name, Type type)
    {
        RegisterType(name, type);
    }

    /// <inheritdoc />
    public Delegate? TryResolve(string qualifiedName, object?[] args)
    {
        if (!AllowReflection)
            return null;

        // Check cache first (including negative cache)
        if (_cache.TryGetValue(qualifiedName, out var cached))
            return cached as Delegate;

        // Parse "TypeName.MethodName"
        var dot = qualifiedName.LastIndexOf('.');
        if (dot < 0)
        {
            _cache.TryAdd(qualifiedName, s_notFoundSentinel);
            return null;
        }

        var typeName = qualifiedName[..dot];
        var methodName = qualifiedName[(dot + 1)..];

        if (!_typeMap.TryGetValue(typeName, out var clrType))
        {
            _cache.TryAdd(qualifiedName, s_notFoundSentinel);
            return null;
        }

        // Find matching public static method by name + parameter count
        var methods = clrType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == methodName)
            .ToArray();

        MethodInfo? best = null;

        if (methods.Length == 1)
        {
            best = methods[0];
        }
        else if (methods.Length > 1)
        {
            // Try to match by parameter count
            best = methods.FirstOrDefault(m =>
                m.GetParameters().Length == args.Length);
            best ??= methods[0]; // fallback to first
        }

        if (best == null)
        {
            _cache.TryAdd(qualifiedName, s_notFoundSentinel);
            return null;
        }

        // Build a delegate
        var result = CreateInvokeDelegate(best, qualifiedName);
        if (result != null)
            _cache.TryAdd(qualifiedName, result);
        return result;
    }

    private static Delegate? CreateInvokeDelegate(MethodInfo method, string qualifiedName)
    {
        // Wrap MethodInfo.Invoke in a lambda so any caller can invoke it
        // with object?[] args. This works in NativeAOT as long as the
        // method wasn't trimmed, and avoids DynamicInvoke signature issues.
        return new Func<object?[], object?>(args =>
        {
            try
            {
                return method.Invoke(null, args);
            }
            catch (TargetInvocationException tie)
            {
                throw tie.InnerException ?? tie;
            }
        });
    }

    /// <summary>
    /// Clear the resolver's type registry and method cache.
    /// Useful when hot-reloading modules.
    /// </summary>
    public void Reset()
    {
        _typeMap.Clear();
        _cache.Clear();
    }
}
