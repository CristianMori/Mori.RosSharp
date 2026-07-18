using System.Text;

namespace Mori.RosSharp.Msg;

/// <summary>
/// A parsed .srv definition: the request and response message specs, split on the <c>---</c>
/// line. The request half of <c>pkg/Name</c> parses as <c>pkg/NameRequest</c> and the response
/// as <c>pkg/NameResponse</c>, matching the type names rosmsg uses for them.
/// </summary>
public sealed class SrvSpec
{
    public string FullType = "";
    public string Package = "";
    public string ShortName = "";
    public MsgSpec Request = null!;
    public MsgSpec Response = null!;

    public static SrvSpec Parse(string fullType, string text)
    {
        int slash = fullType.IndexOf('/');
        if (slash <= 0) throw new ArgumentException($"service type must be package/Name, got '{fullType}'");

        var request = new StringBuilder();
        var response = new StringBuilder();
        bool inResponse = false;
        foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
        {
            // The separator is a line starting with --- (field lines always start with a type).
            if (!inResponse && line.TrimStart().StartsWith("---", StringComparison.Ordinal))
            {
                inResponse = true;
                continue;
            }
            (inResponse ? response : request).Append(line).Append('\n');
        }
        if (!inResponse) throw new FormatException($"{fullType}: no '---' separator in .srv text");

        return new SrvSpec
        {
            FullType = fullType,
            Package = fullType.Substring(0, slash),
            ShortName = fullType.Substring(slash + 1),
            Request = MsgSpec.Parse(fullType + "Request", request.ToString()),
            Response = MsgSpec.Parse(fullType + "Response", response.ToString()),
        };
    }

    /// <summary>
    /// The service md5sum: MD5 of the request's canonical text concatenated with the response's
    /// (no separator) — the value <c>rossrv md5</c> prints, and what the ROSRPC handshake gates on.
    /// </summary>
    public string Md5(MsgCatalog catalog) =>
        MsgMd5.HashText(MsgMd5.CanonicalText(Request, catalog) + MsgMd5.CanonicalText(Response, catalog));
}
