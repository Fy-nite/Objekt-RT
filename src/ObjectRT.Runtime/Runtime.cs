using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using ObjectRT.Abstractions;
using ObjektRT.Core.Model;
using ObjektRT.Core.Parsing;
using ObjectRT.VM;
// Alias so bare "MethodInfo" keeps meaning System.Reflection.MethodInfo
// (used by ObjectRTDispatchProxy); module reflection lives under its own name.
using ObjectRTReflection = ObjectRT.Runtime.Reflection;

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
public sealed class Runtime : IHostedRuntime
{
    /// <summary>Shared singleton instance used by generated proxy code.</summary>
    public static Runtime Shared { get; } = new();

    private CompiledModule? _compiled;
    private IExecutor? _executor;
    private System.Collections.Concurrent.ConcurrentDictionary<string, long>? _callCounts;

    /// <summary>
    /// Instruction budget (VM steps) applied to every interpreter this runtime
    /// creates, including spawned threads and delegate invocations. 0 (default)
    /// means unlimited. Set before loading/running untrusted content scripts to
    /// cap runaway loops. Propagated to fresh interpreters in
    /// <see cref="SpawnThread"/>, <see cref="StartThread"/> and
    /// <see cref="InvokeDelegate"/>.
    /// </summary>
    public long MaxSteps { get; set; }

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

    /// <summary>
    /// When set and <see cref="Mode"/> is <see cref="JitMode.Reflection"/>,
    /// compiled assemblies are cached to this directory as <c>{hash}.dll</c>.
    /// Subsequent runs skip Roslyn compilation when the module hasn't changed.
    /// </summary>
    public static string? CacheDir
    {
        get => ObjectRT.VM.ReflectionJit.CacheDir;
        set => ObjectRT.VM.ReflectionJit.CacheDir = value;
    }

