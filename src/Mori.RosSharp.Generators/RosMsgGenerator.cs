using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Mori.RosSharp.Msg;

namespace Mori.RosSharp.Generators;

/// <summary>
/// Generates typed C# message classes from .msg files included as AdditionalFiles. The package
/// name comes from the file's directory ROS-style (<c>&lt;package&gt;/msg/Name.msg</c> or
/// <c>&lt;package&gt;/Name.msg</c>); nested types resolve against the other AdditionalFiles first,
/// then the embedded common-message set, and dependencies are emitted too so the code always
/// compiles. The root namespace defaults to <c>RosMessages</c> and can be overridden with the
/// <c>RosSharpNamespace</c> MSBuild property.
/// </summary>
[Generator]
public sealed class RosMsgGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor ParseError = new(
        id: "ROSMSG001",
        title: "Invalid .msg file",
        messageFormat: "{0}: {1}",
        category: "Mori.RosSharp",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValueProvider<ImmutableArray<(string FullType, string Text, string Path)>> msgFiles =
            context.AdditionalTextsProvider
                .Where(static f => f.Path.EndsWith(".msg", StringComparison.OrdinalIgnoreCase))
                .Select(static (f, ct) => (FullTypeOf(f.Path), f.GetText(ct)?.ToString() ?? "", f.Path))
                .Collect();

        IncrementalValueProvider<string> rootNamespace =
            context.AnalyzerConfigOptionsProvider.Select(static (options, _) =>
                options.GlobalOptions.TryGetValue("build_property.RosSharpNamespace", out string? ns)
                && !string.IsNullOrWhiteSpace(ns) ? ns! : "RosMessages");

        context.RegisterSourceOutput(msgFiles.Combine(rootNamespace), static (spc, input) =>
        {
            (ImmutableArray<(string FullType, string Text, string Path)> files, string ns) = input;
            if (files.Length == 0) return;

            var sources = new Dictionary<string, string>();
            foreach ((string fullType, string text, string _) in files)
                sources[fullType] = text;

            var catalog = new MsgCatalog(fullType =>
                sources.TryGetValue(fullType, out string? text) ? text : null);

            try
            {
                foreach (KeyValuePair<string, string> generated in
                         CSharpEmitter.EmitClosure(sources.Keys, catalog, ns, ""))
                    spc.AddSource(generated.Key.Replace('/', '.') + ".g.cs", generated.Value);
            }
            catch (Exception ex)
            {
                spc.ReportDiagnostic(Diagnostic.Create(ParseError, Location.None, files[0].Path, ex.Message));
            }
        });
    }

    // ROS layout: <package>/msg/<Name>.msg — the package is the directory above "msg".
    private static string FullTypeOf(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        string? dir = Path.GetDirectoryName(path);
        string package = "msgs";
        if (dir is not null)
        {
            string leaf = Path.GetFileName(dir);
            if (string.Equals(leaf, "msg", StringComparison.OrdinalIgnoreCase))
            {
                string? parent = Path.GetDirectoryName(dir);
                if (parent is not null) leaf = Path.GetFileName(parent);
            }
            if (!string.IsNullOrEmpty(leaf)) package = leaf;
        }
        return package + "/" + name;
    }
}
