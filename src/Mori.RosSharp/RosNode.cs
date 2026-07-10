using System.Net;
using System.Net.Sockets;

namespace Mori.RosSharp;

/// <summary>
/// A ROS 1 node in pure managed code: registers with the master over XML-RPC, serves the slave API
/// (publisherUpdate / requestTopic) on its own XML-RPC endpoint, and moves messages over TCPROS.
///
/// Subscriptions connect out to every publisher the master reports (and reconcile on
/// publisherUpdate); publications accept TCPROS connections on one shared listener and fan each
/// published message out to the connected subscribers. Message bodies are opaque byte[] here —
/// typed serialization lives in <see cref="RosMessages"/> and the consumer.
/// </summary>
public sealed class RosNode : IDisposable
{
    private readonly string _masterUri;
    private readonly string _advertiseHost;
    private readonly XmlRpcServer _rpc;
    private readonly TcpListener _tcpros;
    private readonly int _tcprosPort;
    private readonly object _lock = new();
    private readonly Dictionary<string, Subscription> _subscriptions = new();
    private readonly Dictionary<string, Publication> _publications = new();
    private volatile bool _disposed;

    public string CallerId { get; }

    /// <summary>Our slave XML-RPC endpoint as advertised to the master (diagnostics).</summary>
    public string SlaveUri => $"http://{_advertiseHost}:{_rpc.Port}/";

    /// <param name="masterUri">The roscore, e.g. <c>http://172.24.23.112:11311/</c>; defaults to
    /// <c>ROS_MASTER_URI</c>.</param>
    /// <param name="callerId">Graph name for this node, e.g. <c>/my_node</c>.</param>
    /// <param name="advertiseHost">The IP peers can reach us at. ROS carries our callback and data
    /// URIs inside the protocol, so behind NAT-style setups (WSL ↔ host) this must be the address
    /// of the interface facing the master — auto-detected from the route to it when null.</param>
    public RosNode(string? masterUri = null, string callerId = "/rossharp", string? advertiseHost = null)
    {
        _masterUri = masterUri
            ?? Environment.GetEnvironmentVariable("ROS_MASTER_URI")
            ?? "http://localhost:11311/";
        CallerId = callerId;
        _advertiseHost = advertiseHost ?? DetectLocalAddress(_masterUri);

        _rpc = new XmlRpcServer(HandleSlaveCall);
        _tcpros = new TcpListener(IPAddress.Any, 0);
        _tcpros.Start();
        _tcprosPort = ((IPEndPoint)_tcpros.LocalEndpoint).Port;
        new Thread(AcceptTcpRos) { IsBackground = true, Name = "ros-tcpros-accept" }.Start();
    }

    // The local IPv4 the OS would use to reach the master — what we advertise in our URIs.
    private static string DetectLocalAddress(string masterUri)
    {
        try
        {
            var uri = new Uri(masterUri);
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(uri.Host, uri.Port); // no packets sent; just resolves the route
            return ((IPEndPoint)probe.LocalEndPoint!).Address.ToString();
        }
        catch { return "127.0.0.1"; }
    }

    // ---------------------------------------------------------------- subscribing

    /// <summary>Subscribes and invokes <paramref name="onMessage"/> (worker thread!) per message.</summary>
    public void Subscribe(string topic, string type, string md5, Action<byte[]> onMessage)
    {
        var sub = new Subscription(topic, type, md5, onMessage);
        lock (_lock) _subscriptions[topic] = sub;

        object result = XmlRpc.CallRos(_masterUri, "registerSubscriber", CallerId, topic, type, SlaveUri);
        if (result is object[] publishers)
            sub.ReconcilePublishers(publishers.OfType<string>().ToArray(), CallerId);
    }

    /// <summary>The latest message on a subscribed topic (null until one arrives).</summary>
    public byte[]? Latest(string topic)
    {
        lock (_lock) return _subscriptions.TryGetValue(topic, out Subscription? s) ? s.Latest : null;
    }

    private sealed class Subscription(string topic, string type, string md5, Action<byte[]> onMessage)
    {
        private readonly Dictionary<string, TcpClient> _byPublisherUri = new();
        private readonly object _subLock = new();
        public volatile byte[]? Latest;

