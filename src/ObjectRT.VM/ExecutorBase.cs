using ObjectRT.Abstractions;

namespace ObjectRT.VM;

/// <summary>
/// Shared state between all executor implementations: heap, static fields,
/// string table, value marshaling, and the native call hook.
/// </summary>
public abstract class ExecutorBase : IExecutor
{
    protected readonly CompiledModule Mod;

    // Heap — each object is a byte buffer sized by the type's instance_size
    protected readonly List<byte[]> Heap = new();

    // Static field storage
    protected readonly Value[] StaticFields;

    // Interned string table (handles — the Value struct can't hold CLR refs).
    private readonly Dictionary<string, uint> _stringMap = new(StringComparer.Ordinal);
    private readonly List<string?> _strings = new();

    /// <summary>Optional handler invoked by the <c>call</c> opcode for host methods.</summary>
    private Func<string, object?[], object?>? _nativeCall;

    public Func<string, object?[], object?>? NativeCallHandler
    {
        get => _nativeCall;
        set => _nativeCall = value;
    }

    protected ExecutorBase(CompiledModule mod)
    {
        Mod = mod;
        StaticFields = new Value[mod.Fields.Count];
        Array.Fill(StaticFields, Value.Nil());
    }

    // ── Abstract (implemented by Interpreter / JIT) ──────────────────

    public abstract Result<Value> RunFunction(uint funcIdx, Value[] args);
    public abstract void Reset(bool clearHeap = false, bool clearStatics = false);

    public Result<Value> Run()
    {
        if (!Mod.HasEntry)
            return new VmError(VmErrorKind.UnresolvedEntryPoint, "module has no entry point");
        return RunFunction(Mod.EntryFunction, Array.Empty<Value>());
    }

    // ── String table ──────────────────────────────────────────────────

    public uint InternString(string s)
    {
        if (_stringMap.TryGetValue(s, out var idx)) return idx;
        idx = (uint)_strings.Count;
        _strings.Add(s);
        _stringMap[s] = idx;
        return idx;
    }

    public string? GetStringValue(uint idx)
        => idx < _strings.Count ? _strings[(int)idx] : null;

    // ── Value marshaling ──────────────────────────────────────────────

    public Value MarshalValue(object? val) => val switch
    {
        null => Value.Nil(),
        string s => Value.FromStr(InternString(s)),
        _ => Value.FromObject(val),
    };

    public object? ValueToObject(Value v) => v.Tag switch
    {
        ValueTag.Str => GetStringValue(v.AsStr()),
        _ => Value.ToObject(v),
    };

    // ── Heap helpers ─────────────────────────────────────────────────

    protected Result<uint> AllocObject(uint typeIdx)
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
