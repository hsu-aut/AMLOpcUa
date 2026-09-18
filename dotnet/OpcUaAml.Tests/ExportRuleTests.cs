using System.Xml.Linq;
using OpcUaAml.Compare;
using OpcUaAml.Export;
using Xunit.Abstractions;

namespace OpcUaAml.Tests;

/// <summary>
/// The deviations from AML-UA-XSLT on a document made for them
/// (Fixtures/export/Deviations.aml). Deviations.xslt.xml is what the XSLT
/// (commit a144dcc, SaxonC-HE 13.0, post-processed like AML2UA.py) makes of
/// it; compatibility mode must reproduce it, the default must not.
/// </summary>
public class ExportRuleTests(ITestOutputHelper output)
{
    private static readonly XNamespace Ua = NodeSetExporter.UaNodeSetNamespace;
    private static readonly XNamespace Uax = NodeSetExporter.UaTypesNamespace;
    private static readonly string Input = Fixtures.Path("export", "Deviations.aml");

    private static XDocument Export(bool compat = false) =>
        NodeSetExporter.ExportFile(Input, new NodeSetExportOptions { PublicationDate = new DateTime(2026, 9, 18), XsltCompatibility = compat });

    private static XElement Node(XDocument nodeSet, string nodeId) =>
        nodeSet.Root!.Elements().Single(e => (string?)e.Attribute("NodeId") == nodeId);

    private static IEnumerable<(string Type, string Target, bool Forward)> References(XElement node) =>
        node.Element(Ua + "References")!.Elements().Select(r =>
            ((string)r.Attribute("ReferenceType")!, r.Value, (string?)r.Attribute("IsForward") != "false"));

    private static string Ns(XDocument nodeSet, string name)
    {
        var uris = nodeSet.Root!.Element(Ua + "NamespaceUris")!.Elements().Select(u => u.Value).ToList();
        return (uris.IndexOf("http://opcfoundation.org/UA/AML/" + name) + 1).ToString();
    }

    [Fact]
    public void Compatibility_mode_reproduces_the_XSLT_on_the_corner_cases()
    {
        var diffs = new NodeSetComparer().Compare(Export(compat: true), XDocument.Load(Fixtures.Path("export", "Deviations.xslt.xml")));
        foreach (var d in diffs) output.WriteLine(d.ToString());
        Assert.Empty(diffs);
    }

    [Fact]
    public void The_default_output_has_no_dangling_references_where_the_XSLT_has_some()
    {
        Assert.NotEmpty(ExportConformanceTests.DanglingReferences(Export(compat: true)));
        Assert.Empty(ExportConformanceTests.DanglingReferences(Export()));
    }

    [Fact]
    public void D4_nested_class_names_with_spaces_are_referenced_without_them()
    {
        var nodeSet = Export();
        var ns = Ns(nodeSet, "Interfaces");
        Assert.Contains(("HasSubtype", $"ns={ns};s=FluidPort", false), References(Node(nodeSet, $"ns={ns};s=HotPort")));
    }

    [Fact]
    public void D6_repeated_elements_get_their_own_NodeIds()
    {
        var nodeSet = Export();
        var file = References(Node(nodeSet, "ns=1;s=CAEXFile")).Select(r => r.Target).ToList();
        foreach (var id in new[] { "SourceDocumentInformation", "SourceDocumentInformation_1", "SuperiorStandardVersion", "SuperiorStandardVersion_1" })
        {
            Assert.Contains($"ns=1;s=CAEXFile_{id}", file);
            Node(nodeSet, $"ns=1;s=CAEXFile_{id}");
        }
        var ns = Ns(nodeSet, "Plant");
        Node(nodeSet, $"ns={ns};s=11111111-1111-1111-1111-111111111111_Speed_RefSemantic");
        Node(nodeSet, $"ns={ns};s=11111111-1111-1111-1111-111111111111_Speed_RefSemantic_1");
        Assert.Equal("RefSemantic", Node(nodeSet, $"ns={ns};s=11111111-1111-1111-1111-111111111111_Speed_RefSemantic").Element(Ua + "DisplayName")!.Value);
    }

