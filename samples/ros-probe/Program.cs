using System.Text;
using Mori.RosSharp;
using Mori.RosSharp.Msg;

// De-risk probe for the managed ROS 1 node: registers with a live roscore, publishes a Twist on
// /rossharp_chatter at 10 Hz, and subscribes to /cmd_vel, printing whatever arrives. Verify from the
// ROS side with:
//   rostopic echo -n 3 /rossharp_chatter
//   rostopic pub -r 2 /cmd_vel geometry_msgs/Twist '{linear: {x: 0.25}, angular: {z: 0.5}}'
//
// Service modes (std_srvs/SetBool; md5 computed at runtime from the .srv text):
//   ros-probe <masterUri> <seconds> serve    serve /set_bool — call it with:
//                                              rosservice call /set_bool "data: true"
//   ros-probe <masterUri> <seconds> call     call /set_bool three times (serve it with rospy)
//
// Usage: ros-probe <masterUri> [seconds] [serve|call]

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: ros-probe <masterUri> [seconds] [serve|call]");
    return 1;
}
int seconds = args.Length > 1 ? int.Parse(args[1]) : 30;
string mode = args.Length > 2 ? args[2].ToLowerInvariant() : "topics";

if (mode is "serve" or "call")
{
    // std_srvs/SetBool, verbatim.
    var setBool = SrvSpec.Parse("std_srvs/SetBool", "bool data\n---\nbool success\nstring message\n");
    string srvMd5 = setBool.Md5(new MsgCatalog());
    Console.WriteLine($"std_srvs/SetBool md5 {srvMd5}");

    using var svcNode = new RosNode(args[0], "/rossharp_probe");
    if (mode == "serve")
    {
        RosServiceServer server = svcNode.AdvertiseService("/set_bool", "std_srvs/SetBool", srvMd5, request =>
        {
            bool data = new RosReader(request).ReadBool();
            Console.WriteLine($"  request: data={data}");
            var w = new RosWriter();
            w.Write(true);
            w.Write($"C# saw {data}");
            return w.ToArray();
        });
        Console.WriteLine($"serving {server.ServiceName} for {seconds}s…");
        Thread.Sleep(seconds * 1000);
        Console.WriteLine($"done: {server.CallCount} call(s) served");
        return 0;
    }

    for (int i = 1; i <= 3; i++)
    {
        var w = new RosWriter();
        w.Write(i % 2 == 1);
        try
        {
            var r = new RosReader(svcNode.CallService("/set_bool", srvMd5, w.ToArray(), TimeSpan.FromSeconds(5)));
            Console.WriteLine($"  call {i} (data={i % 2 == 1}) -> success={r.ReadBool()}, message=\"{r.ReadString()}\"");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  call {i} failed: {ex.Message}");
        }
        Thread.Sleep(500);
    }
    return 0;
}

using var node = new RosNode(args[0], "/rossharp_probe");
Console.WriteLine($"node up, caller {node.CallerId}, master {args[0]}, slave {node.SlaveUri}");

node.Subscribe("/cmd_vel", RosMessages.TwistType, RosMessages.TwistMd5, body =>
{
    double[] v = RosMessages.DecodeTwist(body);
    Console.WriteLine($"  /cmd_vel: linear=({v[0]:F2}, {v[1]:F2}, {v[2]:F2}) angular=({v[3]:F2}, {v[4]:F2}, {v[5]:F2})");
});
Console.WriteLine("subscribed /cmd_vel");

RosPublisher pub = node.Advertise("/rossharp_chatter", RosMessages.TwistType, RosMessages.TwistMd5);
Console.WriteLine("advertised /rossharp_chatter");

for (int i = 0; i < seconds * 10; i++)
{
    pub.Publish(RosMessages.EncodeTwist([i * 0.1, 0, 0, 0, 0, 42]));
    if (i % 50 == 0) Console.WriteLine($"t={i / 10.0:F0}s  chatter subscribers: {pub.SubscriberCount}");
    Thread.Sleep(100);
}

Console.WriteLine("done");
return 0;
