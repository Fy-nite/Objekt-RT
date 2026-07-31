using System.Runtime.InteropServices;

namespace ObjectRT.Reader;

/// <summary>Little-endian binary stream reader over a byte array.</summary>
public class BinaryStream
{
    private readonly byte[] _data;
    private int _pos;

    public BinaryStream(string path)
    {
        _data = File.ReadAllBytes(path);
        _pos = 0;
    }

    public BinaryStream(byte[] data)
    {
        _data = data;
        _pos = 0;
    }

    public int Position => _pos;
    public int Length => _data.Length;
    public bool Eof => _pos >= _data.Length;

    public void Seek(int pos) => _pos = pos;
    public void Skip(int count) => _pos += count;

    public byte ReadU8()
    {
        if (_pos + 1 > _data.Length)
            throw new InvalidOperationException("Unexpected end of stream");
        return _data[_pos++];
    }

    public ushort ReadU16()
    {
        if (_pos + 2 > _data.Length)
            throw new InvalidOperationException("Unexpected end of stream");
        ushort val = (ushort)(_data[_pos] | (_data[_pos + 1] << 8));
        _pos += 2;
        return val;
    }

    public uint ReadU32()
    {
        if (_pos + 4 > _data.Length)
            throw new InvalidOperationException("Unexpected end of stream");
        uint val = (uint)(_data[_pos] | (_data[_pos + 1] << 8)
                          | (_data[_pos + 2] << 16) | (_data[_pos + 3] << 24));
        _pos += 4;
        return val;
    }

    public int ReadI32() => (int)ReadU32();

    public long ReadI64() => (long)ReadU64();

    public ulong ReadU64()
    {
        if (_pos + 8 > _data.Length)
            throw new InvalidOperationException("Unexpected end of stream");
        ulong val = (ulong)_data[_pos]
                    | ((ulong)_data[_pos + 1] << 8)
                    | ((ulong)_data[_pos + 2] << 16)
                    | ((ulong)_data[_pos + 3] << 24)
                    | ((ulong)_data[_pos + 4] << 32)
                    | ((ulong)_data[_pos + 5] << 40)
                    | ((ulong)_data[_pos + 6] << 48)
                    | ((ulong)_data[_pos + 7] << 56);
        _pos += 8;
        return val;
    }

    public float ReadR4()
    {
        var bits = ReadU32();
        return MemoryMarshal.Cast<uint, float>(new Span<uint>(ref bits))[0];
    }

    public double ReadR8()
    {
        var bits = ReadU64();
        return MemoryMarshal.Cast<ulong, double>(new Span<ulong>(ref bits))[0];
    }

    public string ReadString()
    {
        ushort len = ReadU16();
        if (len == 0) return "";
        if (_pos + len > _data.Length)
            throw new InvalidOperationException("Unexpected end of stream reading string");
        var s = System.Text.Encoding.UTF8.GetString(_data, _pos, len);
        _pos += len;
        return s;
    }

    public byte[] ReadBytes(int count)
    {
        if (_pos + count > _data.Length)
            throw new InvalidOperationException("Unexpected end of stream");
        var result = new byte[count];
        Array.Copy(_data, _pos, result, 0, count);
        _pos += count;
        return result;
    }
}
