using System.Buffers.Binary;
using System.Text;

namespace Mori.RosSharp;

/// <summary>
/// TCPROS wire framing. A connection opens with one header from each side — a 4-byte little-endian
/// total length, then repeated fields, each a 4-byte length + <c>key=value</c> in UTF-8. After the
/// handshake every message is a 4-byte length + the serialized body.
/// </summary>
public static class TcpRos
{
    /// <summary>Encodes a connection header from key/value fields.</summary>
    public static byte[] EncodeHeader(IReadOnlyDictionary<string, string> fields)
    {
        using var body = new MemoryStream();
        foreach ((string k, string v) in fields)
        {
            byte[] field = Encoding.UTF8.GetBytes($"{k}={v}");
            WriteLength(body, field.Length);
            body.Write(field);
        }
        var outStream = new MemoryStream();
        WriteLength(outStream, (int)body.Length);
        body.WriteTo(outStream);
        return outStream.ToArray();
    }

    /// <summary>Reads one connection header from the stream.</summary>
    public static Dictionary<string, string> ReadHeader(Stream stream)
    {
        int total = ReadLength(stream);
        if (total is < 0 or > 1024 * 1024) throw new IOException("TCPROS: implausible header length");
        byte[] raw = ReadExactly(stream, total);

        var fields = new Dictionary<string, string>();
        for (int at = 0; at < raw.Length;)
        {
            int len = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(at)); at += 4;
            string field = Encoding.UTF8.GetString(raw, at, len); at += len;
            int eq = field.IndexOf('=');
            if (eq > 0) fields[field[..eq]] = field[(eq + 1)..];
        }
        return fields;
    }

    /// <summary>Writes one length-prefixed serialized message.</summary>
    public static void WriteMessage(Stream stream, ReadOnlySpan<byte> body)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(len, body.Length);
        stream.Write(len);
        stream.Write(body);
    }

    /// <summary>Reads one length-prefixed serialized message body.</summary>
    public static byte[] ReadMessage(Stream stream)
    {
        int len = ReadLength(stream);
        if (len is < 0 or > 64 * 1024 * 1024) throw new IOException("TCPROS: implausible message length");
        return ReadExactly(stream, len);
    }

    private static void WriteLength(Stream s, int value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, value);
        s.Write(b);
    }

    private static int ReadLength(Stream s) =>
        BinaryPrimitives.ReadInt32LittleEndian(ReadExactly(s, 4));

    private static byte[] ReadExactly(Stream s, int count)
    {
        var buf = new byte[count];
        s.ReadExactly(buf);
        return buf;
    }
}
