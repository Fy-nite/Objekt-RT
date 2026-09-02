using ObjektRT.Core.Model;

namespace ObjectRT.VM;

/// <summary>
/// Base for all executor implementations: owns a reference to the shared
/// <see cref="ExecutorState"/> (heap, statics, strings) and the native call
/// hook. Each executor has its own call stack; the state is shared so object
/// handles are valid across threads.
/// </summary>
public abstract class ExecutorBase : IExecutor
{
    protected readonly CompiledModule Mod;

    /// <summary>The shared module state (heap, statics, string table).</summary>
    public ExecutorState State { get; }

    // Compatibility aliases over State — existing interpreter/JIT code uses
    // Heap/StaticFields/InternString/GetStringValue directly.
    /// <summary>Heap — each object is a byte buffer sized by the type's instance_size.</summary>
    public List<byte[]?> Heap => State.Heap;

    /// <summary>VMHeap accessor seam for V2 handle table.</summary>
    public byte[]? GetHeapBuffer(uint handle) => State.GetHeapBuffer(handle);
    public bool TryGetHeapBuffer(uint handle, out byte[]? buf) => State.TryGetHeapBuffer(handle, out buf);

    /// <summary>Static field storage.</summary>
    public Value[] StaticFields => State.StaticFields;

    private Func<string, object?[], object?>? _nativeCall;

    public ObjectRT.Abstractions.GC.GCStats GCStats => State.GCStats;
    public bool CollectGC(ObjectRT.Abstractions.GC.GCReason reason = ObjectRT.Abstractions.GC.GCReason.Explicit) => State.CollectGC(reason);

    public Func<string, object?[], object?>? NativeCallHandler
    {
        get => _nativeCall;
        set => _nativeCall = value;
    }

