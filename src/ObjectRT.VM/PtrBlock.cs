using System;
using System.Runtime.InteropServices;

namespace ObjectRT.VM;

/// <summary>
/// A managed, bound-checked region of memory that a <c>ManagedPtr&lt;T&gt;</c>
/// references. Because ObjektRT does not yet have a mark-and-sweep GC, a
/// <c>PtrBlock</c> owns an explicit native buffer whose lifetime is manual:
/// allocate with <see cref="Alloc"/>, release with <see cref="Free"/>). It is
/// kept alive on the VM stack as an external object handle
/// (ExecutorState.InternExternal), so the address is stable while a pointer to
/// it is live.
/// </summary>
public sealed class PtrBlock
{
    private IntPtr _data;
    private readonly int _count;
    private readonly int _elementSize;
    private bool _freed;

    private PtrBlock(IntPtr data, int count, int elementSize)
    {
        _data = data;
        _count = count;
        _elementSize = elementSize;
    }

    /// <summary>The raw address of the start of the block.</summary>
    public IntPtr Address => _data;

    /// <summary>Number of elements the block can hold.</summary>
    public int Count => _count;

    /// <summary>Size in bytes of a single element.</summary>
    public int ElementSize => _elementSize;

    /// <summary>Total region length in bytes.</summary>
    public long ByteLength => (long)_count * _elementSize;

    /// <summary>Allocates a zeroed native buffer of <paramref name="count"/> elements each <paramref name="elementSize"/> bytes.</summary>
    public static PtrBlock Alloc(int count, int elementSize)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), "count must be >= 0");
        if (elementSize <= 0) throw new ArgumentOutOfRangeException(nameof(elementSize), "elementSize must be > 0");

        long total = (long)count * elementSize;
        if (total == 0)
            total = 1; // zero-length region still needs a valid (unique) handle
        IntPtr p = Marshal.AllocHGlobal((IntPtr)total);
        try
        {
            // Zero-fill using a temporary span.
            Span<byte> span;
            unsafe { span = new Span<byte>((void*)p, (int)total); }
            span.Clear();
        }
        catch
        {
            Marshal.FreeHGlobal(p);
            throw;
        }
        return new PtrBlock(p, count, elementSize);
    }

    /// <summary>Releases the native buffer. The block becomes unusable (subsequent derefs fail).</summary>
    public void Free()
    {
        if (_freed) return;
        if (_data != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_data);
            _data = IntPtr.Zero;
        }
        _freed = true;
    }

    /// <summary>True once <see cref="Free"/> has been called.</summary>
    public bool IsFreed => _freed;

    /// <summary>Bounds check for a single element read/write at byte offset <paramref name="byteOffset"/> spanning <paramref name="size"/> bytes.</summary>
    public void ValidateRange(long byteOffset, int size)
    {
        if (_freed)
            throw new InvalidOperationException("PtrBlock has been freed");
        if (_data == IntPtr.Zero)
            throw new InvalidOperationException("PtrBlock has no backing storage");
        if (byteOffset < 0 || byteOffset + size > ByteLength)
            throw new IndexOutOfRangeException($"PtrBlock access out of range at byte {byteOffset}, length {ByteLength}");
    }
}
