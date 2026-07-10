using Mori.RosSharp;

// De-risk probe for the managed ROS 1 node: registers with a live roscore, publishes a Twist on
// /gemelli_chatter at 10 Hz, and subscribes to /cmd_vel, printing whatever arrives. Verify from the
// ROS side with:
//   rostopic echo -n 3 /gemelli_chatter
//   rostopic pub -r 2 /cmd_vel geometry_msgs/Twist '{linear: {x: 0.25}, angular: {z: 0.5}}'
//
// Usage: ros-probe <masterUri> [seconds]

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: ros-probe <masterUri> [seconds]");
    return 1;
}
int seconds = args.Length > 1 ? int.Parse(args[1]) : 30;

using var node = new RosNode(args[0], "/gemelli_probe");
Console.WriteLine($"node up, caller {node.CallerId}, master {args[0]}, slave {node.SlaveUri}");

node.Subscribe("/cmd_vel", RosMessages.TwistType, RosMessages.TwistMd5, body =>
{
    double[] v = RosMessages.DecodeTwist(body);
    Console.WriteLine($"  /cmd_vel: linear=({v[0]:F2}, {v[1]:F2}, {v[2]:F2}) angular=({v[3]:F2}, {v[4]:F2}, {v[5]:F2})");
});
Console.WriteLine("subscribed /cmd_vel");

RosPublisher pub = node.Advertise("/gemelli_chatter", RosMessages.TwistType, RosMessages.TwistMd5);
Console.WriteLine("advertised /gemelli_chatter");

for (int i = 0; i < seconds * 10; i++)
{
    pub.Publish(RosMessages.EncodeTwist([i * 0.1, 0, 0, 0, 0, 42]));
    if (i % 50 == 0) Console.WriteLine($"t={i / 10.0:F0}s  chatter subscribers: {pub.SubscriberCount}");
    Thread.Sleep(100);
}

Console.WriteLine("done");
return 0;
