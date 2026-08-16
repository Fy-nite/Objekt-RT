using System.Runtime.InteropServices;

namespace ObjectRT.VM;

/// <summary>
/// Marshals VM struct objects (heap byte buffers, one 16-byte <see cref="Value"/>
/// slot per field) to and from the C layout used by the DllImport P/Invoke bridge.
///
/// Layout rule: the compiler emits struct fields with their native C wire widths
/// (byte → uint8, int → int32, ...), so the packed layout mirrors C natural
/// alignment — each field is aligned to its own width and the struct is padded
/// up to its largest field alignment. This matches both the
/// <c>[StructLayout(LayoutKind.Sequential)]</c> structs the bridge generates (the
/// CLR lays blittable primitives out the same way) and real C libraries like
/// raylib on the x64 ABI the runtime targets. A field whose wire name is not a
/// known struct and not a primitive (e.g. an enum) is treated as int32, matching
/// the bridge's default mapping.
/// </summary>
public static class StructMarshaller
{
    private sealed class LayoutInfo
    {
        public int Size;
        public int Align;
    }

    /// <summary>Index of the struct type with this wire name, or -1. Falls back to a last-segment match.</summary>
    public static int FindStructIndex(CompiledModule mod, string? name)
    {
        if (mod == null || string.IsNullOrEmpty(name)) return -1;
        for (int i = 0; i < mod.Types.Count; i++)
            if (mod.Types[i].Kind == VMTypeKind.Struct
                && string.Equals(mod.Types[i].DebugName, name, StringComparison.Ordinal))
                return i;
        int dot = name.LastIndexOf('.');
        string shortName = dot > 0 && dot < name.Length - 1 ? name[(dot + 1)..] : name;
        for (int i = 0; i < mod.Types.Count; i++)
            if (mod.Types[i].Kind == VMTypeKind.Struct
                && (string.Equals(mod.Types[i].DebugName, shortName, StringComparison.Ordinal)
                    || mod.Types[i].DebugName.EndsWith("." + shortName, StringComparison.Ordinal)))
                return i;
        return -1;
    }

    public static bool IsStructType(CompiledModule mod, string? name) => FindStructIndex(mod, name) >= 0;

    /// <summary>Packs a VM struct object (an Obj value over a heap buffer) into C-layout bytes.</summary>
    public static Result<byte[]> Pack(CompiledModule mod, ExecutorBase ex, string typeName, Value value)
    {
        int ti = FindStructIndex(mod, typeName);
        if (ti < 0)
            return new VmError(VmErrorKind.TypeMismatch, $"'{typeName}' is not a struct type");
        if (value.Tag != ValueTag.Obj)
            return new VmError(VmErrorKind.TypeMismatch, $"struct argument '{typeName}' is not an object");
        uint h = value.AsObj();
        if (h >= ex.Heap.Count)
            return new VmError(VmErrorKind.InvalidObjectHandle, $"struct argument '{typeName}' has an invalid handle");
        var slots = ex.Heap[(int)h];
        var cache = new Dictionary<string, LayoutInfo>(StringComparer.Ordinal);
        var info = LayoutOf(mod, mod.Types[ti].DebugName, cache);
        var data = new byte[info.Size];
        int offset = 0;
        if (!TryPackFields(mod, ex, mod.Types[ti], slots, data, ref offset, cache, out var err))
            return new VmError(VmErrorKind.TypeMismatch, err ?? "struct packing failed");
        return data;
    }

    /// <summary>
    /// Allocates a heap object for the struct type and unpacks C-layout bytes
    /// into its slots; nested structs are allocated their own heap objects and
    /// stored as Obj handles in the parent slot.
    /// </summary>
    public static Result<uint> Unpack(CompiledModule mod, ExecutorBase ex, string typeName, byte[] data)
    {
        int ti = FindStructIndex(mod, typeName);
        if (ti < 0)
            return new VmError(VmErrorKind.TypeMismatch, $"'{typeName}' is not a struct type");
        var alloc = ex.AllocObject((uint)ti);
        if (alloc.IsError) return alloc.Error;
        var slots = ex.Heap[(int)alloc.Value];
        var cache = new Dictionary<string, LayoutInfo>(StringComparer.Ordinal);
        int offset = 0;
        if (!TryUnpackFields(mod, ex, mod.Types[ti], slots, data, ref offset, cache, out var err))
            return new VmError(VmErrorKind.RuntimeError, err ?? "struct unpacking failed");
        return alloc.Value;
    }