        public void ReconcilePublishers(string[] publisherUris, string callerId)
        {
            lock (_subLock)
            {
                foreach (string gone in _byPublisherUri.Keys.Except(publisherUris).ToArray())
                {
                    try { _byPublisherUri[gone].Close(); } catch { }
                    _byPublisherUri.Remove(gone);
                }
                foreach (string uri in publisherUris.Except(_byPublisherUri.Keys).ToArray())
                {
                    var thread = new Thread(() => ReceiveFrom(uri, callerId)) { IsBackground = true, Name = $"ros-sub{topic}" };
                    thread.Start();
                }
            }
        }

        // requestTopic → TCPROS handshake → message pump. Any failure just drops this publisher;
        // the master sends a publisherUpdate if it comes back.
        private void ReceiveFrom(string publisherUri, string callerId)
        {
            try
            {
                if (XmlRpc.CallRos(publisherUri, "requestTopic", callerId, topic,
                        new object[] { new object[] { "TCPROS" } }) is not object[] { Length: >= 3 } proto
                    || proto[1] is not string host || proto[2] is not int port)
                    return;

                var client = new TcpClient();
                client.Connect(host, port);
                lock (_subLock)
                {
                    if (_byPublisherUri.ContainsKey(publisherUri)) { client.Close(); return; }
                    _byPublisherUri[publisherUri] = client;
                }

                NetworkStream stream = client.GetStream();
                stream.Write(TcpRos.EncodeHeader(new Dictionary<string, string>
                {
                    ["callerid"] = callerId, ["topic"] = topic,
                    ["md5sum"] = md5, ["type"] = type, ["tcp_nodelay"] = "1",
                }));
                Dictionary<string, string> reply = TcpRos.ReadHeader(stream);
                if (reply.TryGetValue("error", out string? err)) throw new IOException($"publisher refused: {err}");

                while (true)
                {
                    byte[] body = TcpRos.ReadMessage(stream);
                    Latest = body;
                    onMessage(body);
                }
            }
            catch
            {
                lock (_subLock)
                {
                    if (_byPublisherUri.TryGetValue(publisherUri, out TcpClient? c))
                    {
                        try { c.Close(); } catch { }
                        _byPublisherUri.Remove(publisherUri);
                    }
                }
            }
        }

        public void CloseAll()
        {
            lock (_subLock)
            {
                foreach (TcpClient c in _byPublisherUri.Values) { try { c.Close(); } catch { } }
                _byPublisherUri.Clear();
            }
        }
    }

    // ---------------------------------------------------------------- publishing

    /// <summary>Advertises a topic; returns the handle used to publish serialized messages.</summary>
    public RosPublisher Advertise(string topic, string type, string md5)
    {
        var pub = new Publication(topic, type, md5, CallerId);
        lock (_lock) _publications[topic] = pub;
        XmlRpc.CallRos(_masterUri, "registerPublisher", CallerId, topic, type, SlaveUri);
        return new RosPublisher(pub);
    }

