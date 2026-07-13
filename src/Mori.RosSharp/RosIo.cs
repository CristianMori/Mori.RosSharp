using System.Buffers.Binary;
using System.Text;

namespace Mori.RosSharp;

/// <summary>A ROS time value: unsigned seconds + nanoseconds since the epoch.</summary>
public readonly record struct RosTime(uint Secs, uint Nsecs)
{
    public static RosTime FromSeconds(double seconds)
    {
        uint secs = (uint)seconds;
        return new RosTime(secs, (uint)Math.Clamp((seconds - secs) * 1e9, 0, 999_999_999));
    }

    public double ToSeconds() => Secs + Nsecs * 1e-9;
}

/// <summary>A ROS duration: signed seconds + nanoseconds.</summary>
public readonly record struct RosDuration(int Secs, int Nsecs)
{
    public double ToSeconds() => Secs + Nsecs * 1e-9;
}

/// <summary>
/// Writes the ROS 1 wire format: little-endian primitives, strings and variable arrays prefixed
/// with a uint32 count, times as two uint32. Used by generated message classes and the dynamic
/// serializer.
/// </summary>
public sealed class RosWriter
{
    private readonly MemoryStream _s;

    public RosWriter(int capacity = 256) => _s = new MemoryStream(capacity);

    public void Write(bool v) => _s.WriteByte(v ? (byte)1 : (byte)0);
    public void Write(sbyte v) => _s.WriteByte(unchecked((byte)v));
    public void Write(byte v) => _s.WriteByte(v);
    public void Write(short v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteInt16LittleEndian(b, v); _s.Write(b); }
    public void Write(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); _s.Write(b); }
    public void Write(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); _s.Write(b); }
    public void Write(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); _s.Write(b); }
    public void Write(long v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian(b, v); _s.Write(b); }
    public void Write(ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, v); _s.Write(b); }
    public void Write(float v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteSingleLittleEndian(b, v); _s.Write(b); }
    public void Write(double v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteDoubleLittleEndian(b, v); _s.Write(b); }

    public void Write(string v)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(v ?? "");
        Write((uint)utf8.Length);
        _s.Write(utf8);
    }

    public void Write(RosTime v) { Write(v.Secs); Write(v.Nsecs); }
    public void Write(RosDuration v) { Write(v.Secs); Write(v.Nsecs); }

    /// <summary>The uint32 element-count prefix of a variable-length array.</summary>
    public void WriteLength(int count) => Write((uint)count);

    public byte[] ToArray() => _s.ToArray();
}

/// <summary>Reads the ROS 1 wire format (the mirror of <see cref="RosWriter"/>).</summary>
public sealed class RosReader
{
    private readonly byte[] _b;
    private int _at;

    public RosReader(byte[] body) => _b = body;

    /// <summary>Bytes not yet consumed (0 after a complete parse).</summary>
    public int Remaining => _b.Length - _at;

    public bool ReadBool() => _b[_at++] != 0;
    public sbyte ReadInt8() => unchecked((sbyte)_b[_at++]);
    public byte ReadUInt8() => _b[_at++];
    public short ReadInt16() { short v = BinaryPrimitives.ReadInt16LittleEndian(_b.AsSpan(_at)); _at += 2; return v; }
    public ushort ReadUInt16() { ushort v = BinaryPrimitives.ReadUInt16LittleEndian(_b.AsSpan(_at)); _at += 2; return v; }
    public int ReadInt32() { int v = BinaryPrimitives.ReadInt32LittleEndian(_b.AsSpan(_at)); _at += 4; return v; }
    public uint ReadUInt32() { uint v = BinaryPrimitives.ReadUInt32LittleEndian(_b.AsSpan(_at)); _at += 4; return v; }
    public long ReadInt64() { long v = BinaryPrimitives.ReadInt64LittleEndian(_b.AsSpan(_at)); _at += 8; return v; }
    public ulong ReadUInt64() { ulong v = BinaryPrimitives.ReadUInt64LittleEndian(_b.AsSpan(_at)); _at += 8; return v; }
    public float ReadFloat32() { float v = BinaryPrimitives.ReadSingleLittleEndian(_b.AsSpan(_at)); _at += 4; return v; }
    public double ReadFloat64() { double v = BinaryPrimitives.ReadDoubleLittleEndian(_b.AsSpan(_at)); _at += 8; return v; }

    public string ReadString()
    {
        int len = (int)ReadUInt32();
        string v = Encoding.UTF8.GetString(_b, _at, len);
        _at += len;
        return v;
    }

    public RosTime ReadTime() => new(ReadUInt32(), ReadUInt32());
    public RosDuration ReadDuration() => new(ReadInt32(), ReadInt32());

    /// <summary>The uint32 element-count prefix of a variable-length array.</summary>
    public int ReadLength() => (int)ReadUInt32();
}
