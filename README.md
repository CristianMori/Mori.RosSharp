# Mori.RosSharp

A **ROS 1 node in pure managed C#** — the actual protocols, not a bridge:

- **XML-RPC graph APIs**: registers with the master (`registerPublisher` / `registerSubscriber`),
  serves the slave API (`publisherUpdate`, `requestTopic`) on its own endpoint.
- **TCPROS transport**: the connection-header handshake (topic, type, md5sum, callerid) and
  length-prefixed message framing, on raw sockets.
- **Zero dependencies**: BCL only (`System.Net.Sockets`, `System.Xml.Linq`). No native libraries,
  no rosbridge, no ROS installation on the .NET side. Cross-platform (`net8.0`).

A node subscribes, publishes, survives publisher churn (reconnects on `publisherUpdate`), and
unregisters cleanly on dispose. Message bodies are `byte[]` at the transport layer; hand-rolled
serializers for the common types ship in `RosMessages`.

## Quick start

```csharp
using Mori.RosSharp;

using var node = new RosNode("http://192.168.1.10:11311/", "/my_node");

// Subscribe: callback per message, or poll node.Latest(topic).
node.Subscribe("/cmd_vel", RosMessages.TwistType, RosMessages.TwistMd5, body =>
{
    double[] v = RosMessages.DecodeTwist(body);   // [linear xyz, angular xyz]
    Console.WriteLine($"vx={v[0]:F2} wz={v[5]:F2}");
});

// Publish.
RosPublisher pub = node.Advertise("/joint_states", RosMessages.JointStateType, RosMessages.JointStateMd5);
pub.Publish(RosMessages.EncodeJointState(seq: 0, stamp: 1.25, frameId: "",
    names: ["shoulder", "elbow"], positions: [0.1, -0.4], velocities: [0, 0], efforts: []));
```

`samples/ros-probe` is a round-trip smoke test against a live roscore:

```
dotnet run --project samples/ros-probe -- http://<master-host>:11311/ 30
```

then, on the ROS side: `rostopic echo -n 3 /gemelli_chatter` and
`rostopic pub -r 5 /cmd_vel geometry_msgs/Twist '{linear: {x: 0.25}}'`.

## Bundled message types

| Type | Encode | Decode |
|---|---|---|
| `geometry_msgs/Twist` | ✅ | ✅ |
| `sensor_msgs/JointState` | ✅ | |
| `nav_msgs/Odometry` | ✅ | |
| `rosgraph_msgs/Clock` | ✅ | |
| `tf2_msgs/TFMessage` | ✅ | |

The wire format is simple (little-endian primitives, uint32-prefixed strings and arrays, times as
two uint32) — adding a type is a dozen lines in the same style. The md5sums are the constants ROS
derives from the message text; they gate the TCPROS handshake, so a mismatch fails loudly at
connect time rather than silently corrupting data.

## Networking notes

ROS 1 carries the node's callback and data URIs *inside* the protocol, so every peer must be able
to reach the address you advertise. `RosNode` auto-detects it from the local route to the master;
pass `advertiseHost` explicitly when that guess is wrong.

Talking to a roscore inside WSL2 (NAT) from Windows works with two things in place:

- the WSL side sets `ROS_IP` to its own address (`ip -4 addr show eth0`), and
- Windows allows inbound TCP from the WSL subnet, e.g.
  `New-NetFirewallRule -DisplayName "ROS inbound (WSL)" -Direction Inbound -Action Allow -Protocol TCP -RemoteAddress 172.16.0.0/12`
  (elevated) — ROS uses ephemeral ports, so a port-scoped rule is not practical.

## License

Apache License 2.0 © 2026 Cristian Mori. See [LICENSE](LICENSE).
