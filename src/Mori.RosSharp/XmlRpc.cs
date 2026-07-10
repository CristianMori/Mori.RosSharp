using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace Mori.RosSharp;

/// <summary>
/// The slice of XML-RPC that ROS 1 actually uses: parameters are ints, booleans, doubles, strings,
/// and arrays thereof (modelled as <c>object</c> / <c>object[]</c>). Covers both sides — calling the
/// master/slave APIs and serving our own slave API — with no dependency beyond System.Xml.
/// </summary>
public static class XmlRpc
{
    // ---------------------------------------------------------------- encoding

    /// <summary>Builds a &lt;methodCall&gt; document for <paramref name="method"/>.</summary>
    public static string EncodeCall(string method, params object[] args)
    {
        var call = new XElement("methodCall",
            new XElement("methodName", method),
            new XElement("params", args.Select(a => new XElement("param", EncodeValue(a)))));
        return new XDocument(new XDeclaration("1.0", "utf-8", null), call).ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>Builds a &lt;methodResponse&gt; document around one return value.</summary>
    public static string EncodeResponse(object value)
    {
        var resp = new XElement("methodResponse",
            new XElement("params", new XElement("param", EncodeValue(value))));
        return new XDocument(new XDeclaration("1.0", "utf-8", null), resp).ToString(SaveOptions.DisableFormatting);
    }

    private static XElement EncodeValue(object? v) => new("value", v switch
    {
        null => new XElement("string", ""),
        int i => new XElement("i4", i),
        bool b => new XElement("boolean", b ? "1" : "0"),
        double d => new XElement("double", d.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
        string s => new XElement("string", s),
        IEnumerable<object> arr => new XElement("array", new XElement("data", arr.Select(EncodeValue))),
        _ => throw new NotSupportedException($"XML-RPC: unsupported value type {v.GetType().Name}"),
    });

    // ---------------------------------------------------------------- decoding

    /// <summary>Extracts the single return value from a &lt;methodResponse&gt; (throws on &lt;fault&gt;).</summary>
    public static object DecodeResponse(string xml)
    {
        XElement root = XDocument.Parse(xml).Root ?? throw new IOException("XML-RPC: empty response");
        if (root.Element("fault") is { } fault)
            throw new IOException("XML-RPC fault: " + fault.Value.Trim());
        XElement value = root.Element("params")?.Element("param")?.Element("value")
                         ?? throw new IOException("XML-RPC: response has no value");
        return DecodeValue(value);
    }

    /// <summary>Extracts (method, args) from a &lt;methodCall&gt; document.</summary>
    public static (string Method, object[] Args) DecodeCall(string xml)
    {
        XElement root = XDocument.Parse(xml).Root ?? throw new IOException("XML-RPC: empty call");
        string method = root.Element("methodName")?.Value ?? "";
        object[] args = root.Element("params")?.Elements("param")
            .Select(p => DecodeValue(p.Element("value") ?? throw new IOException("XML-RPC: param without value")))
            .ToArray() ?? [];
        return (method, args);
    }

    private static object DecodeValue(XElement value)
    {
        XElement? typed = value.Elements().FirstOrDefault();
        if (typed is null) return value.Value; // bare text inside <value> is a string
        return typed.Name.LocalName switch
        {
            "i4" or "int" => int.Parse(typed.Value, System.Globalization.CultureInfo.InvariantCulture),
            "boolean" => typed.Value.Trim() == "1",
            "double" => double.Parse(typed.Value, System.Globalization.CultureInfo.InvariantCulture),
            "string" => typed.Value,
            "array" => typed.Element("data")?.Elements("value").Select(DecodeValue).ToArray() ?? [],
            _ => typed.Value, // dateTime/base64 never appear in the ROS graph APIs
        };
    }

    // ---------------------------------------------------------------- client

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>Calls <paramref name="method"/> at an XML-RPC endpoint and returns its value.</summary>
    public static object Call(string uri, string method, params object[] args)
    {
        using var content = new StringContent(EncodeCall(method, args), Encoding.UTF8, "text/xml");
        using HttpResponseMessage resp = Http.PostAsync(uri, content).GetAwaiter().GetResult();
        resp.EnsureSuccessStatusCode();
        return DecodeResponse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
    }

    /// <summary>
    /// The ROS graph APIs wrap every result as <c>[statusCode, statusMessage, value]</c>; this calls
    /// and unwraps, throwing when the master reports failure (code ≤ 0).
    /// </summary>
    public static object CallRos(string uri, string method, params object[] args)
    {
        if (Call(uri, method, args) is not object[] { Length: 3 } triple)
            throw new IOException($"ROS API {method}: malformed response");
        if (triple[0] is not int code || code <= 0)
            throw new IOException($"ROS API {method} failed: {triple[1]}");
        return triple[2];
    }
}

/// <summary>
/// A minimal XML-RPC-over-HTTP server on a raw <see cref="TcpListener"/>. HttpListener would need a
/// URL ACL to bind a LAN-visible address on Windows; parsing the one POST shape XML-RPC uses does
/// not. One handler receives (method, args) and returns the response value.
/// </summary>
public sealed class XmlRpcServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<string, object[], object> _handler;
    private volatile bool _running = true;

    /// <summary>The port the OS assigned (bound on all interfaces).</summary>
    public int Port { get; }

    /// <summary>Starts serving immediately on an OS-assigned port; <paramref name="handler"/>
    /// receives (method, args) per call and returns the response value.</summary>
    public XmlRpcServer(Func<string, object[], object> handler)
    {
        _handler = handler;
        _listener = new TcpListener(IPAddress.Any, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        var accept = new Thread(AcceptLoop) { IsBackground = true, Name = "ros-xmlrpc" };
        accept.Start();
    }

    private void AcceptLoop()
    {
        while (_running)
        {
            TcpClient client;
            try { client = _listener.AcceptTcpClient(); }
            catch { return; } // listener closed
            ThreadPool.QueueUserWorkItem(_ => Serve(client));
        }
    }

    private void Serve(TcpClient client)
    {
        try
        {
            using (client)
            using (NetworkStream stream = client.GetStream())
            {
                client.ReceiveTimeout = 5000;
                string? body = ReadHttpRequest(stream);
                if (body is null) return;

                string responseXml;
                try
                {
                    (string method, object[] args) = XmlRpc.DecodeCall(body);
                    responseXml = XmlRpc.EncodeResponse(_handler(method, args));
                }
                catch (Exception ex)
                {
                    // ROS-style error triple rather than an XML-RPC fault — that is what rospy expects.
                    responseXml = XmlRpc.EncodeResponse(new object[] { -1, ex.Message, 0 });
                }

                byte[] payload = Encoding.UTF8.GetBytes(responseXml);
                string head = "HTTP/1.1 200 OK\r\nContent-Type: text/xml\r\n" +
                              $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
                stream.Write(Encoding.ASCII.GetBytes(head));
                stream.Write(payload);
            }
        }
        catch { /* a broken peer connection only affects that peer */ }
    }

    // Reads one HTTP request and returns its body (headers are only scanned for Content-Length).
    private static string? ReadHttpRequest(NetworkStream stream)
    {
        var buf = new MemoryStream();
        int contentLength = -1, headerEnd = -1;
        var chunk = new byte[4096];
        while (true)
        {
            if (headerEnd < 0)
            {
                int n = stream.Read(chunk, 0, chunk.Length);
                if (n <= 0) return null;
                buf.Write(chunk, 0, n);
                headerEnd = FindHeaderEnd(buf);
                if (headerEnd < 0) { if (buf.Length > 64 * 1024) return null; continue; }

                string headers = Encoding.ASCII.GetString(buf.GetBuffer(), 0, headerEnd);
                foreach (string line in headers.Split("\r\n"))
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        contentLength = int.Parse(line["Content-Length:".Length..].Trim(),
                            System.Globalization.CultureInfo.InvariantCulture);
                if (contentLength is < 0 or > 4 * 1024 * 1024) return null;
            }

            int bodyStart = headerEnd + 4;
            if (buf.Length >= bodyStart + contentLength)
                return Encoding.UTF8.GetString(buf.GetBuffer(), bodyStart, contentLength);

            int m = stream.Read(chunk, 0, chunk.Length);
            if (m <= 0) return null;
            buf.Write(chunk, 0, m);
        }
    }

    private static int FindHeaderEnd(MemoryStream buf)
    {
        byte[] b = buf.GetBuffer();
        for (int i = 3; i < buf.Length; i++)
            if (b[i - 3] == '\r' && b[i - 2] == '\n' && b[i - 1] == '\r' && b[i] == '\n')
                return i - 3;
        return -1;
    }

    /// <summary>Stops accepting calls and closes the listener.</summary>
    public void Dispose()
    {
        _running = false;
        try { _listener.Stop(); } catch { }
    }
}
