# Mori.RosSharp

[![NuGet](https://img.shields.io/nuget/v/Mori.RosSharp)](https://www.nuget.org/packages/Mori.RosSharp)
[![Downloads](https://img.shields.io/nuget/dt/Mori.RosSharp)](https://www.nuget.org/packages/Mori.RosSharp)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue)](LICENSE)

A **ROS 1 node in pure managed C#** — the actual protocols, not a bridge. A .NET 8 or .NET 10
process registers with a roscore and publishes, subscribes, and serves or calls services over
TCPROS. `rostopic` and `rosservice` talk to it directly. No native libraries, no rosbridge, and
no ROS installation on the .NET side.

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

## Install

```
dotnet add package Mori.RosSharp
dotnet tool install -g Mori.RosSharp.MsgGen   # optional: typed messages generated from .msg files
```

[`Mori.RosSharp`](https://www.nuget.org/packages/Mori.RosSharp) is the node and the Roslyn
source generator (typed classes appear at build time from `.msg` files).
[`Mori.RosSharp.MsgGen`](https://www.nuget.org/packages/Mori.RosSharp.MsgGen) is the optional
`rosmsggen` CLI, packed as a dotnet tool, when you want those classes written to disk and
committed. The .NET 8 or .NET 10 SDK is the only local requirement; the ROS master can be
another machine.

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

Omit `masterUri` to use `ROS_MASTER_URI`, or `http://localhost:11311/` when that variable is
unset. The default graph name is `/rossharp`. Subscribe callbacks and service handlers run on
worker threads.

<!-- TODO: GIF of `rostopic echo` on Linux showing messages from a C# app on Windows (samples/ros-probe publishes geometry_msgs/Twist on /rossharp_chatter). -->

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

`samples/msg-demo` needs no master. It checks md5 anchors (including `std_srvs/SetBool` and
nested `nav_msgs/GetPlan`), checks that `RosMessageDefinition` encodes a Twist byte-for-byte
like `RosMessages.EncodeTwist`, and round-trips the source-generated `geometry_msgs/Twist` and
`demo_msgs/RobotStatus` classes:

```
dotnet run --project samples/msg-demo
```

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

The NuGet package imports `buildTransitive/Mori.RosSharp.props`, which already marks
`RosSharpNamespace` compiler-visible. Add that `CompilerVisibleProperty` item yourself when you
reference `Mori.RosSharp.Generators` as a project, the way `samples/msg-demo` does. A directory
named `msg` is skipped, so `my_msgs/msg/Foo.msg` and `my_msgs/Foo.msg` are both package
`my_msgs`. Generated classes expose `RosType`, `RosMd5`, `Serialize` / `Deserialize`, and
`ToBytes` / `FromBytes`.

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
fast-path serializers remain in `RosMessages`: `geometry_msgs/Twist` (encode and decode),
`sensor_msgs/JointState`, `nav_msgs/Odometry` (pose and twist covariances are written as zeros),
`rosgraph_msgs/Clock`, and `tf2_msgs/TFMessage` (encode only).

## Networking notes

ROS 1 carries the node's callback and data URIs *inside* the protocol, so every peer must be able
to reach the address you advertise. `RosNode` auto-detects it from the local route to the master;
pass `advertiseHost` explicitly when that guess is wrong.

Talking to a roscore inside WSL2 (NAT) from Windows works with two things in place:

- the WSL side sets `ROS_IP` to its own address (`ip -4 addr show eth0`), and
- Windows allows inbound TCP from the WSL subnet, e.g.
  `New-NetFirewallRule -DisplayName "ROS inbound (WSL)" -Direction Inbound -Action Allow -Protocol TCP -RemoteAddress 172.16.0.0/12`
  (elevated) — ROS uses ephemeral ports, so a port-scoped rule is not practical.

## Scope

What the current code implements, and what it leaves alone:

- **TCPROS only.** `requestTopic` rejects any other protocol with `only TCPROS is supported`.
- **No latched topics.** The publisher handshake sends `latching=0`. `Publish` writes to
  subscribers that are already connected; a late subscriber does not receive the previous body.
- **No parameter server.** `paramUpdate` is acknowledged and ignored. There is no get/set param API.
- **Slave diagnostics are empty.** `getBusStats` and `getBusInfo` return empty results. A master
  `shutdown` call is acknowledged and does not dispose the node; `Dispose` still unregisters.
- **`.srv` files are not generated.** Service md5sums come from `SrvSpec`. Bodies are
  `RosWriter` / `RosReader` bytes, as in the service samples above.
- **ROS 1 only.** ROS 2 is a separate library; see below.

## Related projects

Part of a family of open-source robotics / industrial-automation libraries by @CristianMori:

- **EtherNet/IP + CIP Safety:** [Python](https://github.com/CristianMori/EthernetIpPython) · [C#](https://github.com/CristianMori/EthernetIpSharp) · [Rust](https://github.com/CristianMori/EthernetIpRust) · [C++](https://github.com/CristianMori/EthernetIpCpp)
- **PLC tooling:** [MoriFixPlc](https://github.com/CristianMori/MoriFixPlc) (portable PLC HAL) · [EscaFlow](https://github.com/CristianMori/Mori.EscaFlow) (PLC transpiler)
- **ROS in pure C#:** [Mori.RosSharp](https://github.com/CristianMori/Mori.RosSharp) (ROS 1) · [Mori.Ros2Sharp](https://github.com/CristianMori/Mori.Ros2Sharp) (ROS 2)
- **Simulation & vision:** [GemelliStudio](https://github.com/CristianMori/GemelliStudio) · [LocateAnythingSharp](https://github.com/CristianMori/LocateAnythingSharp) · [Mori.SkyScope](https://github.com/CristianMori/Mori.SkyScope)
- **AI agents:** [Deckhand](https://github.com/CristianMori/DeckHand)

## License

Apache License 2.0 © 2026 Cristian Mori. See [LICENSE](LICENSE).
