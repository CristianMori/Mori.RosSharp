using Mori.RosSharp;

// Verifies the .msg toolchain three ways:
//  1. md5 anchors — the computed md5sums must match the canonical ROS values (which also match
//     the hand-written constants in RosMessages).
//  2. Dynamic serialization — RosMessageDefinition encodes a Twist byte-identically to the
//     hand-rolled RosMessages.EncodeTwist.
//  3. Generated code — the source-generated classes (from the .msg AdditionalFiles) round-trip,
//     including a custom message with constants, arrays, and a nested common type.

int failures = 0;

// 1 — md5 anchors (rosmsg md5 ground truth).
Check("md5 geometry_msgs/Twist", new RosMessageDefinition("geometry_msgs/Twist").Md5, RosMessages.TwistMd5);
Check("md5 sensor_msgs/JointState", new RosMessageDefinition("sensor_msgs/JointState").Md5, RosMessages.JointStateMd5);
Check("md5 nav_msgs/Odometry", new RosMessageDefinition("nav_msgs/Odometry").Md5, RosMessages.OdometryMd5);
Check("md5 rosgraph_msgs/Clock", new RosMessageDefinition("rosgraph_msgs/Clock").Md5, RosMessages.ClockMd5);
Check("md5 tf2_msgs/TFMessage", new RosMessageDefinition("tf2_msgs/TFMessage").Md5, RosMessages.TfMd5);
Check("md5 std_msgs/String", new RosMessageDefinition("std_msgs/String").Md5, "992ce8a1687cec8c8bd883ec73ca41d1");
Check("md5 std_msgs/Empty", new RosMessageDefinition("std_msgs/Empty").Md5, "d41d8cd98f00b204e9800998ecf8427e");

// Service md5s: request + response canonical texts concatenated (rossrv md5 ground truth).
// GetPlan nests geometry_msgs/PoseStamped and nav_msgs/Path, so it also proves nested-type
// md5 substitution inside service halves.
var srvCatalog = new Mori.RosSharp.Msg.MsgCatalog(fullType =>
    fullType == "nav_msgs/Path" ? "Header header\ngeometry_msgs/PoseStamped[] poses\n" : null);
Check("srv md5 std_srvs/SetBool",
    Mori.RosSharp.Msg.SrvSpec.Parse("std_srvs/SetBool", "bool data\n---\nbool success\nstring message\n").Md5(srvCatalog),
    "09fb03525b03e7ea1fd3992bafd87e16");
Check("srv md5 nav_msgs/GetPlan",
    Mori.RosSharp.Msg.SrvSpec.Parse("nav_msgs/GetPlan",
        "geometry_msgs/PoseStamped start\ngeometry_msgs/PoseStamped goal\nfloat32 tolerance\n---\nnav_msgs/Path plan\n").Md5(srvCatalog),
    "421c8ea4d21c6c9db7054b4bbdf1e024");

// 2 — dynamic encode vs hand-rolled bytes.
var twistDef = new RosMessageDefinition("geometry_msgs/Twist");
byte[] dynamicBytes = twistDef.Encode(new Dictionary<string, object?>
{
    ["linear"] = new Dictionary<string, object?> { ["x"] = 0.25, ["y"] = -1.5, ["z"] = 3.0 },
    ["angular"] = new Dictionary<string, object?> { ["x"] = 0.0, ["y"] = 0.0, ["z"] = 42.0 },
});
byte[] handBytes = RosMessages.EncodeTwist([0.25, -1.5, 3.0, 0, 0, 42]);
Check("dynamic Twist bytes", Convert.ToHexString(dynamicBytes), Convert.ToHexString(handBytes));

Dictionary<string, object?> decoded = twistDef.Decode(dynamicBytes);
Check("dynamic Twist decode", ((Dictionary<string, object?>)decoded["angular"]!)["z"]!.ToString()!, "42");

// 3 — generated classes.
Check("generated Twist md5", MsgDemo.Ros.geometry_msgs.Twist.RosMd5, RosMessages.TwistMd5);
var genTwist = new MsgDemo.Ros.geometry_msgs.Twist
{
    Linear = { X = 0.25, Y = -1.5, Z = 3.0 },
    Angular = { Z = 42.0 },
};
Check("generated Twist bytes", Convert.ToHexString(genTwist.ToBytes()), Convert.ToHexString(handBytes));

var status = new MsgDemo.Ros.demo_msgs.RobotStatus
{
    Header = { Seq = 7, Stamp = RosTime.FromSeconds(12.5), FrameId = "base" },
    Mode = "walking",
    Temperatures = [36.5, 37.1, 41.0],
    Velocity = { Linear = { X = 0.12 }, Angular = { Z = -0.4 } },
};
var back = MsgDemo.Ros.demo_msgs.RobotStatus.FromBytes(status.ToBytes());
Check("RobotStatus mode", back.Mode, "walking");
Check("RobotStatus temps", string.Join(",", back.Temperatures), "36.5,37.1,41");
Check("RobotStatus vx", back.Velocity.Linear.X.ToString(), "0.12");
Check("RobotStatus stamp", back.Header.Stamp.ToSeconds().ToString(), "12.5");
Check("RobotStatus MODE_ACTIVE", MsgDemo.Ros.demo_msgs.RobotStatus.MODE_ACTIVE.ToString(), "1");

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

void Check(string what, string actual, string expected)
{
    bool ok = actual == expected;
    if (!ok) failures++;
    Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {what}" + (ok ? "" : $"  expected {expected}, got {actual}"));
}