    [Fact]
    public void D7_a_library_name_used_twice_gets_one_namespace()
    {
        var uris = Export().Root!.Element(Ua + "NamespaceUris")!.Elements().Select(u => u.Value).ToList();
        Assert.Single(uris, u => u == "http://opcfoundation.org/UA/AML/Units");
        Assert.Equal(uris.Count, uris.Distinct().Count());
    }

    [Fact]
    public void D8_an_InternalLink_to_a_missing_interface_gives_no_reference()
    {
        var nodeSet = Export();
        var outlet = Node(nodeSet, $"ns={Ns(nodeSet, "Plant")};s={{22222222-2222-2222-2222-222222222222}}");
        Assert.DoesNotContain(References(outlet), r => r.Type == "HasAMLInternalLink");
        var compat = Export(compat: true);
        Assert.Contains(References(Node(compat, $"ns={Ns(compat, "Plant")};s={{22222222-2222-2222-2222-222222222222}}")),
            r => r.Type == "HasAMLInternalLink" && r.Target == $"ns={Ns(compat, "Plant")};s=");
    }

    [Fact]
    public void D9_data_types_are_UA_data_types_or_left_out()
    {
        var nodeSet = Export();
        var ns = Ns(nodeSet, "Plant");
        string? DataType(string attribute) => (string?)Node(nodeSet, $"ns={ns};s=11111111-1111-1111-1111-111111111111_{attribute}").Attribute("DataType");

        Assert.Null(DataType("Built"));
        Assert.Equal("Decimal", DataType("Price"));
        Assert.Equal("NormalizedString", DataType("Label"));
        var aliases = nodeSet.Root!.Element(Ua + "Aliases")!.Elements().ToDictionary(a => (string)a.Attribute("Alias")!, a => a.Value);
        Assert.Equal("i=50", aliases["Decimal"]);
        Assert.Equal("i=12877", aliases["NormalizedString"]);
    }

    [Fact]
    public void D10_a_double_value_is_written_as_Double()
    {
        var nodeSet = Export();
        var speed = Node(nodeSet, $"ns={Ns(nodeSet, "Plant")};s=11111111-1111-1111-1111-111111111111_Speed");
        Assert.Equal("12.5", speed.Element(Ua + "Value")!.Element(Uax + "Double")!.Value);
    }

    [Fact]
    public void D11_an_UnknownType_constraint_is_typed_as_such()
    {
        var nodeSet = Export();
        var constraint = Node(nodeSet, $"ns={Ns(nodeSet, "Plant")};s=11111111-1111-1111-1111-111111111111_Mode_Constraint");
        Assert.Contains(("HasTypeDefinition", "CAEXUnknownConstraintType", true), References(constraint));
    }

    [Fact]
    public void D12_container_properties_and_copyrights_are_referenced()
    {
        var nodeSet = Export();
        var ns = Ns(nodeSet, "Plant");
        var hierarchy = References(Node(nodeSet, $"ns={ns};s=InstanceHierarchy_Plant")).ToList();
        Assert.Contains(("HasProperty", $"ns={ns};s=InstanceHierarchy_Plant_Version", true), hierarchy);
        Assert.Contains(("HasProperty", $"ns={ns};s=InstanceHierarchy_Plant_Copyright", true), hierarchy);
        Assert.Contains(("HasProperty", $"ns={ns};s=11111111-1111-1111-1111-111111111111_Copyright", true),
            References(Node(nodeSet, $"ns={ns};s={{11111111-1111-1111-1111-111111111111}}")));
    }

    [Fact]
    public void Section_comments_can_be_switched_off()
    {
        var nodeSet = NodeSetExporter.ExportFile(Input, new NodeSetExportOptions { PublicationDate = new DateTime(2026, 9, 18), Comments = false });
        Assert.Empty(nodeSet.DescendantNodes().OfType<XComment>());
        Assert.Empty(new NodeSetComparer().Compare(nodeSet, Export()));
    }
}
