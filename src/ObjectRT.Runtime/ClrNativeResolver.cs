using System.Collections.Concurrent;
using System.Reflection;

namespace ObjectRT.Runtime
{


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

    /// <summary>
    /// Returns all registered type names and their CLR types.
    /// Used by <c>--list-imports</c> to enumerate available types.
    /// </summary>
    public IReadOnlyDictionary<string, Type> GetRegisteredTypes() => _typeMap;

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

        // Parse "TypeName.MethodName". Constructors are emitted as "Type..ctor"
        // (a double dot before ".ctor"); splitting on the LAST dot would hand
        // the resolver a type name with a trailing dot ("System.Text.StringBuilder.")
        // and a methodName of "ctor". Detect that exact suffix so the type
        // parses cleanly and the method name stays ".ctor".
        string typeName;
        string methodName;
        if (qualifiedName.EndsWith("..ctor", StringComparison.Ordinal))
        {
            typeName = qualifiedName[..^".ctor".Length].TrimEnd('.');
            methodName = ".ctor";
        }
        else
        {
            var dot = qualifiedName.LastIndexOf('.');
            if (dot < 0)
            {
                _cache.TryAdd(cacheKey, s_notFoundSentinel);
                return null;
            }
            typeName = qualifiedName[..dot];
            methodName = qualifiedName[(dot + 1)..];
        }

        if (!_typeMap.TryGetValue(typeName, out var clrType))
        {
            _cache.TryAdd(cacheKey, s_notFoundSentinel);
            return null;
        }

