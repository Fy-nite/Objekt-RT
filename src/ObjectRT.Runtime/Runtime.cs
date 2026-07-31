using System.Linq;
using System.Reflection;
using ObjectRT.Abstractions;
using ObjectRT.Reader;
using ObjectRT.VM;

namespace ObjectRT.Runtime;

/// <summary>
/// Host for ObjectRT scripts in .NET.
///
/// Method dispatch order:
///   1. Explicitly registered native methods (RegisterNative)
///   2. Pluggable INativeResolver chain (CLR reflection, LLVM, etc.)
///   3. VM interpreter / JIT
///
/// Usage:
/// <code>
/// var rt = new Runtime();
/// rt.LoadModule("module MyApp ... class Calc { ... }");
///
/// // Strongly typed proxy (source-generated):
/// var calc = rt.Bind&lt;ICalc&gt;();
/// int sum = calc.Add(3, 4);
///
/// // Dynamic by name:
/// int sum = rt.CallMethod&lt;int&gt;("Calc.Add", 3, 4);
/// </code>
/// </summary>
public sealed class Runtime
{
    /// <summary>Shared singleton instance used by generated proxy code.</summary>
    public static Runtime Shared { get; } = new();

    private CompiledModule? _compiled;
    private IExecutor? _executor;

    /// <summary>Explicitly registered native methods (fast path).</summary>
    private readonly Dictionary<string, Delegate> _nativeMethods = new(StringComparer.Ordinal);

    /// <summary>Pluggable resolver chain, tried after explicit registry.</summary>
    private readonly List<INativeResolver> _resolvers = new();

    /// <summary>
    /// The execution mode for VM functions.
    ///   <see cref="JitMode.Interpreter"/> — iterative dispatch loop (default)
    ///   <see cref="JitMode.Reflection"/> — compile-on-load via Roslyn C# emit
    ///   Planned: <see cref="JitMode.LLVM"/> — NativeAOT-safe via LLVMSharp
    /// </summary>
    public JitMode Mode { get; set; } = JitMode.Interpreter;

    /// <summary>
    /// When set and <see cref="Mode"/> is <see cref="JitMode.Reflection"/>,
    /// the generated C# source for compiled modules is written to this directory
    /// as <c>{ModuleName}.ObjectRT.g.cs</c>. Leave null to skip disk output.
    /// </summary>
    public static string? EmitDir
    {
        get => ObjectRT.VM.ReflectionJit.EmitDir;
        set => ObjectRT.VM.ReflectionJit.EmitDir = value;
    }

    // ── Constructor ────────────────────────────────────────────────

    /// <summary>
    /// Create a new Runtime. Registers built-in console/string natives
    /// and adds a <see cref="ClrNativeResolver"/> plus an
    /// <see cref="InterfaceHostResolver"/> by default.
    /// </summary>
    public Runtime()
    {
        RegisterBuiltins();

        // Add the CLR reflection resolver by default.
        // Disable via ClrResolver.AllowReflection = false for NativeAOT.
        ClrResolver = new ClrNativeResolver();
        _resolvers.Add(ClrResolver);

        // Host interface resolver (source-generated hardwired dispatch with
        // reflection fallback). Disable via HostResolver.AllowReflection = false.
        HostResolver = new InterfaceHostResolver();
        _resolvers.Add(HostResolver);
    }

    /// <summary>
    /// The CLR reflection resolver. Set <c>AllowReflection = false</c>
    /// for NativeAOT or when you want VM-only dispatch.
    /// </summary>
    public ClrNativeResolver ClrResolver { get; }

    /// <summary>
    /// Resolver for host objects registered through interface contracts.
    /// Generated dispatch is always hardwired; set <c>AllowReflection = false</c>
    /// to drop the reflection fallback for NativeAOT.
    /// </summary>
    public InterfaceHostResolver HostResolver { get; }

    /// <summary>
    /// Register a host implementation under its binding name. Scripts call it
    /// as <c>callnative Name.Method(...)</c>. Prefer interfaces marked with
    /// <see cref="IRHostBindingAttribute"/> so the source generator emits a
    /// reflection-free dispatch adapter.
    /// </summary>
    public void RegisterHost<T>(string name, T host) where T : class
        => HostResolver.RegisterHost(name, host);

    /// <summary>
    /// Register a host implementation under its binding name with an explicit
    /// interface type.
    /// </summary>
    public void RegisterHost(string name, object host, Type interfaceType)
        => HostResolver.RegisterHost(name, host, interfaceType);

    /// <summary>
    /// Register a type with the CLR resolver so its static methods
    /// are auto-discovered by reflection and callable from ObjectRT.
    /// Shortcut for <c>rt.ClrResolver.RegisterType&lt;T&gt;(name)</c>.
    /// </summary>
    public void RegisterClrType<T>(string? name = null)
        => ClrResolver.RegisterType<T>(name);