    /// <inheritdoc />
    public Dictionary<string, DirectNativeCall> DirectCalls { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// When non-null, the interpreter/JIT increments a count each time a
    /// module function is entered. The dictionary maps
    /// <c>CompiledFunction.DebugName</c> → number of invocations. Useful for
    /// profiling and the <c>--emit-callgraph</c> CLI flag.
    /// </summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, long>? CallCounts { get; set; }

    protected ExecutorBase(CompiledModule mod) : this(mod, null) { }

    protected ExecutorBase(CompiledModule mod, ExecutorState? shared)
    {
        Mod = mod;
        State = shared ?? new ExecutorState(mod);
    }

    // ── IExecutor ──────────────────────────────────────────────────

    public abstract Result<Value> RunFunction(uint funcIdx, Value[] args);
    public abstract void Reset(bool clearHeap = false, bool clearStatics = false);

    public Result<Value> Run()
    {
        if (!Mod.HasEntry)
            return new VmError(VmErrorKind.UnresolvedEntryPoint, "module has no entry point");
        return RunFunction(Mod.EntryFunction, Array.Empty<Value>());
    }

    // ── String table ───────────────────────────────────────────────

    public uint InternString(string s) => State.InternString(s);

    public string? GetStringValue(uint idx) => State.GetStringValue(idx);

    // ── Value marshaling ───────────────────────────────────────────

    public Value MarshalValue(object? val) => val switch
    {
        null => Value.Nil(),
        string s => Value.FromStr(InternString(s)),
        int i => Value.FromI4(i),
        bool b => Value.FromI4(b ? 1 : 0),
        long l => Value.FromI8(l),
        float f => Value.FromR4(f),
        double d => Value.FromR8(d),
        // The VM's uint8/int8/int16/uint16 map onto the I4 tag, so a CLR array
        // element of one of these boxed narrow-int types must come back as an
        // I4 — otherwise it falls through to an opaque external object handle.
        byte ub => Value.FromI4(ub),
        sbyte sb => Value.FromI4(sb),
        short s16 => Value.FromI4(s16),
        ushort u16 => Value.FromI4(u16),
        // A boxed uint is a VM heap handle (ValueToObject returns the raw
        // handle for VM-internal objects), so it round-trips as an Obj value —
        // this is how instance-method receivers flow back into the VM.
        uint h => Value.FromObj(h),
        _ => Value.FromObj(State.InternExternal(val)),
    };

    public object? ValueToObject(Value v) => v.Tag switch
    {
        ValueTag.Str => GetStringValue(v.AsStr()),
        ValueTag.Obj => ExecutorState.IsExternal(v.AsObj()) ? State.GetExternal(v.AsObj()) : v.AsObj(),
        _ => Value.ToObject(v),
    };

    // ── Heap allocation ────────────────────────────────────────────

    public Result<uint> AllocObject(uint typeIdx)
    {
        if (typeIdx >= Mod.Types.Count)
            return new VmError(VmErrorKind.InvalidTypeIndex,
                $"type index {typeIdx} out of bounds ({Mod.Types.Count})");
        var type = Mod.GetType(typeIdx);
        uint instanceSize = type.InstanceSize;

        // — Pressure / threshold / OOM handling (PR4) —
        // Note: AllocatedBytes check is lock-protected inside VMHeap, but we read via State.AllocatedBytes
        // which is itself lock-protected. Trigger STW GC before bump if needed.
        var heapOpts = State.HeapOptions;
        if (heapOpts.MaximumHeapSizeBytes > 0 && State.AllocatedBytes + instanceSize > heapOpts.MaximumHeapSizeBytes)
        {
            State.CollectGC(ObjectRT.Abstractions.GC.GCReason.AllocationFailure);
            if (State.AllocatedBytes + instanceSize > heapOpts.MaximumHeapSizeBytes)
                return new VmError(VmErrorKind.OutOfBounds,
                    $"heap OOM: need {instanceSize} bytes, allocated {State.AllocatedBytes}, max {heapOpts.MaximumHeapSizeBytes}");
        }
        if (State.GC.ShouldCollect(State.AllocatedBytes))
        {
            State.CollectGC(ObjectRT.Abstractions.GC.GCReason.Threshold);
        }
        // Single large object larger than threshold: after GC we still allocate (no loop)

        uint handle = State.VMHeap.Allocate(instanceSize);
        // Remember the allocating type so virtual dispatch can walk the
        // receiver's concrete chain (ExecutorState.ObjectTypes).
        State.RecordObjectType(handle, (int)typeIdx);
        return handle;
    }

    // ── DirectNativeCall bridge ───────────────────────────────────

    /// <summary>
    /// Wrap a legacy <see cref="NativeCallHandler"/> into a
    /// <see cref="DirectNativeCall"/> delegate. The bridge pops
    /// <paramref name="argc"/> values from the stack, converts them
    /// via <see cref="ValueToObject"/>, calls the handler, and pushes
    /// the result via <see cref="MarshalValue"/>.
    /// </summary>
    public DirectNativeCall WrapLegacyNativeCall(string name, int argc)
    {
        return (x, s, sp) =>
        {
            var handler = x.NativeCallHandler
                ?? throw new VmRuntimeException(new VmError(VmErrorKind.UnresolvedMethod, $"'{name}': no native handler"));
            var args = new object?[argc];
            for (int i = 0; i < argc; i++)
                args[i] = x.ValueToObject(s[sp + i]);
            var result = handler(name, args);
            var newSp = sp - argc;
            s[newSp++] = x.MarshalValue(result);
            return newSp;
        };
    }

    /// <summary>
    /// Invoke a <see cref="DirectNativeCall"/> from JIT-generated code.
    /// Copies args from the JIT's local stack into an internal backing array,
    /// calls the direct native, and copies results back.
    /// Returns the new stack pointer.
    /// </summary>
    public int InvokeDirectNative(string name, Value[] jitStack, int sp, int argc)
    {
        if (!DirectCalls.TryGetValue(name, out var call))
            throw new VmRuntimeException(new VmError(VmErrorKind.UnresolvedMethod, $"'{name}': no direct native call"));

        // Copy args from JIT stack into backing array
        var args = new Value[argc];
        for (int i = 0; i < argc; i++)
            args[i] = jitStack[sp + i];

        int newSp = call(this, args, 0);

        // Copy results back to JIT stack
        for (int i = 0; i < newSp; i++)
            jitStack[sp + i] = args[i];

        return sp + newSp;
    }
}
