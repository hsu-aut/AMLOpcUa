using System.Xml.Linq;
using OpcUaAml.Export;
using Xunit.Abstractions;

namespace OpcUaAml.Tests;

/// <summary>
/// What a NodeSet must be before anyone asks whether it says the right thing:
/// every node once, every reference once, every alias once, every namespace
/// index a real one. A file that breaks these does not load into the OPC
/// Foundation's stack at all, and an audit found that several did.
/// </summary>
public class ExportWellFormedTests(ITestOutputHelper output)
{
    private static readonly XNamespace Ua = XNamespace.Get(NodeSetExporter.UaNodeSetNamespace);

    public static TheoryData<string> Documents()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Fixtures.Path("aml-ua-xslt", "AML"), "*.aml"))
            data.Add(Path.GetFileNameWithoutExtension(file)!);
        // The document that gathers the deviations: the hardest case we have.
        data.Add("Deviations");
        return data;
    }

    private static XDocument Export(string name)
    {
        var path = name == "Deviations"
            ? Fixtures.Path("export", "Deviations.aml")
            : ExportConformanceTests.AmlPath(name);
        return NodeSetExporter.ExportFile(path, new NodeSetExportOptions
        {
            PublicationDate = new DateTime(2026, 9, 17, 0, 0, 0, DateTimeKind.Utc),
        });
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void Every_node_has_a_NodeId_of_its_own(string name)
    {
        var duplicates = Export(name).Root!.Elements()
            .Where(e => e.Name.LocalName.StartsWith("UA", StringComparison.Ordinal))
            .GroupBy(e => (string?)e.Attribute("NodeId") ?? "")
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} ({g.Count()}x: {string.Join(", ", g.Select(e => (string?)e.Attribute("BrowseName")))})")
            .ToList();

        foreach (var d in duplicates) output.WriteLine(d);
        Assert.Empty(duplicates);
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void No_node_carries_the_same_reference_twice(string name)
    {
        // Two references of one type, direction and target on one node: the
        // stack refuses the file with "key already exists".
        var duplicates = new List<string>();
        foreach (var node in Export(name).Root!.Elements().Where(e => e.Name.LocalName.StartsWith("UA", StringComparison.Ordinal)))
        {
            var seen = new HashSet<(string, string, string)>();
            foreach (var r in node.Descendants(Ua + "Reference"))
            {
                var key = ((string?)r.Attribute("ReferenceType") ?? "", (string?)r.Attribute("IsForward") ?? "true", r.Value.Trim());
                if (!seen.Add(key)) duplicates.Add($"{(string?)node.Attribute("NodeId")}: {key}");
            }
        }

        foreach (var d in duplicates) output.WriteLine(d);
        Assert.Empty(duplicates);
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void Every_alias_name_stands_for_one_node(string name)
    {
        var duplicates = Export(name).Root!.Element(Ua + "Aliases")?.Elements()
            .GroupBy(a => (string?)a.Attribute("Alias") ?? "")
            .Where(g => g.Select(a => a.Value).Distinct().Count() > 1)
            .Select(g => $"{g.Key} -> {string.Join(" / ", g.Select(a => a.Value))}")
            .ToList() ?? [];

        foreach (var d in duplicates) output.WriteLine(d);
        Assert.Empty(duplicates);
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void No_reference_names_a_namespace_that_does_not_exist(string name)
    {
        var nodeSet = Export(name);
        var count = nodeSet.Root!.Element(Ua + "NamespaceUris")?.Elements().Count() ?? 0;
        var bad = new List<string>();
        foreach (var node in nodeSet.Root.Elements().Where(e => e.Name.LocalName.StartsWith("UA", StringComparison.Ordinal)))
        {
            foreach (var target in node.Descendants(Ua + "Reference").Select(r => r.Value.Trim())
                         .Append((string?)node.Attribute("ParentNodeId") ?? "")
                         .Append((string?)node.Attribute("NodeId") ?? ""))
            {
                if (!target.StartsWith("ns=", StringComparison.Ordinal)) continue;
                var end = target.IndexOf(';');
                var index = end < 0 ? "" : target[3..end];
                if (index.Length == 0 || !int.TryParse(index, out var n) || n > count)
                    bad.Add($"{(string?)node.Attribute("NodeId")}: '{target}'");
            }
        }

        foreach (var b in bad.Take(20)) output.WriteLine(b);
        Assert.Empty(bad);
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void The_OPC_UA_stack_reads_it(string name)
    {
        // The last word on whether a NodeSet is a NodeSet: the OPC
        // Foundation's own reader, which refuses an empty namespace index or a
        // reference it has already seen.
        var nodeSet = Export(name);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(NodeSetExporter.ToXml(nodeSet)));
        var loaded = Opc.Ua.Export.UANodeSet.Read(stream);

        var context = new Opc.Ua.SystemContext(Opc.Ua.DefaultTelemetry.Create(_ => { }))
        {
            NamespaceUris = new Opc.Ua.NamespaceTable(),
            ServerUris = new Opc.Ua.StringTable(),
        };
        var states = new Opc.Ua.NodeStateCollection();
        loaded.Import(context, states);

        Assert.Equal(loaded.Items.Length, states.Count);
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void Every_model_is_declared_once(string name)
    {
        var duplicates = Export(name).Root!.Element(Ua + "Models")?.Elements(Ua + "Model")
            .GroupBy(m => (string?)m.Attribute("ModelUri") ?? "")
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} ({g.Count()}x)")
            .ToList() ?? [];

        foreach (var d in duplicates) output.WriteLine(d);
        Assert.Empty(duplicates);
    }
}