    private void AcceptTcpRos()
    {
        while (!_disposed)
        {
            TcpClient client;
            try { client = _tcpros.AcceptTcpClient(); }
            catch { return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    client.NoDelay = true;
                    NetworkStream stream = client.GetStream();
                    Dictionary<string, string> header = TcpRos.ReadHeader(stream);
                    Publication? pub = null;
                    if (header.TryGetValue("topic", out string? topic))
                        lock (_lock) _publications.TryGetValue(topic, out pub);

                    if (pub is null)
                    {
                        stream.Write(TcpRos.EncodeHeader(new Dictionary<string, string>
                            { ["error"] = $"no publication for {header.GetValueOrDefault("topic", "?")}" }));
                        client.Close();
                        return;
                    }
                    pub.Attach(client, stream);
                }
                catch { try { client.Close(); } catch { } }
            });
        }
    }

    internal sealed class Publication(string topic, string type, string md5, string callerId)
    {
        private readonly List<(TcpClient Client, NetworkStream Stream)> _subscribers = [];
        private readonly object _pubLock = new();

        public void Attach(TcpClient client, NetworkStream stream)
        {
            stream.Write(TcpRos.EncodeHeader(new Dictionary<string, string>
            {
                ["callerid"] = callerId, ["topic"] = topic,
                ["md5sum"] = md5, ["type"] = type, ["latching"] = "0",
            }));
            lock (_pubLock) _subscribers.Add((client, stream));
        }

        public int SubscriberCount { get { lock (_pubLock) return _subscribers.Count; } }

        public void Publish(byte[] body)
        {
            lock (_pubLock)
                for (int i = _subscribers.Count - 1; i >= 0; i--)
                    try { TcpRos.WriteMessage(_subscribers[i].Stream, body); }
                    catch
                    {
                        try { _subscribers[i].Client.Close(); } catch { }
                        _subscribers.RemoveAt(i); // subscriber went away — not our problem
                    }
        }

        public void CloseAll()
        {
            lock (_pubLock)
            {
                foreach ((TcpClient c, _) in _subscribers) { try { c.Close(); } catch { } }
                _subscribers.Clear();
            }
        }
    }

    // ---------------------------------------------------------------- slave API

    private object HandleSlaveCall(string method, object[] args) => method switch
    {
        "getPid" => Ok(Environment.ProcessId),
        "getMasterUri" => Ok(_masterUri),
        "getSubscriptions" => Ok(SnapshotTopics(_subscriptions.Keys)),
        "getPublications" => Ok(SnapshotTopics(_publications.Keys)),
        "getBusStats" => Ok(new object[] { new object[0], new object[0], new object[0] }),
        "getBusInfo" => Ok(new object[0]),
        "paramUpdate" => Ok(0),
        "publisherUpdate" when args is [_, string topic, object[] uris] => PublisherUpdate(topic, uris),
        "requestTopic" when args is [_, string topic, object[] protocols] => RequestTopic(topic, protocols),
        "shutdown" => Ok(0), // the master asking us to go away does not tear down the twin
        _ => new object[] { -1, $"unknown method {method}", 0 },
    };

    private static object[] Ok(object value) => [1, "", value];

    private object[] SnapshotTopics(IEnumerable<string> topics)
    {
        lock (_lock) return topics.Select(object (t) => new object[] { t, "*" }).ToArray();
    }

    private object PublisherUpdate(string topic, object[] uris)
    {
        Subscription? sub;
        lock (_lock) _subscriptions.TryGetValue(topic, out sub);
        sub?.ReconcilePublishers(uris.OfType<string>().ToArray(), CallerId);
        return Ok(0);
    }

    private object RequestTopic(string topic, object[] protocols)
    {
        bool tcpros = protocols.OfType<object[]>().Any(p => p.FirstOrDefault() as string == "TCPROS");
        bool known;
        lock (_lock) known = _publications.ContainsKey(topic);
        return !known ? new object[] { -1, $"not publishing {topic}", 0 }
            : !tcpros ? new object[] { -1, "only TCPROS is supported", 0 }
            : Ok(new object[] { "TCPROS", _advertiseHost, _tcprosPort });
    }

    // ---------------------------------------------------------------- teardown

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock)
        {
            foreach ((string topic, Subscription sub) in _subscriptions)
            {
                try { XmlRpc.CallRos(_masterUri, "unregisterSubscriber", CallerId, topic, SlaveUri); } catch { }
                sub.CloseAll();
            }
            foreach ((string topic, Publication pub) in _publications)
            {
                try { XmlRpc.CallRos(_masterUri, "unregisterPublisher", CallerId, topic, SlaveUri); } catch { }
                pub.CloseAll();
            }
            _subscriptions.Clear();
            _publications.Clear();
        }
        try { _tcpros.Stop(); } catch { }
        _rpc.Dispose();
    }
}

/// <summary>Publish handle for one advertised topic.</summary>
public sealed class RosPublisher
{
    private readonly RosNode.Publication _pub;
    internal RosPublisher(RosNode.Publication pub) => _pub = pub;

    /// <summary>How many TCPROS subscribers are currently connected.</summary>
    public int SubscriberCount => _pub.SubscriberCount;

    /// <summary>Sends one serialized message body to every connected subscriber.</summary>
    public void Publish(byte[] serializedBody) => _pub.Publish(serializedBody);
}