    /// <summary>
    /// When non-null, every executor counts how many times each module
    /// function is entered. Set this before running to enable call-graph
    /// collection for the <c>--emit-callgraph</c> CLI flag.
    /// </summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, long>? CallCounts
    {
        get => _callCounts;
        set
        {
            _callCounts = value;
            if (_executor is ExecutorBase ex) ex.CallCounts = value;
        }
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

        // Add the CLR reflection resolver by default. Must be initialized
        // before RegisterStdLib, which registers [ClassBinding] types via it.
        // Disable via ClrResolver.AllowReflection = false for NativeAOT.
        ClrResolver = new ClrNativeResolver();
        _resolvers.Add(ClrResolver);

        StdLibRegistrar.RegisterStdLib(this);

        // Host interface resolver (source-generated hardwired dispatch with
        // reflection fallback). Disable via HostResolver.AllowReflection = false.
        HostResolver = new InterfaceHostResolver();
        _resolvers.Add(HostResolver);

        // IR-level @NativeBinding resolver — reads module metadata for host
        // bindings declared in the ObjectIL source. Coexists with HostResolver.
        NativeResolver = new NativeBindingResolver();
        _resolvers.Add(NativeResolver);

        // @DllImport resolver — bridges ObjectIL calls to native P/Invoke
        // libraries. Generates bridge assemblies on first use.
        DllResolver = new DllImportResolver();
        _resolvers.Add(DllResolver);
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
    /// Resolver for host objects registered through <c>@NativeBinding</c>
    /// annotations in the module source. Complements <see cref="HostResolver"/>:
    /// this path is driven by IR-level metadata, the other by C# interface
    /// contracts. Both register hosts through the same <c>RegisterHost</c> API.
    /// </summary>
    public NativeBindingResolver NativeResolver { get; }

    /// <summary>
    /// Resolver for <c>@DllImport("lib.dll")</c> native library bindings.
    /// Generates P/Invoke bridge assemblies via Roslyn on first use and
    /// caches them. All marshaling is handled by the CLR.
    /// </summary>
    public DllImportResolver DllResolver { get; }

    /// <summary>
    /// Register a host implementation under its binding name. Scripts call it
    /// as <c>callnative Name.Method(...)</c>. Prefer interfaces marked with
    /// <see cref="IRHostBindingAttribute"/> so the source generator emits a
    /// reflection-free dispatch adapter.
    /// </summary>
    public void RegisterHost<T>(string name, T host) where T : class
    {
        HostResolver.RegisterHost(name, host);
        NativeResolver.RegisterHost(name, host, typeof(T));
    }

    /// <summary>
    /// Register a host implementation under its binding name with an explicit
    /// interface type.
    /// </summary>
    public void RegisterHost(string name, object host, Type interfaceType)
    {
        HostResolver.RegisterHost(name, host, interfaceType);
        NativeResolver.RegisterHost(name, host, interfaceType);
    }

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

    // ── IHostedRuntime ────────────────────────────────────────────

    /// <inheritdoc />
    public void RegisterBinding(string name, Type type)
        => RegisterClrType(name, type);

    /// <summary>
    /// The generic ObjectRT runtime has no assembly-level binding attribute
    /// (bindings are registered per-type via <see cref="RegisterBinding"/>),
    /// so this is a no-op. Contract's runtime overrides this to scan for
    /// <c>[ClassBinding]</c> attributes.
    /// </summary>
    public void RegisterBindingAssembly(Assembly assembly)
    {
    }

    /// <summary>
    /// Scans the module's import metadata, loads it, and runs its entry point
    /// (the static <c>Main</c> of the first type that has one). Mirrors the
    /// load-then-run sequence the CLI uses so bundled executables behave
    /// identically to <c>objectrt run</c>.
    /// </summary>
    public object? RunModule(ORBTModule module)
        => RunModule(module, null);

    /// <summary>
    /// Scans the module's import metadata, loads it, and runs its entry point
    /// (the static <c>Main</c> of the first type that has one), passing the
    /// command-line arguments through to a C#-style <c>Main(string[] args)</c>.
    /// When the entry declares no parameter, the arguments are ignored.
    /// Mirrors the load-then-run sequence the CLI uses so bundled executables
    /// behave identically to <c>objectrt run</c>.
    /// </summary>
    public object? RunModule(ORBTModule module, string[]? args)
    {
        DllResolver.ScanModule(module, null);
        NativeResolver.ScanModule(module, null);
        LoadModule(module);

        string? entry = null;
        bool takesArgs = false;
        foreach (var t in module.Types)
        {
            var name = $"{module.Resolve(t.NameIndex)}.Main";
            foreach (var m in t.Methods)
            {
                if (module.Resolve(m.NameIndex) == "Main")
                {
                    entry = name;
                    takesArgs = m.ParamCount > 0;
                    break;
                }
            }
            if (entry != null) break;
        }
        if (entry is null)
            throw new InvalidOperationException("No entry point (class with static method Main) found.");
        return CallMethod<object?>(entry, takesArgs ? new object?[] { args ?? Array.Empty<string>() } : Array.Empty<object?>());
    }

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

        // Thread.Spawn(delegate) — the delegate is an object handle into the
        // shared heap; run it on a fresh interpreter sharing the module state.
        RegisterNative("Thread.Spawn",
            (Func<object?, object?>)((object? d) => { SpawnThread(d); return null; }));
        RegisterNative("Thread.Spawn(1)",
            (Func<object?, object?>)((object? d) => { SpawnThread(d); return null; }));

        // Thread handles — C#-style lifecycle: Thread.Create(work) gives you a
        // thread value you can store, start explicitly, join, and poll.
        RegisterNative("Thread.Create",
            (Func<object?, object?>)(d => CreateThread(d)));
        RegisterNative("Thread.Create(1)",
            (Func<object?, object?>)(d => CreateThread(d)));
        RegisterNative("Thread.Start",
            (Action<object?>)(t => StartThread(RequireThread(t))));
        RegisterNative("Thread.Start(1)",
            (Action<object?>)(t => StartThread(RequireThread(t))));
        RegisterNative("Thread.Join",
            (Action<object?>)(t => RequireThread(t).Join()));
        RegisterNative("Thread.Join(1)",
            (Action<object?>)(t => RequireThread(t).Join()));
        RegisterNative("Thread.IsAlive",
            (Func<object?, bool>)(t => RequireThread(t).IsAlive));
        RegisterNative("Thread.IsAlive(1)",
            (Func<object?, bool>)(t => RequireThread(t).IsAlive));

        // List.Map / List.Filter / List.Reduce — higher-order stdlib helpers
        // (FEATURE_PROPOSALS §7). The fn argument arrives as a boxed uint: the
        // raw heap handle of a Contract delegate. Each element flows through
        // InvokeDelegate, which runs on a fresh interpreter sharing this
        // runtime's module state.
        RegisterNative("List.Map",
            (Func<object?, object?, object?>)((list, fn) => ListMap(list, fn)));
        RegisterNative("List.Map(2)",
            (Func<object?, object?, object?>)((list, fn) => ListMap(list, fn)));
        RegisterNative("List.Filter",
            (Func<object?, object?, object?>)((list, fn) => ListFilter(list, fn)));
        RegisterNative("List.Filter(2)",
            (Func<object?, object?, object?>)((list, fn) => ListFilter(list, fn)));
        RegisterNative("List.Reduce",
            (Func<object?, object?, object?, object?>)((list, fn, seed) => ListReduce(list, fn, seed)));
        RegisterNative("List.Reduce(3)",
            (Func<object?, object?, object?, object?>)((list, fn, seed) => ListReduce(list, fn, seed)));
    }

