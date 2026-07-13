using Mori.RosSharp.Msg;

namespace Mori.RosSharp;

/// <summary>
/// Runtime (reflection-style) serialization for any message type from its .msg definition — no
/// code generation step. Values travel as dictionaries: builtin fields map to their natural CLR
/// types (double, int, string, <see cref="RosTime"/>, …), nested messages to nested dictionaries,
/// arrays to typed arrays (<c>double[]</c>, <c>string[]</c>) or <c>object?[]</c> of dictionaries.
/// Encoding is tolerant: missing keys become zeros/empties, numerics accept any IConvertible,
/// fixed-size arrays are padded or truncated to their declared length.
/// </summary>
public sealed class RosMessageDefinition
{
    private readonly MsgCatalog _catalog;
    private readonly MsgSpec _spec;

    /// <summary>Full type name, e.g. <c>geometry_msgs/Twist</c>.</summary>
    public string Type => _spec.FullType;

    /// <summary>The md5sum computed from the definition (what the TCPROS handshake needs).</summary>
    public string Md5 { get; }

    /// <param name="fullType">Message type, e.g. <c>sensor_msgs/JointState</c>.</param>
    /// <param name="resolveText">Optional source of .msg text by full type name (searched before
    /// the embedded common-message set) — supply this for custom message packages.</param>
    public RosMessageDefinition(string fullType, MsgTextResolver? resolveText = null)
    {
        _catalog = new MsgCatalog(resolveText);
        _spec = _catalog.Get(fullType);
        Md5 = _catalog.Md5(fullType);
    }

    private RosMessageDefinition(MsgCatalog catalog, MsgSpec spec)
    {
        _catalog = catalog;
        _spec = spec;
        Md5 = catalog.Md5(spec.FullType);
    }

    // ---------------------------------------------------------------- encode

    public byte[] Encode(IReadOnlyDictionary<string, object?> values)
    {
        var w = new RosWriter();
        EncodeInto(w, _spec, values);
        return w.ToArray();
    }

    private void EncodeInto(RosWriter w, MsgSpec spec, IReadOnlyDictionary<string, object?>? values)
    {
        foreach (MsgField f in spec.Fields)
        {
            object? v = null;
            values?.TryGetValue(f.Name, out v);
            if (!f.IsArray) { EncodeScalar(w, f, v); continue; }

            object?[] items = v switch
            {
                null => [],
                object?[] arr => arr,
                System.Collections.IEnumerable e and not string => e.Cast<object?>().ToArray(),
                _ => throw new ArgumentException($"{spec.FullType}.{f.Name}: expected an array"),
            };
            int count = f.FixedLength >= 0 ? f.FixedLength : items.Length;
            if (f.FixedLength < 0) w.WriteLength(items.Length);
            for (int i = 0; i < count; i++)
                EncodeScalar(w, f, i < items.Length ? items[i] : null);
        }
    }

    private void EncodeScalar(RosWriter w, MsgField f, object? v)
    {
        switch (f.BaseType)
        {
            case "bool": w.Write(v is not null && Convert.ToBoolean(v)); break;
            case "int8" or "byte": w.Write(v is null ? (sbyte)0 : Convert.ToSByte(v)); break;
            case "uint8" or "char": w.Write(v is null ? (byte)0 : Convert.ToByte(v)); break;
            case "int16": w.Write(v is null ? (short)0 : Convert.ToInt16(v)); break;
            case "uint16": w.Write(v is null ? (ushort)0 : Convert.ToUInt16(v)); break;
            case "int32": w.Write(v is null ? 0 : Convert.ToInt32(v)); break;
            case "uint32": w.Write(v is null ? 0u : Convert.ToUInt32(v)); break;
            case "int64": w.Write(v is null ? 0L : Convert.ToInt64(v)); break;
            case "uint64": w.Write(v is null ? 0UL : Convert.ToUInt64(v)); break;
            case "float32": w.Write(v is null ? 0f : Convert.ToSingle(v)); break;
            case "float64": w.Write(v is null ? 0d : Convert.ToDouble(v)); break;
            case "string": w.Write(v as string ?? ""); break;
            case "time": w.Write(v switch { RosTime t => t, double s => RosTime.FromSeconds(s), null => default, _ => RosTime.FromSeconds(Convert.ToDouble(v)) }); break;
            case "duration": w.Write(v is RosDuration d ? d : default); break;
            default:
                EncodeInto(w, _catalog.Get(f.BaseType), v as IReadOnlyDictionary<string, object?>);
                break;
        }
    }

    // ---------------------------------------------------------------- decode

    public Dictionary<string, object?> Decode(byte[] body) => DecodeFrom(new RosReader(body), _spec);

    private Dictionary<string, object?> DecodeFrom(RosReader r, MsgSpec spec)
    {
        var values = new Dictionary<string, object?>(spec.Fields.Count);
        foreach (MsgField f in spec.Fields)
        {
            if (!f.IsArray) { values[f.Name] = DecodeScalar(r, f); continue; }

            int count = f.FixedLength >= 0 ? f.FixedLength : r.ReadLength();
            // Numeric arrays come back typed (double[] etc.) for convenient consumption.
            values[f.Name] = f.BaseType switch
            {
                "float64" => Fill(count, () => r.ReadFloat64()),
                "float32" => Fill(count, () => r.ReadFloat32()),
                "int32" => Fill(count, () => r.ReadInt32()),
                "uint8" or "char" => Fill(count, () => r.ReadUInt8()),
                "string" => Fill(count, () => r.ReadString()),
                _ => FillObjects(count, () => DecodeScalar(r, f)),
            };
        }
        return values;
    }

    private object? DecodeScalar(RosReader r, MsgField f) => f.BaseType switch
    {
        "bool" => r.ReadBool(),
        "int8" or "byte" => r.ReadInt8(),
        "uint8" or "char" => r.ReadUInt8(),
        "int16" => r.ReadInt16(),
        "uint16" => r.ReadUInt16(),
        "int32" => r.ReadInt32(),
        "uint32" => r.ReadUInt32(),
        "int64" => r.ReadInt64(),
        "uint64" => r.ReadUInt64(),
        "float32" => r.ReadFloat32(),
        "float64" => r.ReadFloat64(),
        "string" => r.ReadString(),
        "time" => r.ReadTime(),
        "duration" => r.ReadDuration(),
        _ => DecodeFrom(r, _catalog.Get(f.BaseType)),
    };

    private static T[] Fill<T>(int count, Func<T> read)
    {
        var a = new T[count];
        for (int i = 0; i < count; i++) a[i] = read();
        return a;
    }

    private static object?[] FillObjects(int count, Func<object?> read)
    {
        var a = new object?[count];
        for (int i = 0; i < count; i++) a[i] = read();
        return a;
    }
}
