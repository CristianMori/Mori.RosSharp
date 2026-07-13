namespace Mori.RosSharp.Msg;

/// <summary>The ROS builtin field types (everything else is a nested message).</summary>
public static class MsgBuiltins
{
    private static readonly HashSet<string> All = new HashSet<string>
    {
        "bool", "int8", "uint8", "int16", "uint16", "int32", "uint32", "int64", "uint64",
        "float32", "float64", "string", "time", "duration",
        "byte", "char", // legacy aliases (serialized as int8 / uint8)
    };

    public static bool Contains(string type) => All.Contains(type);
}

/// <summary>A constant declared in a .msg file (<c>int32 FOO=42</c>).</summary>
public sealed class MsgConstant
{
    public string Type = "";
    public string Name = "";
    /// <summary>The value exactly as written — md5 canonicalization uses the original text.</summary>
    public string ValueText = "";
}

/// <summary>One field of a .msg definition.</summary>
public sealed class MsgField
{
    /// <summary>The type exactly as written, brackets included (e.g. <c>float64[36]</c>, <c>Header</c>).</summary>
    public string RawType = "";

    /// <summary>Element type without brackets, package-resolved for nested messages
    /// (e.g. <c>float64</c>, <c>std_msgs/Header</c>, <c>geometry_msgs/Vector3</c>).</summary>
    public string BaseType = "";

    public string Name = "";
    public bool IsArray;
    /// <summary>-1 for variable-length arrays; the length for fixed-size ones.</summary>
    public int FixedLength = -1;

    public bool IsBuiltin => MsgBuiltins.Contains(BaseType);
}

/// <summary>
/// A parsed .msg definition. The grammar is line-based: comments start at <c>#</c>, a line with
/// <c>=</c> is a constant, anything else is <c>type name</c>. Unqualified nested types resolve to
/// the defining package, except the <c>Header</c> shorthand which is always std_msgs/Header.
/// </summary>
public sealed class MsgSpec
{
    public string FullType = "";
    public string Package = "";
    public string ShortName = "";
    public readonly List<MsgConstant> Constants = new List<MsgConstant>();
    public readonly List<MsgField> Fields = new List<MsgField>();

    public static MsgSpec Parse(string fullType, string text)
    {
        int slash = fullType.IndexOf('/');
        if (slash <= 0) throw new ArgumentException($"message type must be package/Name, got '{fullType}'");
        var spec = new MsgSpec
        {
            FullType = fullType,
            Package = fullType.Substring(0, slash),
            ShortName = fullType.Substring(slash + 1),
        };

        foreach (string rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            int hash = rawLine.IndexOf('#');
            string clean = (hash >= 0 ? rawLine.Substring(0, hash) : rawLine).Trim();
            if (clean.Length == 0) continue;

            int eq = clean.IndexOf('=');
            if (eq > 0)
            {
                // Constant. String constants keep everything after '=' from the ORIGINAL line
                // (a '#' inside a string constant is part of the value, not a comment).
                string[] lhs = SplitTokens(clean.Substring(0, eq));
                if (lhs.Length != 2) throw new FormatException($"{fullType}: bad constant line '{rawLine.Trim()}'");
                string value = lhs[0] == "string"
                    ? rawLine.Substring(rawLine.IndexOf('=') + 1).Trim()
                    : clean.Substring(eq + 1).Trim();
                spec.Constants.Add(new MsgConstant { Type = lhs[0], Name = lhs[1], ValueText = value });
                continue;
            }

            string[] tokens = SplitTokens(clean);
            if (tokens.Length != 2) throw new FormatException($"{fullType}: bad field line '{rawLine.Trim()}'");
            spec.Fields.Add(ParseField(tokens[0], tokens[1], spec.Package));
        }
        return spec;
    }

    private static MsgField ParseField(string rawType, string name, string package)
    {
        var f = new MsgField { RawType = rawType, Name = name };
        string baseType = rawType;
        int bracket = rawType.IndexOf('[');
        if (bracket >= 0)
        {
            f.IsArray = true;
            baseType = rawType.Substring(0, bracket);
            string inside = rawType.Substring(bracket + 1, rawType.Length - bracket - 2);
            f.FixedLength = inside.Length == 0 ? -1 : int.Parse(inside, System.Globalization.CultureInfo.InvariantCulture);
        }

        f.BaseType = MsgBuiltins.Contains(baseType) ? baseType
            : baseType == "Header" ? "std_msgs/Header"
            : baseType.IndexOf('/') >= 0 ? baseType
            : package + "/" + baseType;
        return f;
    }

    private static string[] SplitTokens(string s) =>
        s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>Supplies .msg text for a full type name; return null when unknown.</summary>
public delegate string? MsgTextResolver(string fullType);

/// <summary>
/// Resolves and caches message specs by full type name: a custom resolver first (files the caller
/// knows about), then the embedded common-message set. Also computes and caches md5sums.
/// </summary>
public sealed class MsgCatalog
{
    private readonly MsgTextResolver? _custom;
    private readonly Dictionary<string, MsgSpec> _specs = new Dictionary<string, MsgSpec>();
    private readonly Dictionary<string, string> _md5s = new Dictionary<string, string>();

    public MsgCatalog(MsgTextResolver? custom = null) => _custom = custom;

    public MsgSpec Get(string fullType)
    {
        if (_specs.TryGetValue(fullType, out MsgSpec? cached)) return cached;
        string? text = _custom?.Invoke(fullType) ?? EmbeddedMessages.Find(fullType);
        if (text is null)
            throw new KeyNotFoundException($"no .msg definition available for '{fullType}'");
        MsgSpec spec = MsgSpec.Parse(fullType, text);
        _specs[fullType] = spec;
        return spec;
    }

    public string Md5(string fullType)
    {
        if (_md5s.TryGetValue(fullType, out string? cached)) return cached;
        string md5 = MsgMd5.Compute(Get(fullType), this);
        _md5s[fullType] = md5;
        return md5;
    }
}