    // ── Packing ────────────────────────────────────────────────────

    private static bool TryPackFields(CompiledModule mod, ExecutorBase ex, VMType st, byte[] slots, byte[] data, ref int offset, Dictionary<string, LayoutInfo> cache, out string? error)
    {
        error = null;
        for (int i = 0; i < st.FieldCount; i++)
        {
            string? fname = st.FieldTypeNames != null && st.FieldTypeNames.Length > i ? st.FieldTypeNames[i] : null;
            var fv = MemoryMarshal.Read<Value>(slots.AsSpan(i * (int)VmConstants.FieldSlotSize, 16));
            int subIdx = FindStructIndex(mod, fname);
            if (subIdx >= 0)
            {
                var subInfo = LayoutOf(mod, mod.Types[subIdx].DebugName, cache);
                offset = AlignUp(offset, subInfo.Align);
                if (fv.Tag == ValueTag.Obj && fv.AsObj() < ex.Heap.Count)
                {
                    if (!TryPackFields(mod, ex, mod.Types[subIdx], ex.Heap[(int)fv.AsObj()], data, ref offset, cache, out error))
                        return false;
                }
                else offset += subInfo.Size;   // nil / missing nested struct → zero-fill
                continue;
            }
            var (size, align) = FieldInfo(fname);
            offset = AlignUp(offset, align);
            WriteField(data, ref offset, fv, size, fname);
        }
        return true;
    }

    private static void WriteField(byte[] data, ref int offset, Value fv, int size, string? fname)
    {
        byte[]? bytes = null;
        // The language has no float32 literal: every user-written float is
        // stored as an R8 (double). The native ABI wants R4, so narrow/widen at
        // the marshalling boundary, where the field's wire name tells us the
        // target width. Integers likewise widen to floats when the field is one.
        switch (fname?.ToLowerInvariant())
        {
            case "float32" when fv.Tag == ValueTag.R8:
                bytes = BitConverter.GetBytes((float)fv.R8);
                break;
            case "float32" when fv.Tag == ValueTag.I4:
                bytes = BitConverter.GetBytes((float)fv.I4);
                break;
            case "float64" when fv.Tag == ValueTag.R4:
                bytes = BitConverter.GetBytes((double)fv.R4);
                break;
            case "float64" when fv.Tag == ValueTag.I4:
                bytes = BitConverter.GetBytes((double)fv.I4);
                break;
            default:
                bytes = fv.Tag switch
                {
                    ValueTag.I4 => BitConverter.GetBytes(fv.I4),
                    ValueTag.I8 => BitConverter.GetBytes(fv.I8),
                    ValueTag.R4 => BitConverter.GetBytes(fv.R4),
                    ValueTag.R8 => BitConverter.GetBytes(fv.R8),
                    _ => null,   // nil / string / non-struct obj → zero-filled
                };
                break;
        }
        if (bytes != null && size > 0 && offset + size <= data.Length)
        {
            int n = Math.Min(size, bytes.Length);
            Buffer.BlockCopy(bytes, 0, data, offset, n);
        }
        offset += size;
    }

    // ── Unpacking ──────────────────────────────────────────────────

