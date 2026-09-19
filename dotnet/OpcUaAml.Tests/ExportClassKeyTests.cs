using System.Xml.Linq;
using OpcUaAml.Export;

namespace OpcUaAml.Tests;

/// <summary>D17: classes of one name in one library get NodeIds of their own.</summary>
public class ExportClassKeyTests
{
    private static readonly XNamespace Ua = NodeSetExporter.UaNodeSetNamespace;

    private static readonly XDocument Document = XDocument.Parse("""
        <CAEXFile SchemaVersion="3.0" FileName="Twins.aml" xmlns="http://www.dke.de/CAEX">
          <SuperiorStandardVersion>AutomationML 2.10</SuperiorStandardVersion>
          <InstanceHierarchy Name="Plant">
            <InternalElement Name="One" ID="11111111-1111-1111-1111-111111111111" RefBaseSystemUnitPath="Lib/A/X" />
            <InternalElement Name="Two" ID="22222222-2222-2222-2222-222222222222" RefBaseSystemUnitPath="Lib/B/X" />
          </InstanceHierarchy>
          <SystemUnitClassLib Name="Lib">
            <SystemUnitClass Name="A" ID="aaaaaaaa-0000-0000-0000-000000000001">
              <SystemUnitClass Name="X" ID="aaaaaaaa-0000-0000-0000-000000000002">
                <Attribute Name="Size" AttributeDataType="xs:double"><Value>1</Value></Attribute>
              </SystemUnitClass>
            </SystemUnitClass>
            <SystemUnitClass Name="B" ID="bbbbbbbb-0000-0000-0000-000000000001">
              <SystemUnitClass Name="X" ID="bbbbbbbb-0000-0000-0000-000000000002">
                <Attribute Name="Size" AttributeDataType="xs:double"><Value>2</Value></Attribute>
              </SystemUnitClass>
            </SystemUnitClass>
          </SystemUnitClassLib>
        </CAEXFile>
        """);

    private static XDocument Export(bool compat) =>
        NodeSetExporter.Export(Document, new NodeSetExportOptions { PublicationDate = new DateTime(2026, 9, 19), XsltCompatibility = compat });

    private static List<string> NodeIds(XDocument nodeSet) =>
        nodeSet.Root!.Elements().Where(e => e.Name.LocalName.StartsWith("UA", StringComparison.Ordinal)).Select(e => (string)e.Attribute("NodeId")!).ToList();

    private static XElement Instance(XDocument nodeSet, string name) =>
        nodeSet.Root!.Elements(Ua + "UAObject").Single(e => ((string)e.Attribute("BrowseName")!).EndsWith(":" + name, StringComparison.Ordinal));

    [Fact]
    public void Twins_get_their_path_and_instances_their_own_type()
    {
        var nodeSet = Export(compat: false);
        var ids = NodeIds(nodeSet);

        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Contains(ids, id => id.EndsWith(";s=A/X", StringComparison.Ordinal));
        Assert.Contains(ids, id => id.EndsWith(";s=B/X", StringComparison.Ordinal));
        Assert.Contains(ids, id => id.EndsWith(";s=A/X_Size", StringComparison.Ordinal));
        Assert.Contains(ids, id => id.EndsWith(";s=B/X_Size", StringComparison.Ordinal));
        // A class without a twin keeps its name.
        Assert.Contains(ids, id => id.EndsWith(";s=A", StringComparison.Ordinal));

        string TypeOf(string name) => Instance(nodeSet, name).Element(Ua + "References")!.Elements()
            .Single(r => (string?)r.Attribute("ReferenceType") == "HasTypeDefinition").Value;
        Assert.EndsWith(";s=A/X", TypeOf("One"));
        Assert.EndsWith(";s=B/X", TypeOf("Two"));
        Assert.Empty(ExportConformanceTests.DanglingReferences(nodeSet));
    }

    [Fact]
    public void The_XSLT_compatible_mode_keeps_the_shared_NodeId()
    {
        var ids = NodeIds(Export(compat: true));
        Assert.True(ids.Count(id => id.EndsWith(";s=X", StringComparison.Ordinal)) >= 2);
    }
}