    private static global::System.Collections.Generic.List<object> RequireList(object? list)
        => list as global::System.Collections.Generic.List<object>
           ?? throw new ArgumentException("List.Map/Filter/Reduce expect a List created with List.Create().");

    private static uint RequireDelegate(object? fn)
        => fn is uint h ? h
           : throw new ArgumentException("List.Map/Filter/Reduce expect a function value (fun ...).");

    private object? ListMap(object? list, object? fn)
    {
        var h = RequireDelegate(fn);
        var result = new global::System.Collections.Generic.List<object>();
        foreach (var item in RequireList(list))
            result.Add(InvokeDelegate(h, item)!);
        return result;
    }

    private object ListFilter(object? list, object? fn)
    {
        var h = RequireDelegate(fn);
        var result = new global::System.Collections.Generic.List<object>();
        foreach (var item in RequireList(list))
        {
            if (Convert.ToBoolean(InvokeDelegate(h, item), global::System.Globalization.CultureInfo.InvariantCulture))
                result.Add(item);
        }
        return result;
    }

    private object? ListReduce(object? list, object? fn, object? seed)
    {
        var h = RequireDelegate(fn);
        var acc = seed;
        foreach (var item in RequireList(list))
            acc = InvokeDelegate(h, acc, item);
        return acc;
    }

    /// <summary>
    /// Wraps a VM delegate handle in a <see cref="ThreadHandle"/> — the value
    /// form of a thread. Nothing runs until <c>Thread.Start</c> is called.
    /// </summary>
    private object CreateThread(object? d)
    {
        if (d is not uint h)
            throw new ArgumentException("Thread.Create argument must be a delegate (fun ...).");
        return new ThreadHandle(h);
    }

    /// <summary>Coerces a native argument to a <see cref="ThreadHandle"/>.</summary>
    private static ThreadHandle RequireThread(object? t)
        => t as ThreadHandle
           ?? throw new ArgumentException("Thread argument must be a Thread.Create() handle.");