    private static bool TryUnpackFields(CompiledModule mod, ExecutorBase ex, VMType st, byte[] slots, byte[] data, ref int offset, Dictionary<string, LayoutInfo> cache, out string? error)
    {
        error = null;
        for (int i = 0; i < st.FieldCount; i++)
        {
            string? fname = st.FieldTypeNames != null && st.FieldTypeNames.Length > i ? st.FieldTypeNames[i] : null;
            int subIdx = FindStructIndex(mod, fname);
            if (subIdx >= 0)
            {
                var subInfo = LayoutOf(mod, mod.Types[subIdx].DebugName, cache);
                offset = AlignUp(offset, subInfo.Align);
                if (offset + subInfo.Size > data.Length)
                {
                    error = $"struct '{st.DebugName}': nested '{fname}' overruns native data";
                    return false;
                }
                var alloc = ex.AllocObject((uint)subIdx);
                if (alloc.IsError) { error = alloc.Error.Message; return false; }
                if (!TryUnpackFields(mod, ex, mod.Types[subIdx], ex.Heap[(int)alloc.Value], data, ref offset, cache, out error))
                    return false;
                var nestedVal = Value.FromObj(alloc.Value);
                MemoryMarshal.Write(slots.AsSpan(i * (int)VmConstants.FieldSlotSize, 16), in nestedVal);
                continue;
            }
            var (size, align) = FieldInfo(fname);
            offset = AlignUp(offset, align);
            if (offset + size > data.Length)
            {
                error = $"struct '{st.DebugName}': field {i} overruns native data";
                return false;
            }
            var fieldVal = ReadField(data, offset, size, fname);
            MemoryMarshal.Write(slots.AsSpan(i * (int)VmConstants.FieldSlotSize, 16), in fieldVal);
            offset += size;
        }
        return true;
    }

    private static Value ReadField(byte[] data, int offset, int size, string? fname)
    {
        switch (fname?.ToLowerInvariant())
        {
            case "float32" when size == 4:
                return Value.FromR4(BitConverter.ToSingle(data, offset));
            case "float64" when size == 8:
                return Value.FromR8(BitConverter.ToDouble(data, offset));
            case "int64":
            case "uint64" when size == 8:
                return Value.FromI8(BitConverter.ToInt64(data, offset));
            default:
                return size switch
                {
                    1 => Value.FromI4(data[offset]),
                    2 => Value.FromI4((short)(data[offset] | (data[offset + 1] << 8))),
                    4 => Value.FromI4(BitConverter.ToInt32(data, offset)),
                    8 => Value.FromI8(BitConverter.ToInt64(data, offset)),
                    _ => Value.Nil(),
                };
        }
    }

    // ── C layout ───────────────────────────────────────────────────

    private static int AlignUp(int v, int a) => a <= 1 ? v : (v + a - 1) & ~(a - 1);

    /// <summary>(size, alignment) of a primitive field by its wire name. Unknown
    /// names (enums, object) default to int32, matching the bridge's mapping.</summary>
    private static (int Size, int Align) FieldInfo(string? fname) => fname?.ToLowerInvariant() switch
    {
        "int8" or "uint8" => (1, 1),
        "int16" or "uint16" or "char" => (2, 2),
        "int32" or "uint32" or "float32" or null => (4, 4),
        "int64" or "uint64" or "float64" => (8, 8),
        // bool and everything unknown → 4 bytes (matches the generated C#
        // struct, where a bool marshals as a 4-byte Windows BOOL).
        _ => (4, 4),
    };

    /// <summary>Size and max alignment of a struct type under C natural alignment.</summary>
    private static LayoutInfo LayoutOf(CompiledModule mod, string structName, Dictionary<string, LayoutInfo> cache)
    {
        if (cache.TryGetValue(structName, out var li)) return li;
        int ti = FindStructIndex(mod, structName);
        var st = mod.Types[ti];
        li = new LayoutInfo();   // placeholder (breaks accidental cycles)
        cache[structName] = li;
        int size = 0, maxAlign = 1;
        for (int i = 0; i < st.FieldCount; i++)
        {
            string? fname = st.FieldTypeNames != null && st.FieldTypeNames.Length > i ? st.FieldTypeNames[i] : null;
            int sz, al;
            int sub = FindStructIndex(mod, fname);
            if (sub >= 0)
            {
                var si = LayoutOf(mod, mod.Types[sub].DebugName, cache);
                sz = si.Size;
                al = si.Align;
            }
            else (sz, al) = FieldInfo(fname);
            size = AlignUp(size, al);
            size += sz;
            if (al > maxAlign) maxAlign = al;
        }
        li.Size = AlignUp(size, maxAlign);
        li.Align = maxAlign;
        return li;
    }
}