    /// <summary>
    /// Register a type with the CLR resolver.
    /// Shortcut for <c>rt.ClrResolver.RegisterType(name, type)</c>.
    /// </summary>
    public void RegisterClrType(string name, Type type)
        => ClrResolver.RegisterType(name, type);

    // ── Resolver chain ─────────────────────────────────────────────

    /// <summary>
    /// Add a custom resolver to the chain. Resolvers are tried in order
    /// after the explicit native registry and before the VM fallback.
    /// </summary>
    public void AddResolver(INativeResolver resolver)
    {
        _resolvers.Add(resolver);
    }

    // ── Built-in natives ───────────────────────────────────────────

    private void RegisterBuiltins()
    {
        RegisterNative("System.Console.WriteLine(string)",
            (string? s) => Console.WriteLine(s));
        RegisterNative("System.Console.Write(string)",
            (string? s) => Console.Write(s));
        RegisterNative("System.Console.ReadLine",
            () => Console.ReadLine());
        RegisterNative("System.Console.Clear",
            () => Console.Clear());
        RegisterNative("System.String.Concat(string,string)",
            (string? a, string? b) => string.Concat(a, b));
        RegisterNative("System.String.IsNullOrEmpty(string)",
            (string? s) => string.IsNullOrEmpty(s));
    }

    // ── Module loading ─────────────────────────────────────────────

    /// <summary>Load an ObjectRT module from ObjectIL source code.</summary>
    public void LoadModule(string oilSource)
    {
        var module = OilFileReader.ParseString(oilSource);
        LoadModule(module);
    }

    /// <summary>Load an ObjectRT module from an .oil file.</summary>
    public void LoadModuleFile(string path)
    {
        var module = OilFileReader.ParseFile(path);
        LoadModule(module);
    }

    /// <summary>Load an ObjectRT module from an already-parsed ORBTModule.</summary>
    public void LoadModule(ORBTModule module)
    {
        var result = VmCompiler.Compile(module);
        if (result.IsError)
            throw new InvalidOperationException($"Compilation failed: {result.Error}");

        _compiled = result.Value;
        _executor = CreateExecutor(_compiled);
    }

    /// <summary>Whether a module is currently loaded and ready.</summary>
    public bool IsLoaded => _compiled != null;

    /// <summary>
    /// Reset the executor state between top-level calls. Creates a fresh
    /// executor so per-call state (stack, frames) is clean.
    /// </summary>
    public void ResetExecutor()
    {
        if (_compiled != null)
            _executor = CreateExecutor(_compiled);
    }

    // ── Native method registration ─────────────────────────────────

    /// <summary>
    /// Register a C# method that ObjectRT code can call.
    /// The signature should match what the ObjectRT code uses,
    /// e.g. "Calculator.Add(int32,int32)".
    /// </summary>
    public void RegisterNative(string signature, Delegate method)
    {
        _nativeMethods[signature] = method;
    }

    /// <summary>Register a C# Action that ObjectRT code can call.</summary>
    public void RegisterNative(string signature, Action method)
        => _nativeMethods[signature] = method;

    // ── Calling methods ────────────────────────────────────────────

    /// <summary>
    /// Call an ObjectRT method by its qualified name ("Type.Method")
    /// and return the result as <typeparamref name="T"/>.
    /// </summary>
    public T CallMethod<T>(string qualifiedName, params object?[] args)
    {
        var result = CallMethodInternal(qualifiedName, args);

        if (result is null) return default!;
        if (result is T t) return t;

        var targetType = typeof(T);
        if (targetType == typeof(int))    return (T)(object)Convert.ToInt32(result);
        if (targetType == typeof(long))   return (T)(object)Convert.ToInt64(result);
        if (targetType == typeof(float))  return (T)(object)Convert.ToSingle(result);
        if (targetType == typeof(double)) return (T)(object)Convert.ToDouble(result);
        if (targetType == typeof(string)) return (T)(object)(result.ToString() ?? "");
        if (targetType == typeof(bool))   return (T)(object)Convert.ToBoolean(result);

        return (T)result;
    }

    private object? CallMethodInternal(string qualifiedName, object?[] args)
    {
        // ── 1. Explicit natives + pluggable resolver chain ─────────
        var native = TryResolveNative(qualifiedName, args, out var resolved);
        if (resolved)
            return native;

        // ── 2. VM interpreter / JIT fallback ────────────────────────
        return CallMethodViaVm(qualifiedName, args);
    }

    /// <summary>
    /// Resolve a native method through the explicit registry and the
    /// pluggable resolver chain.
    /// </summary>
    /// <param name="resolved">True when a handler was found and invoked (its
    /// result may legitimately be null — void methods, null returns).</param>
    /// <returns>The invocation result, or null when nothing handled the name.</returns>
    private object? TryResolveNative(string qualifiedName, object?[] args, out bool resolved)
    {
        resolved = false;