    /// <summary>
    /// Starts a <see cref="ThreadHandle"/>: its delegate runs on a fresh
    /// interpreter sharing this runtime's module state, so the delegate and
    /// its closure are valid on the new thread.
    /// </summary>
    private void StartThread(ThreadHandle thread)
    {
        if (thread.Started)
            throw new InvalidOperationException("Thread already started.");
        if (_compiled == null || _executor == null)
            throw new InvalidOperationException("No module loaded.");

        var mod = _compiled;
        var state = ((ExecutorBase)_executor).State;
        thread.Launch(() =>
        {
            try
            {
                var exec = new Interpreter(mod, state);
                exec.NativeCallHandler = ResolveNativeCall;
                exec.MaxSteps = MaxSteps;
                var result = exec.RunDelegate(thread.DelegateHandle, Array.Empty<Value>());
                if (result.IsError)
                    Console.Error.WriteLine($"; Thread error: {result.Error}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"; Thread exception: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Starts an OS thread whose entry point runs the given delegate value.
    /// The new thread uses a fresh <see cref="Interpreter"/> sharing this
    /// runtime's module state (heap/statics/strings), so the delegate and its
    /// closure are valid on the new thread. Fire-and-forget: no join/result in
    /// v1; the thread is a background thread.
    /// </summary>
    public void SpawnThread(object? handle)
    {
        if (_compiled == null || _executor == null)
            throw new InvalidOperationException("No module loaded.");
        if (handle is not uint h)
            throw new ArgumentException("Thread.Spawn argument must be a delegate handle.");

        var mod = _compiled;
        var state = ((ExecutorBase)_executor).State;
        var t = new Thread(() =>
        {
            try
            {
                var exec = new Interpreter(mod, state);
                exec.NativeCallHandler = ResolveNativeCall;
                exec.MaxSteps = MaxSteps;
                var result = exec.RunDelegate(h, Array.Empty<Value>());
                if (result.IsError)
                    Console.Error.WriteLine($"; Thread error: {result.Error}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"; Thread exception: {ex.Message}");
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    /// <summary>
    /// Invokes a delegate value (the object handle produced by a lambda) on
    /// this runtime, passing the given args, and returns its result. Runs on a
    /// fresh <see cref="Interpreter"/> sharing the module state
    /// (heap/statics/strings), so it is safe to call from host callbacks —
    /// UI threads, timers, native bindings — whether or not the VM is
    /// mid-execution (re-entrancy never resets the outer frames).
    /// </summary>
    public object? InvokeDelegate(object? handle, params object?[] args)
    {
        if (_compiled == null || _executor == null)
            throw new InvalidOperationException("No module loaded.");
        if (handle is not uint h)
            throw new ArgumentException("Delegate handle required (a lambda value).");

        var mod = _compiled;
        var state = ((ExecutorBase)_executor).State;
        var exec = new Interpreter(mod, state);
        exec.NativeCallHandler = ResolveNativeCall;
        exec.MaxSteps = MaxSteps;

        var vmArgs = new Value[args.Length];
        for (int i = 0; i < args.Length; i++)
            vmArgs[i] = exec.MarshalValue(args[i]);

        var result = exec.RunDelegate(h, vmArgs);
        if (result.IsError)
            throw new InvalidOperationException(result.Error.ToString());
        return exec.ValueToObject(result.Value);
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
        DllImportResolver.AddSearchDirectory(Path.GetDirectoryName(path));
        var module = OilFileReader.ParseFile(path);
        LoadModule(module);
    }

    /// <summary>Load an ObjectRT module from an already-parsed ORBTModule.</summary>
    public void LoadModule(ORBTModule module)
    {
        // Retained so the loaded program can be inspected via GetReflector().
        LoadedModule = module;

        // Strip placeholder bodies from @DllImport classes so the interpreter
        // falls through to the DllImportResolver instead of executing the stub.
        // The .orbt reader decodes raw bytes into Instructions, so overwriting
        // RawInstructionData alone is ignored by VmCompiler (it re-encodes the
        // decoded list when it is non-empty) — clear both.
        foreach (var type in module.Types)
        {
            bool isDllImport = type.Attributes.Any(a =>
                module.Resolve(a.NameIndex) == "DllImport");
            if (isDllImport)
            {
                foreach (var m in type.Methods)
                {
                    m.Instructions.Clear();
                    m.RawInstructionData = new byte[] { 0x18 }; // single "ret"
                }
            }
        }

        var result = VmCompiler.Compile(module);
        if (result.IsError)
            throw new InvalidOperationException($"Compilation failed: {result.Error}");

        _compiled = result.Value;
        _executor = CreateExecutor(_compiled);

        // Serve the in-language Reflect binding from this runtime.
        ObjektRT.Stdlib.System.Reflect.Host = new Reflection.RuntimeReflectHost(this);
    }

    /// <summary>Whether a module is currently loaded and ready.</summary>
    public bool IsLoaded => _compiled != null;

    /// <summary>
    /// The source module most recently loaded (retained for reflection via
    /// <see cref="GetReflector"/>). Null when nothing has been loaded yet.
    /// </summary>
    public ORBTModule? LoadedModule { get; private set; }

    /// <summary>
    /// Reset the executor state between top-level calls. Creates a fresh
    /// executor so per-call state (stack, frames) is clean.
    /// </summary>
    public void ResetExecutor()
    {
        if (_compiled != null)
            _executor = CreateExecutor(_compiled);
    }

    // ── Reflection ─────────────────────────────────────────────────

    /// <summary>
    /// C#-style reflection over the loaded module: enumerate types, methods,
    /// fields and attributes, walk inheritance hierarchies, and get invokable
    /// method references (including inherited ones). Returns null when no
    /// module is loaded.
    /// </summary>
    public ObjectRTReflection.ModuleReflector? GetReflector() =>
        LoadedModule != null ? new ObjectRTReflection.ModuleReflector(LoadedModule) : null;

    /// <summary>
    /// Reads a static field's current value by qualified name ("Type.field").
    /// Returns null when nothing is loaded or the field is unknown.
    /// </summary>
    public object? GetStaticField(string qualifiedName)
    {
        if (_compiled == null || _executor is not ExecutorBase ex) return null;
        if (!_compiled.FieldMap.TryGetValue(qualifiedName, out var idx)) return null;
        return ex.ValueToObject(ex.StaticFields[(int)idx]);
    }

    /// <summary>
    /// Writes a static field by qualified name ("Type.field"). No-op when
    /// nothing is loaded or the field is unknown.
    /// </summary>
    public void SetStaticField(string qualifiedName, object? value)
    {
        if (_compiled == null || _executor is not ExecutorBase ex) return;
        if (!_compiled.FieldMap.TryGetValue(qualifiedName, out var idx)) return;
        ex.StaticFields[(int)idx] = ex.MarshalValue(value);
    }

    /// <summary>
    /// Allocates a VM object of the named type (as declared in the loaded
    /// module) and returns its heap handle as a boxed <see cref="uint"/>. Pass
    /// the handle back to <see cref="GetField"/> / <see cref="SetField"/> and
    /// as arg[0] when calling instance methods. Returns null when nothing is
    /// loaded or the type is unknown.
    /// </summary>
    public object? AllocateObject(string typeName)
    {
        if (_compiled == null || _executor is not ExecutorBase ex) return null;
        int typeIdx = -1;
        for (int i = 0; i < _compiled.Types.Count; i++)
        {
            if (_compiled.Types[i].DebugName == typeName) { typeIdx = i; break; }
        }
        if (typeIdx < 0) return null;
        var type = _compiled.GetType((uint)typeIdx);
        uint handle = (uint)ex.Heap.Count;
        ex.Heap.Add(new byte[type.InstanceSize]);
        return handle;
    }

    /// <summary>
    /// Whether a field (by qualified name "Type.field") is static. Instance
    /// fields need an object handle; static ones live in the static table.
    /// </summary>
    public bool IsStaticField(string qualifiedName)
    {
        if (LoadedModule == null) return false;
        int dot = qualifiedName.LastIndexOf('.');
        if (dot <= 0 || dot >= qualifiedName.Length - 1) return false;
        string typeName = qualifiedName[..dot];
        string fieldName = qualifiedName[(dot + 1)..];
        foreach (var t in LoadedModule.Types)
        {
            if (LoadedModule.Resolve(t.NameIndex) != typeName) continue;
            foreach (var f in t.Fields)
            {
                if (LoadedModule.Resolve(f.NameIndex) == fieldName)
                    return f.IsStatic;
            }
        }
        return false;
    }

    /// <summary>
    /// Reads a field by qualified name ("Type.field"). Static fields need no
    /// instance (pass null); instance fields require the object handle returned
    /// by <see cref="AllocateObject"/>. Returns null when nothing is loaded,
    /// the field is unknown, or the instance handle is invalid.
    /// </summary>
    public object? GetField(string qualifiedName, object? instance = null)
    {
        if (_compiled == null || _executor is not ExecutorBase ex) return null;
        if (!_compiled.FieldMap.TryGetValue(qualifiedName, out var idx)) return null;
        if (IsStaticField(qualifiedName))
            return ex.ValueToObject(ex.StaticFields[(int)idx]);
        if (instance is not uint h || h >= ex.Heap.Count) return null;
        var fld = _compiled.Fields[(int)idx];
        if (fld.Offset + VmConstants.FieldSlotSize > ex.Heap[(int)h].Length) return null;
        var val = MemoryMarshal.Read<Value>(ex.Heap[(int)h].AsSpan((int)fld.Offset, (int)VmConstants.FieldSlotSize));
        return ex.ValueToObject(val);
    }

    /// <summary>
    /// Writes a field by qualified name ("Type.field"). Static fields need no
    /// instance (pass null); instance fields require the object handle returned
    /// by <see cref="AllocateObject"/>. No-op when nothing is loaded, the field
    /// is unknown, or the instance handle is invalid.
    /// </summary>
    public void SetField(string qualifiedName, object? value, object? instance = null)
    {
        if (_compiled == null || _executor is not ExecutorBase ex) return;
        if (!_compiled.FieldMap.TryGetValue(qualifiedName, out var idx)) return;
        var marshaled = ex.MarshalValue(value);
        if (IsStaticField(qualifiedName))
        {
            ex.StaticFields[(int)idx] = marshaled;
            return;
        }
        if (instance is not uint h || h >= ex.Heap.Count) return;
        var fld = _compiled.Fields[(int)idx];
        if (fld.Offset + VmConstants.FieldSlotSize > ex.Heap[(int)h].Length) return;
        MemoryMarshal.Write(ex.Heap[(int)h].AsSpan((int)fld.Offset, (int)VmConstants.FieldSlotSize), in marshaled);
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
            return InvokeNative(native, args);
        }

        var withParamCount = $"{qualifiedName}({args.Length})";
        if (_nativeMethods.TryGetValue(withParamCount, out native))
        {
            resolved = true;
            return InvokeNative(native, args);
        }

        // Builtins are registered under signature-qualified keys (e.g.
        // "System.Console.WriteLine(string)"), but a `callnative` operand
        // carries only the bare name plus a separate param count. When the
        // exact / param-count lookups miss, match a signature key whose name
        // prefix is the bare name — if exactly one such builtin exists.
        var prefix = qualifiedName + "(";
        string? signatureMatch = null;
        foreach (var key in _nativeMethods.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                if (signatureMatch != null) { signatureMatch = null; break; } // ambiguous overloads: don't guess
                signatureMatch = key;
            }
        }
        if (signatureMatch != null)
        {
            resolved = true;
            return _nativeMethods[signatureMatch].DynamicInvoke(args);
        }

        foreach (var resolver in _resolvers)
        {
            var del = resolver.TryResolve(qualifiedName, args);
            if (del != null)
            {
                resolved = true;
                return del is Func<object?[], object?> wrapper
                    ? wrapper(args)
                    : InvokeNative(del, args);
            }
        }

        return null;
    }

    /// <summary>
    /// Invokes a native delegate, unwrapping the <see cref="TargetInvocationException"/>
    /// that <see cref="Delegate.DynamicInvoke(object[])"/> wraps around exceptions
    /// thrown by the delegate, so script error messages show the real cause.
    /// </summary>
    private static object? InvokeNative(Delegate d, object?[] args)
    {
        try
        {
            return d.DynamicInvoke(args);
        }
        catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw; // unreachable
        }
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

    /// <summary>
    /// Wires the host's native-call resolver onto an externally created
    /// executor (e.g. a debug interpreter driven outside LoadModule/Run).
    /// </summary>
    public void AttachHostHandlers(IExecutor vm)
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
        if (vm is Interpreter ip) ip.MaxSteps = MaxSteps;
        if (vm is ExecutorBase eb) eb.CallCounts = _callCounts;
        AttachHostHandlers(vm);
        return vm;
    }

    private object? CallMethodViaVm(string qualifiedName, object?[] args)
    {
        if (_compiled == null)
            throw new InvalidOperationException("No module loaded. Call LoadModule() first.");

        // Inheritance-aware: "Derived.Method" also resolves when the method is
        // declared on a base type (most-derived declaration wins).
        var funcIdx = _compiled.ResolveFunction(qualifiedName);
        if (funcIdx == uint.MaxValue)
            throw new MissingMethodException($"Method '{qualifiedName}' not found in loaded module and no resolver handled it.");

        // Reuse executor across calls — Reset() clears per-call state. BUT when
        // this call comes from inside a running VM function (re-entrancy — e.g.
        // a host binding like Reflect.Call invoking another module method),
        // Reset() would wipe the OUTER frames. Use a fresh interpreter sharing
        // the same heap/statics instead.
        IExecutor vm;
        if (_executor is Interpreter active && active.IsExecuting)
        {
            vm = new Interpreter(_compiled, active.State);
            AttachHostHandlers(vm);
            if (vm is Interpreter ip) ip.MaxSteps = MaxSteps;
        }
        else
        {
            vm = _executor ??= CreateExecutor(_compiled);
            vm.Reset(clearHeap: false, clearStatics: false);
        }

        var vmArgs = args.Length > 0 ? new Value[args.Length] : Array.Empty<Value>();
        for (int i = 0; i < args.Length; i++)
            vmArgs[i] = vm.MarshalValue(args[i]);

        var result = vm.RunFunction(funcIdx, vmArgs);

        if (result.IsError)
            throw new InvalidOperationException(result.Error.ToString());

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
