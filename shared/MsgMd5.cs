using System.Security.Cryptography;
using System.Text;

namespace Mori.RosSharp.Msg;

/// <summary>
/// The ROS message md5sum: MD5 of the canonicalized definition text. Canonical form is one line
/// per constant (<c>type name=value</c>, values as originally written) followed by one line per
/// field — builtin fields keep their declared type including array brackets
/// (<c>float64[36] covariance</c>), nested-message fields are replaced by the nested type's md5
/// with the brackets dropped (<c>&lt;md5&gt; transforms</c>) — joined with '\n', no trailing
/// newline. Getting this byte-exact matters: the md5 gates every TCPROS handshake.
/// </summary>
public static class MsgMd5
{
    public static string Compute(MsgSpec spec, MsgCatalog catalog)
    {
        var lines = new List<string>();
        foreach (MsgConstant c in spec.Constants)
            lines.Add($"{c.Type} {c.Name}={c.ValueText}");
        foreach (MsgField f in spec.Fields)
            lines.Add(f.IsBuiltin
                ? $"{f.RawType} {f.Name}"
                : $"{catalog.Md5(f.BaseType)} {f.Name}");

        byte[] hash;
        using (var md5 = MD5.Create())
            hash = md5.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", lines)));

        var hex = new StringBuilder(32);
        foreach (byte b in hash) hex.Append(b.ToString("x2"));
        return hex.ToString();
    }
}
