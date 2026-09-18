using System.Xml.Linq;
using OpcUaAml.Compare;

namespace OpcUaAml.Tests;

public class NodeSetComparerTests
{
    private const string Header = """
        <UANodeSet xmlns="http://opcfoundation.org/UA/2011/03/UANodeSet.xsd" xmlns:uax="http://opcfoundation.org/UA/2008/02/Types.xsd">
          <NamespaceUris><Uri>urn:a</Uri></NamespaceUris>
          <Models><Model ModelUri="urn:a" Version="1.0" PublicationDate="2026-01-01T00:00:00Z" /></Models>
          <Aliases><Alias Alias="HasComponent">i=47</Alias><Alias Alias="String">i=12</Alias></Aliases>
        """;

    private static XDocument NodeSet(string nodes, string header = Header) => XDocument.Parse(header + nodes + "</UANodeSet>");

    private const string Nodes = """
        <UAObject NodeId="ns=1;s=A" BrowseName="1:A"><DisplayName>A</DisplayName>
          <References><Reference ReferenceType="HasComponent">ns=1;s=B</Reference></References></UAObject>
        <UAVariable NodeId="ns=1;s=B" BrowseName="1:B" ParentNodeId="ns=1;s=A" DataType="String"><DisplayName>B</DisplayName>
          <References><Reference ReferenceType="HasComponent" IsForward="false">ns=1;s=A</Reference></References>
          <Value><uax:String><![CDATA[<Info a="1" b="2"><X>y</X></Info>]]></uax:String></Value></UAVariable>
        """;

    [Fact]
    public void Order_comments_aliases_and_XML_formatting_do_not_count()
    {
        var reordered = """
            <!-- B first -->
            <UAVariable NodeId="ns=1;s=B" ParentNodeId="ns=1;s=A" BrowseName="1:B" DataType="i=12"><DisplayName>B</DisplayName>
              <References><Reference ReferenceType="i=47" IsForward="false">ns=1;s=A</Reference></References>
              <Value><uax:String>&lt;Info b="2" a="1"&gt;
                  &lt;X&gt;y&lt;/X&gt;
                &lt;/Info&gt;</uax:String></Value></UAVariable>
            <UAObject NodeId="ns=1;s=A" BrowseName="1:A"><DisplayName>A</DisplayName>
              <References><Reference ReferenceType="i=47" IsForward="true">ns=1;s=B</Reference></References></UAObject>
            """;
        var other = Header.Replace("2026-01-01", "2027-01-01");

        Assert.Empty(new NodeSetComparer().Compare(NodeSet(Nodes), NodeSet(reordered, other)));
        Assert.NotEmpty(new NodeSetComparer { ComparePublicationDates = true }.Compare(NodeSet(Nodes), NodeSet(reordered, other)));
    }

    [Fact]
    public void Changed_attributes_values_and_references_are_reported()
    {
        var changed = Nodes.Replace("<DisplayName>B</DisplayName>", "<DisplayName>C</DisplayName>")
            .Replace("<X>y</X>", "<X>z</X>")
            .Replace("""<Reference ReferenceType="HasComponent">ns=1;s=B</Reference>""", "");

        var diffs = new NodeSetComparer().Compare(NodeSet(Nodes), NodeSet(changed));

        Assert.Contains(diffs, d => d.Kind == DifferenceKind.Changed && d.Path.EndsWith("s=B/@DisplayName"));
        Assert.Contains(diffs, d => d.Kind == DifferenceKind.Changed && d.Path.EndsWith("s=B/@Value"));
        Assert.Contains(diffs, d => d.Kind == DifferenceKind.OnlyLeft && d.Path == "node:nsu=urn:a;s=A/ref:i=47>nsu=urn:a;s=B");
        Assert.Equal(3, diffs.Count);
    }

    [Fact]
    public void A_repeated_NodeId_is_a_difference()
    {
        var twice = Nodes + """<UAObject NodeId="ns=1;s=A" BrowseName="1:A"><DisplayName>A</DisplayName></UAObject>""";
        Assert.Contains(new NodeSetComparer().Compare(NodeSet(Nodes), NodeSet(twice)), d => d.Path == "node:nsu=urn:a;s=A#2");
    }
}
