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
    public List<byte[]> Heap => State.Heap;

    /// <summary>Static field storage.</summary>
    public Value[] StaticFields => State.StaticFields;

    private Func<string, object?[], object?>? _nativeCall;

    public Func<string, object?[], object?>? NativeCallHandler
    {
        get => _nativeCall;
        set => _nativeCall = value;
    }

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
        var data = new byte[type.InstanceSize];
        uint handle = (uint)Heap.Count;
        Heap.Add(data);
        return handle;
    }
}