        // ── Constructor dispatch ─────────────────────────────────
        // A ClrImport facade constructor ("Type..ctor") maps to the CLR type's
        // public constructor; the result is a new instance (an external handle).
        if (methodName == ".ctor")
        {
            var ctors = clrType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .Where(c => !c.IsStatic)
                .ToArray();
            var ctor = MatchConstructor(ctors, args);
            if (ctor == null)
            {
                _cache.TryAdd(cacheKey, s_notFoundSentinel);
                return null;
            }
            Delegate ctorDel = new Func<object?[], object?>(a =>
            {
                try { return ctor.Invoke(CoerceArgs(ctor.GetParameters(), a)); }
                catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }
            });
            _cache.TryAdd(cacheKey, ctorDel);
            return ctorDel;
        }

        // ── Instance dispatch ────────────────────────────────────
        // When the first argument is a CLR receiver object of the target type
        // (and no static method matches), treat the call as an *instance*
        // method: invoke it ON the receiver, with args[1..] as the parameters.
        // The receiver is an external object handle unmarshalled by the VM.
        if (args.Length >= 1 && args[0] != null
            && (clrType.IsInstanceOfType(args[0]) || IsAssignableFrom(clrType, args[0]!.GetType())))
        {
            var instMethods = clrType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => !m.IsStatic && m.Name == methodName)
                .ToArray();
            // Parameters for an instance call exclude the receiver (args[1..]).
            var instParams = args.Skip(1).ToArray();
            MethodInfo? instBest = MatchMethod(instMethods, instParams);
            if (instBest != null)
            {
                var target = instBest;
                // The delegate must read the CURRENT invocation args (a), not
                // capture the first-call values — the VM re-invokes the cached
                // delegate with fresh args on every call site. `target` is fixed
                // by the cache key (name + arg types), but receiver/params change.
                Delegate instDel = new Func<object?[], object?>(a =>
                {
                    try
                    {
                        var r = a.Length >= 1 ? a[0] : null;
                        var realParams = a.Skip(1).ToArray();
                        var co = CoerceArgs(target.GetParameters(), realParams);
                        var all = new object?[co.Length + 1];
                        all[0] = r;
                        for (int i = 0; i < co.Length; i++) all[i + 1] = co[i];
                        return target.Invoke(r, all[1..]);
                    }
                    catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }
                });
                _cache.TryAdd(cacheKey, instDel);
                return instDel;
            }
        }

        // ── Field access ─────────────────────────────────────────
        // "Type.Field" maps to a public instance field or property. The VM
        // calls it with the receiver as args[0] (read: args.Length == 1;
        // write: args.Length == 2 with the new value in args[1]).
        var prop = clrType.GetProperty(methodName, BindingFlags.Public | BindingFlags.Instance);
        var field = clrType.GetField(methodName, BindingFlags.Public | BindingFlags.Instance);
        if (prop != null || field != null)
        {
            var pTarget = prop;
            var fTarget = field;
            // Read when a single receiver arg is supplied ~ the receiver is
            // args[0]; write when a value is supplied alongside it (args[1]).
            Delegate fDel;
            if (pTarget != null)
            {
                fDel = new Func<object?[], object?>(a =>
                {
                    var r = a[0];
                    if (a.Length >= 2)
                    {
                        pTarget.SetValue(r, CoerceTo(pTarget.PropertyType, a[1]));
                        return null;
                    }
                    return pTarget.GetValue(r);
                });
            }
            else
            {
                fDel = new Func<object?[], object?>(a =>
                {
                    var r = a[0];
                    if (a.Length >= 2)
                    {
                        fTarget.SetValue(r, CoerceTo(fTarget.FieldType, a[1]));
                        return null;
                    }
                    return fTarget.GetValue(r);
                });
            }
            _cache.TryAdd(cacheKey, fDel);
            return fDel;
        }

        // Find matching public static method by name, then rank overloads by
        // how well their parameter types match the actual argument values
        // (exact type wins, then VM-coercible, then assignable). Falls back
        // to arity-only matching for callers that pass object handles the VM
        // can't type precisely.
        var methods = clrType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == methodName)
            .ToArray();

        MethodInfo? best = MatchMethod(methods, args);

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

    /// <summary>Ranks and selects the best matching method for the supplied arguments.</summary>
    private static MethodInfo? MatchMethod(IEnumerable<MethodInfo> methods, object?[] args)
    {
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
        return best;
    }

    private static ConstructorInfo? MatchConstructor(IEnumerable<ConstructorInfo> ctors, object?[] args)
    {
        ConstructorInfo? best = null;
        int bestScore = -1;
        foreach (var c in ctors)
        {
            var ps = c.GetParameters();
            if (ps.Length != args.Length) continue;
            int score = 0;
            bool compatible = true;
            for (int i = 0; i < ps.Length && compatible; i++)
            {
                var target = ps[i].ParameterType;
                var v = args[i];
                if (v == null)
                {
                    if (!target.IsValueType || Nullable.GetUnderlyingType(target) != null) score += 1;
                    else compatible = false;
                    continue;
                }
                var vt = v.GetType();
                if (target == vt) score += 3;
                else if (target == typeof(double) && (v is int or float or long)) score += 2;
                else if (target == typeof(float) && v is int) score += 2;
                else if (target == typeof(long) && v is int) score += 2;
                else if (target.IsAssignableFrom(vt)) score += 1;
                else compatible = false;
            }
            if (compatible && score > bestScore) { bestScore = score; best = c; }
        }
        return best;
    }

    private static bool IsAssignableFrom(Type target, Type actual)
    {
        try { return target.IsAssignableFrom(actual); }
        catch { return false; }
    }

    /// <summary>Coerces VM-marshaled argument values to a method's declared parameter types.</summary>
    private static object?[] CoerceArgs(ParameterInfo[] parameters, object?[] args)
    {
        for (int i = 0; i < parameters.Length && i < args.Length; i++)
            args[i] = CoerceTo(parameters[i].ParameterType, args[i]);
        return args;
    }

    /// <summary>Coerces a single value to a target CLR type (numeric widening, bool, string, arrays).</summary>
    private static object? CoerceTo(Type target, object? v)
    {
        if (v == null) return null;
        if (target.IsInstanceOfType(v)) return v;
        if (target == typeof(bool) && v is int i0) return i0 != 0;
        if (target == typeof(int) && v is bool b0) return b0 ? 1 : 0;
        if (target == typeof(double) && v is int i1) return (double)i1;
        if (target == typeof(double) && v is long l1) return (double)l1;
        if (target == typeof(double) && v is float f1) return (double)f1;
        if (target == typeof(float) && v is int i2) return (float)i2;
        if (target == typeof(float) && v is long l2) return (float)l2;
        if (target == typeof(float) && v is double d2) return (float)d2;
        if (target == typeof(long) && v is int i3) return (long)i3;
        if (target == typeof(string)) return v.ToString();
        if (target.IsArray && v is System.Array srcArr) return CoerceArray(srcArr, target.GetElementType()!);
        return v;
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
                    else if (target == typeof(double) && v is float f5)
                        args[i] = (double)f5;
                    else if (target == typeof(float) && v is int i6)
                        args[i] = (float)i6;
                    else if (target == typeof(float) && v is long l6)
                        args[i] = (float)l6;
                    else if (target == typeof(float) && v is double d6)
                        args[i] = (float)d6;
                    else if (target == typeof(long) && v is int i7)
                        args[i] = (long)i7;
                    else if (target.IsArray && v is System.Array srcArr)
                        args[i] = CoerceArray(srcArr, target.GetElementType()!);
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

    /// <summary>
    /// Converts a VM array (stored as <c>object[]</c>) to a typed array of the
    /// declared element type, coercing each element (e.g. <c>object[]</c> of
    /// ints → <c>int[]</c>, or of strings → <c>string[]</c>). Returns the
    /// original array when the element types already match.
    /// </summary>
    private static System.Array CoerceArray(System.Array src, System.Type elementType)
    {
        if (src.GetType().GetElementType() == elementType) return src;
        var dst = System.Array.CreateInstance(elementType, src.Length);
        for (int i = 0; i < src.Length; i++)
        {
            var v = src.GetValue(i);
            dst.SetValue(CoerceScalar(v, elementType), i);
        }
        return dst;
    }

    /// <summary>Coerces a single value to a target scalar type (numeric widening, bool, string).</summary>
    private static object? CoerceScalar(object? v, System.Type target)
    {
        if (v == null) return null;
        if (target.IsInstanceOfType(v)) return v;
        if (target == typeof(bool) && v is int i) return i != 0;
        if (target == typeof(int) && v is bool b) return b ? 1 : 0;
        if (target == typeof(double) && v is int i2) return (double)i2;
        if (target == typeof(double) && v is long l2) return (double)l2;
        if (target == typeof(double) && v is float f2) return (double)f2;
        if (target == typeof(float) && v is int i3) return (float)i3;
        if (target == typeof(float) && v is long l3) return (float)l3;
        if (target == typeof(float) && v is double d3) return (float)d3;
        if (target == typeof(long) && v is int i4) return (long)i4;
        if (target == typeof(string)) return v.ToString();
        return v;
    }
}
}
