namespace Mori.RosSharp.Msg;

/// <summary>
/// The common-message definitions bundled so the tools work without a ROS checkout: std_msgs
/// primitives, the geometry_msgs family, joint states, IMU, odometry, clock, and tf. Texts match
/// the ROS Noetic definitions field-for-field (comments are irrelevant to both md5 and wire
/// format, so they are omitted).
/// </summary>
public static class EmbeddedMessages
{
    public static string? Find(string fullType) => Definitions.TryGetValue(fullType, out string? t) ? t : null;

    public static IEnumerable<string> Types => Definitions.Keys;

    private static readonly Dictionary<string, string> Definitions = new Dictionary<string, string>
    {
        ["std_msgs/Header"] = "uint32 seq\ntime stamp\nstring frame_id\n",
        ["std_msgs/String"] = "string data\n",
        ["std_msgs/Bool"] = "bool data\n",
        ["std_msgs/Int32"] = "int32 data\n",
        ["std_msgs/Int64"] = "int64 data\n",
        ["std_msgs/Float32"] = "float32 data\n",
        ["std_msgs/Float64"] = "float64 data\n",
        ["std_msgs/Empty"] = "",
        ["std_msgs/ColorRGBA"] = "float32 r\nfloat32 g\nfloat32 b\nfloat32 a\n",

        ["geometry_msgs/Vector3"] = "float64 x\nfloat64 y\nfloat64 z\n",
        ["geometry_msgs/Point"] = "float64 x\nfloat64 y\nfloat64 z\n",
        ["geometry_msgs/Quaternion"] = "float64 x\nfloat64 y\nfloat64 z\nfloat64 w\n",
        ["geometry_msgs/Pose"] = "Point position\nQuaternion orientation\n",
        ["geometry_msgs/PoseStamped"] = "Header header\nPose pose\n",
        ["geometry_msgs/PoseWithCovariance"] = "Pose pose\nfloat64[36] covariance\n",
        ["geometry_msgs/Twist"] = "Vector3 linear\nVector3 angular\n",
        ["geometry_msgs/TwistStamped"] = "Header header\nTwist twist\n",
        ["geometry_msgs/TwistWithCovariance"] = "Twist twist\nfloat64[36] covariance\n",
        ["geometry_msgs/Transform"] = "Vector3 translation\nQuaternion rotation\n",
        ["geometry_msgs/TransformStamped"] = "Header header\nstring child_frame_id\nTransform transform\n",
        ["geometry_msgs/Wrench"] = "Vector3 force\nVector3 torque\n",

        ["sensor_msgs/JointState"] =
            "Header header\nstring[] name\nfloat64[] position\nfloat64[] velocity\nfloat64[] effort\n",
        ["sensor_msgs/Imu"] =
            "Header header\ngeometry_msgs/Quaternion orientation\nfloat64[9] orientation_covariance\n" +
            "geometry_msgs/Vector3 angular_velocity\nfloat64[9] angular_velocity_covariance\n" +
            "geometry_msgs/Vector3 linear_acceleration\nfloat64[9] linear_acceleration_covariance\n",

        ["nav_msgs/Odometry"] =
            "Header header\nstring child_frame_id\n" +
            "geometry_msgs/PoseWithCovariance pose\ngeometry_msgs/TwistWithCovariance twist\n",

        ["rosgraph_msgs/Clock"] = "time clock\n",

        ["tf2_msgs/TFMessage"] = "geometry_msgs/TransformStamped[] transforms\n",
    };
}