        if (_nativeMethods.TryGetValue(qualifiedName, out var native))
        {
            resolved = true;
            return native.DynamicInvoke(args);
        }

        var withParamCount = $"{qualifiedName}({args.Length})";
        if (_nativeMethods.TryGetValue(withParamCount, out native))
        {
            resolved = true;
            return native.DynamicInvoke(args);
        }

        foreach (var resolver in _resolvers)
        {
            var del = resolver.TryResolve(qualifiedName, args);
            if (del != null)
            {
                resolved = true;
                return del is Func<object?[], object?> wrapper
                    ? wrapper(args)
                    : del.DynamicInvoke(args);
            }
        }

        return null;
    }

    /// <summary>
    /// Entry point for the VM's <c>callnative</c> opcode: resolve a native
    /// method through the same chain as <see cref="CallMethodInternal"/> and
    /// return its result. Throws when unresolvable, which the interpreter
    /// surfaces as a script runtime error.
    /// </summary>
    private object? ResolveNativeCall(string name, object?[] args)
    {
        var result = TryResolveNative(name, args, out var resolved);
        if (resolved) return result;
        throw new MissingMethodException($"Native method '{name}' not found");
    }

    private void AttachHostHandlers(IExecutor vm)
    {
        vm.NativeCallHandler = ResolveNativeCall;
    }

    private IExecutor CreateExecutor(CompiledModule mod)
    {
        IExecutor vm = Mode switch
        {
            JitMode.Reflection => new ReflectionJit(mod),
            _ => new Interpreter(mod),
        };
        AttachHostHandlers(vm);
        return vm;
    }

    private object? CallMethodViaVm(string qualifiedName, object?[] args)
    {
        if (_compiled == null)
            throw new InvalidOperationException("No module loaded. Call LoadModule() first.");

        if (!_compiled.FunctionMap.TryGetValue(qualifiedName, out var funcIdx))
            throw new MissingMethodException($"Method '{qualifiedName}' not found in loaded module and no resolver handled it.");

        // Reuse executor across calls — Reset() clears per-call state.
        var vm = _executor ??= CreateExecutor(_compiled);

        var vmArgs = args.Length > 0 ? new Value[args.Length] : Array.Empty<Value>();
        for (int i = 0; i < args.Length; i++)
            vmArgs[i] = vm.MarshalValue(args[i]);

        vm.Reset(clearHeap: false, clearStatics: false);

        var result = vm.RunFunction(funcIdx, vmArgs);

        if (result.IsError)
            throw new InvalidOperationException($"Runtime error: {result.Error}");

        return vm.ValueToObject(result.Value);
    }

    // ── Strongly-typed binding ─────────────────────────────────────

    /// <summary>
    /// Get a strongly-typed proxy for an ObjectRT class.
    /// Requires a source-generated proxy registered via <see cref="ProxyRegistry"/>,
    /// or falls back to DispatchProxy-based dynamic dispatch.
    /// </summary>
    public T Bind<T>(string? className = null) where T : class
    {
        // Try source-generated proxy first
        var binding = new IRRuntimeBinding(this, className ?? typeof(T).Name);
        if (ProxyRegistry.TryCreate<T>(binding, out var proxy))
            return proxy!;

        // Fall back to DispatchProxy for ad-hoc usage without a source generator
        var dispatch = DispatchProxy.Create<T, ObjectRTDispatchProxy>();
        var dispatchProxy = (ObjectRTDispatchProxy)(object)dispatch;
        dispatchProxy.Init(binding);
        return dispatch;
    }
}

// ── DispatchProxy fallback ───────────────────────────────────────────

/// <summary>
/// Fallback DispatchProxy for <see cref="Runtime.Bind{T}"/> when no
/// source-generated proxy is available. Uses reflection to dispatch calls.
/// </summary>
internal sealed class ObjectRTDispatchProxy : DispatchProxy
{
    private IRRuntimeBinding? _binding;

    public void Init(IRRuntimeBinding binding) => _binding = binding;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod == null) return null;

        var binding = _binding ?? throw new InvalidOperationException("Proxy not initialized.");
        var methodName = targetMethod.Name;
        var returnType = targetMethod.ReturnType;

        var result = binding.Invoke<object>(methodName, args ?? Array.Empty<object?>());

        if (returnType == typeof(void))
            return null;

        // Convert result to the expected return type
        if (result != null && returnType.IsInstanceOfType(result))
            return result;

        if (returnType == typeof(int) && result is long l) return (int)l;
        if (returnType == typeof(long)) return Convert.ToInt64(result);
        if (returnType == typeof(float)) return Convert.ToSingle(result);
        if (returnType == typeof(double)) return Convert.ToDouble(result);
        if (returnType == typeof(string)) return result?.ToString();
        if (returnType == typeof(bool)) return Convert.ToBoolean(result);

        return result;
    }
}
