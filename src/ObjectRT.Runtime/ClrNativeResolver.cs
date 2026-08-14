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

        // The cache is keyed by name + argument types: a name like
        // "System.Math.Abs" has several overloads, and the VM may call it
        // with an int in one place and a double in another.
        string cacheKey = qualifiedName + "|" + string.Join(",", args.Select(a => a?.GetType().Name ?? "null"));

        // Check cache first (including negative cache)
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached as Delegate;

        // Parse "TypeName.MethodName"
        var dot = qualifiedName.LastIndexOf('.');
        if (dot < 0)
        {
            _cache.TryAdd(cacheKey, s_notFoundSentinel);
            return null;
        }

        var typeName = qualifiedName[..dot];
        var methodName = qualifiedName[(dot + 1)..];

        if (!_typeMap.TryGetValue(typeName, out var clrType))
        {
            _cache.TryAdd(cacheKey, s_notFoundSentinel);
            return null;
        }

        // Find matching public static method by name, then rank overloads by
        // how well their parameter types match the actual argument values
        // (exact type wins, then VM-coercible, then assignable). Falls back
        // to arity-only matching for callers that pass object handles the VM
        // can't type precisely.
        var methods = clrType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == methodName)
            .ToArray();

        MethodInfo? best = null;
        int bestScore = -1;

        foreach (var m in methods)
        {
            var ps = m.GetParameters();
            if (ps.Length != args.Length) continue;

            int score = 0;
            bool compatible = true;
            for (int i = 0; i < ps.Length && compatible; i++)
            {
                var target = ps[i].ParameterType;
                var v = args[i];
                if (v == null)
                {
                    if (!target.IsValueType || Nullable.GetUnderlyingType(target) != null)
                        score += 1;
                    else
                        compatible = false;
                    continue;
                }

                var vt = v.GetType();
                if (target == vt) score += 3;
                else if (target == typeof(bool) && v is int) score += 2;            // VM bools arrive as I4
                else if (target == typeof(int) && v is bool) score += 2;
                else if (target == typeof(double) && (v is int or float or long)) score += 2; // numeric widening
                else if (target == typeof(float) && v is int) score += 2;
                else if (target == typeof(long) && v is int) score += 2;
                else if (target.IsAssignableFrom(vt)) score += 1;
                else compatible = false;
            }

            if (compatible && score > bestScore)
            {
                bestScore = score;
                best = m;
            }
        }

        // Fallback: arity-only match (previous behavior) — keeps object
        // handles and untyped args working when no exact signature lines up.
        best ??= methods.FirstOrDefault(m => m.GetParameters().Length == args.Length);
        best ??= methods.Length > 0 ? methods[0] : null;

        if (best == null)
        {
            _cache.TryAdd(cacheKey, s_notFoundSentinel);
            return null;
        }

        // Build a delegate
        var result = CreateInvokeDelegate(best, qualifiedName);
        if (result != null)
            _cache.TryAdd(cacheKey, result);
        return result;
    }

    private static Delegate? CreateInvokeDelegate(MethodInfo method, string qualifiedName)
    {
        var parameters = method.GetParameters();
        // Wrap MethodInfo.Invoke in a lambda so any caller can invoke it
        // with object?[] args. This works in NativeAOT as long as the
        // method wasn't trimmed, and avoids DynamicInvoke signature issues.
        return new Func<object?[], object?>(args =>
        {
            try
            {
                // Coerce VM-marshaled values to the declared parameter types.
                // The VM tags bools as I4 (1/0), so reflection Invoke would
                // reject Debug.Assert(bool, ...) with an int argument.
                for (int i = 0; i < parameters.Length && i < args.Length; i++)
                {
                    var target = parameters[i].ParameterType;
                    var v = args[i];
                    if (v == null) continue;
                    if (target == typeof(bool) && v is int i4)
                        args[i] = i4 != 0;
                    else if (target == typeof(int) && v is bool b)
                        args[i] = b ? 1 : 0;
                    else if (target == typeof(double) && v is int i5)
                        args[i] = (double)i5;
                    else if (target == typeof(double) && v is long l5)
                        args[i] = (double)l5;
                    else if (target == typeof(float) && v is int i6)
                        args[i] = (float)i6;
                    else if (target == typeof(long) && v is int i7)
                        args[i] = (long)i7;
                }
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
