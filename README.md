# Mori.RosSharp

A **ROS 1 node in pure managed C#** — the actual protocols, not a bridge:

- **XML-RPC graph APIs**: registers with the master (`registerPublisher` / `registerSubscriber` /
  `registerService`), serves the slave API (`publisherUpdate`, `requestTopic`) on its own endpoint.
- **TCPROS transport**: the connection-header handshake (topic, type, md5sum, callerid) and
  length-prefixed message framing, on raw sockets.
- **Services over ROSRPC**: serve services that `rosservice call` can invoke, and call services
  served by other nodes — request/response on the same shared listener.
- **Zero dependencies**: BCL only (`System.Net.Sockets`, `System.Xml.Linq`). No native libraries,
  no rosbridge, no ROS installation on the .NET side. Cross-platform (`net8.0` / `net10.0`).

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

## Services

Serve one (the handler receives the serialized request and returns the serialized response —
throwing reports a clean call failure to the client):

```csharp
node.AdvertiseService("/set_bool", "std_srvs/SetBool", srvMd5, request =>
{
    bool data = new RosReader(request).ReadBool();
    var w = new RosWriter();
    w.Write(true);                 // success
    w.Write($"received {data}");   // message
    return w.ToArray();
});
```

Call one:

```csharp
var w = new RosWriter();
w.Write(true);
var r = new RosReader(node.CallService("/set_bool", srvMd5, w.ToArray(), TimeSpan.FromSeconds(5)));
bool success = r.ReadBool();
string message = r.ReadString();
```

Service md5sums combine the request and response definitions; compute them from the `.srv` text
at runtime:

```csharp
using Mori.RosSharp.Msg;
string srvMd5 = SrvSpec.Parse("std_srvs/SetBool", "bool data\n---\nbool success\nstring message\n")
    .Md5(new MsgCatalog());
```

`rosservice list`, `rosservice info`, and `rosservice call` all see a served service exactly as
they would one from roscpp/rospy.

## The probe

`samples/ros-probe` exercises everything against a live roscore:

```
dotnet run --project samples/ros-probe -- http://<master-host>:11311/ 30        # pub/sub round trip
dotnet run --project samples/ros-probe -- http://<master-host>:11311/ 30 serve  # serve /set_bool
dotnet run --project samples/ros-probe -- http://<master-host>:11311/ 30 call   # call /set_bool
```

then, on the ROS side: `rostopic echo -n 3 /rossharp_chatter`,
`rostopic pub -r 5 /cmd_vel geometry_msgs/Twist '{linear: {x: 0.25}}'`, or
`rosservice call /set_bool "data: true"`.

## Messages, three ways

Message definitions are `.msg` files; the md5sum that gates every TCPROS handshake is derived from
the definition text. The `Mori.RosSharp.Msg` engine parses `.msg` files and computes those md5sums
exactly as ROS does (validated against `rosmsg md5` for all 106 messages in the Noetic
geometry/std/sensor/nav/rosgraph/actionlib packages), and feeds three consumption styles:

**1. Source generator (recommended).** It ships inside the `Mori.RosSharp` package — drop `.msg`
files into the project and typed classes appear at build time:

```xml
<PropertyGroup>
  <RosSharpNamespace>MyApp.Ros</RosSharpNamespace>  <!-- optional, default RosMessages -->
</PropertyGroup>
<ItemGroup>
  <CompilerVisibleProperty Include="RosSharpNamespace" />
  <AdditionalFiles Include="my_msgs\*.msg" />       <!-- package name = directory name -->
</ItemGroup>
```

```csharp
var twist = MyApp.Ros.geometry_msgs.Twist.FromBytes(body);
pub.Publish(new MyApp.Ros.my_msgs.RobotStatus { Mode = "walking" }.ToBytes());
```

**2. CLI generator.** The `Mori.RosSharp.MsgGen` dotnet tool emits the same classes as files you
can read and commit — point it at `.msg` files or ROS-style package directories:

```
rosmsggen -o Generated -n MyApp.Ros path\to\my_msgs \\wsl$\Ubuntu\opt\ros\noetic\share\sensor_msgs\msg
```

**3. Runtime, no codegen.** `RosMessageDefinition` serializes any type from its definition at
runtime — dictionaries in, dictionaries out — for generic tooling:

```csharp
var def = new RosMessageDefinition("geometry_msgs/Twist");
node.Subscribe("/cmd_vel", def.Type, def.Md5, body =>
{
    var v = def.Decode(body);
    var linear = (Dictionary<string, object?>)v["linear"]!;
});
```

Nested types resolve against your files first, then an embedded set of the common messages
(std_msgs, geometry_msgs, JointState, Imu, Odometry, Clock, TFMessage), and generation closes over
dependencies — referencing `geometry_msgs/Twist` from a custom message just works. Hand-written
fast-path serializers for the most common types remain in `RosMessages`.

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
