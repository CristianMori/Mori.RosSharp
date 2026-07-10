using System.Buffers.Binary;
using System.Text;

namespace Mori.RosSharp;

/// <summary>
/// Serializers for the message types the blocks speak, hand-rolled against the ROS 1 wire format:
/// little-endian primitives, strings and variable arrays prefixed with a uint32 count, fixed-size
/// arrays (covariances) written raw, times as two uint32 (secs, nsecs). The md5sums are the
/// constants ROS computes from the message text — they gate the TCPROS handshake, so a typo here
/// shows up as "publisher refused" rather than silent garbage.
/// </summary>
public static class RosMessages
{
    // ---------------------------------------------------------------- geometry_msgs/Twist

    /// <summary>Message type name.</summary>
    public const string TwistType = "geometry_msgs/Twist";
    /// <summary>Canonical md5sum (gates the TCPROS handshake).</summary>
    public const string TwistMd5 = "9f195f881246fdfa2798d1d3eebca84a";

    /// <summary>[linear x y z, angular x y z] → wire body.</summary>
    public static byte[] EncodeTwist(ReadOnlySpan<double> v6)
    {
        var w = new Writer(48);
        for (int i = 0; i < 6; i++) w.F64(v6[i]);
        return w.Done();
    }

    /// <summary>Wire body → [linear x y z, angular x y z].</summary>
    public static double[] DecodeTwist(byte[] body)
    {
        var r = new Reader(body);
        var v = new double[6];
        for (int i = 0; i < 6; i++) v[i] = r.F64();
        return v;
    }

    // ---------------------------------------------------------------- rosgraph_msgs/Clock

    /// <summary>Message type name.</summary>
    public const string ClockType = "rosgraph_msgs/Clock";
    /// <summary>Canonical md5sum (gates the TCPROS handshake).</summary>
    public const string ClockMd5 = "a9c97c1d230cfc112e270351a944ee47";

    /// <summary>Sim time (seconds) → wire body.</summary>
    public static byte[] EncodeClock(double simTime)
    {
        var w = new Writer(8);
        w.Time(simTime);
        return w.Done();
    }

    // ---------------------------------------------------------------- sensor_msgs/JointState

    /// <summary>Message type name.</summary>
    public const string JointStateType = "sensor_msgs/JointState";
    /// <summary>Canonical md5sum (gates the TCPROS handshake).</summary>
    public const string JointStateMd5 = "3066dcd76a6cfaef579bd0f34173e9fd";

    /// <summary>Joint names + measured vectors → wire body (arrays may be empty to omit).</summary>
    public static byte[] EncodeJointState(
        uint seq, double stamp, string frameId,
        IReadOnlyList<string> names,
        ReadOnlySpan<double> positions, ReadOnlySpan<double> velocities, ReadOnlySpan<double> efforts)
    {
        var w = new Writer(64 + names.Count * 40);
        w.Header(seq, stamp, frameId);
        w.U32((uint)names.Count);
        foreach (string n in names) w.Str(n);
        w.F64Array(positions);
        w.F64Array(velocities);
        w.F64Array(efforts);
        return w.Done();
    }

    // ---------------------------------------------------------------- nav_msgs/Odometry

    /// <summary>Message type name.</summary>
    public const string OdometryType = "nav_msgs/Odometry";
    /// <summary>Canonical md5sum (gates the TCPROS handshake).</summary>
    public const string OdometryMd5 = "cd5e73d190d741a2f92e81eda573aca7";

    /// <summary>pose7 = [px py pz qx qy qz qw]; vel6 = [v ω]. Covariances are written as zeros.</summary>
    public static byte[] EncodeOdometry(
        uint seq, double stamp, string frameId, string childFrameId,
        ReadOnlySpan<double> pose7, ReadOnlySpan<double> vel6)
    {
        var w = new Writer(768);
        w.Header(seq, stamp, frameId);
        w.Str(childFrameId);
        for (int i = 0; i < 7; i++) w.F64(pose7[i]);   // Point + Quaternion
        for (int i = 0; i < 36; i++) w.F64(0);          // pose covariance (fixed-size, no prefix)
        for (int i = 0; i < 6; i++) w.F64(vel6[i]);     // Twist
        for (int i = 0; i < 36; i++) w.F64(0);          // twist covariance
        return w.Done();
    }

    // ---------------------------------------------------------------- tf2_msgs/TFMessage

    /// <summary>Message type name.</summary>
    public const string TfType = "tf2_msgs/TFMessage";
    /// <summary>Canonical md5sum (gates the TCPROS handshake).</summary>
    public const string TfMd5 = "94810edda583a504dfda3829e70d7eec";

    /// <summary>One transform: parent frame, child frame, and [px py pz qx qy qz qw].</summary>
    public readonly record struct TfEntry(string FrameId, string ChildFrameId, double[] Pose7);

    /// <summary>Transform list → wire body.</summary>
    public static byte[] EncodeTf(uint seq, double stamp, IReadOnlyList<TfEntry> transforms)
    {
        var w = new Writer(64 + transforms.Count * 128);
        w.U32((uint)transforms.Count);
        foreach (TfEntry t in transforms)
        {
            w.Header(seq, stamp, t.FrameId);
            w.Str(t.ChildFrameId);
            for (int i = 0; i < 7; i++) w.F64(t.Pose7[i]); // Vector3 + Quaternion
        }
        return w.Done();
    }

    // ---------------------------------------------------------------- primitives

    private ref struct Writer(int capacity)
    {
        private readonly MemoryStream _s = new(capacity);

        public void U32(uint v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, v);
            _s.Write(b);
        }

        public void F64(double v)
        {
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteDoubleLittleEndian(b, v);
            _s.Write(b);
        }

        public void Str(string v)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(v);
            U32((uint)utf8.Length);
            _s.Write(utf8);
        }

        public void Time(double seconds)
        {
            uint secs = (uint)seconds;
            U32(secs);
            U32((uint)Math.Clamp((seconds - secs) * 1e9, 0, 999_999_999));
        }

        public void Header(uint seq, double stamp, string frameId)
        {
            U32(seq);
            Time(stamp);
            Str(frameId);
        }

        public void F64Array(ReadOnlySpan<double> values)
        {
            U32((uint)values.Length);
            foreach (double v in values) F64(v);
        }

        public byte[] Done() => _s.ToArray();
    }

    private ref struct Reader(byte[] body)
    {
        private int _at;

        public double F64()
        {
            double v = BinaryPrimitives.ReadDoubleLittleEndian(body.AsSpan(_at));
            _at += 8;
            return v;
        }
    }
}
