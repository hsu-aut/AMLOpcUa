using System.Xml.Linq;
using OpcUaAml.Export;

namespace OpcUaAml.Tests;

/// <summary>
/// D20: CAEX 3.0 names each side of an InternalLink by the ID of its
/// interface; CAEX 2.15 wrote "element ID:interface name". The XSLT knows
/// only the second form and drops every link of the first.
/// </summary>
public class ExportInternalLinkTests
{
    private static readonly XNamespace Ua = NodeSetExporter.UaNodeSetNamespace;

    private const string Outlet = "aaaaaaaa-1111-1111-1111-111111111111";
    private const string Inlet = "bbbbbbbb-2222-2222-2222-222222222222";
    private const string ValveInlet = "cccccccc-3333-3333-3333-333333333333";

    private static readonly XDocument Document = XDocument.Parse($"""
        <CAEXFile SchemaVersion="3.0" FileName="Links.aml" xmlns="http://www.dke.de/CAEX">
          <SuperiorStandardVersion>AutomationML 2.10</SuperiorStandardVersion>
          <InstanceHierarchy Name="Plant">
            <InternalElement Name="Line" ID="00000000-0000-0000-0000-000000000001">
              <InternalElement Name="Pump" ID="11111111-1111-1111-1111-111111111111">
                <ExternalInterface Name="Outlet" ID="{Outlet}" />
              </InternalElement>
              <InternalElement Name="Tank" ID="22222222-2222-2222-2222-222222222222">
                <ExternalInterface Name="Inlet" ID="{Inlet}" />
                <InternalElement Name="Valve" ID="33333333-3333-3333-3333-333333333333">
                  <ExternalInterface Name="Inlet" ID="{ValveInlet}" />
                </InternalElement>
              </InternalElement>
              <InternalLink Name="Pipe" RefPartnerSideA="{Outlet}" RefPartnerSideB="{Inlet}" />
            </InternalElement>
          </InstanceHierarchy>
        </CAEXFile>
        """);

    private static XDocument Export(bool compat) =>
        NodeSetExporter.Export(Document, new NodeSetExportOptions { PublicationDate = new DateTime(2026, 9, 19, 0, 0, 0, DateTimeKind.Utc), XsltCompatibility = compat });

    private static string Ns(XDocument nodeSet)
    {
        var uris = nodeSet.Root!.Element(Ua + "NamespaceUris")!.Elements().Select(u => u.Value).ToList();
        return (uris.IndexOf("http://opcfoundation.org/UA/AML/Plant") + 1).ToString();
    }

    private static List<(string Target, bool Forward)> Links(XDocument nodeSet, string interfaceId) =>
        nodeSet.Root!.Elements().Single(e => (string?)e.Attribute("NodeId") == $"ns={Ns(nodeSet)};s={{{interfaceId}}}")
            .Element(Ua + "References")!.Elements()
            .Where(r => (string?)r.Attribute("ReferenceType") == "HasAMLInternalLink")
            .Select(r => (r.Value, (string?)r.Attribute("IsForward") != "false"))
            .ToList();

    [Fact]
    public void D20_a_link_between_interface_IDs_becomes_a_reference_between_exactly_those_interfaces()
    {
        var nodeSet = Export(compat: false);
        var ns = Ns(nodeSet);

        Assert.Equal([($"ns={ns};s={{{Inlet}}}", true)], Links(nodeSet, Outlet));
        Assert.Equal([($"ns={ns};s={{{Outlet}}}", false)], Links(nodeSet, Inlet));
        // The interface of the same name further down is another interface.
        Assert.Empty(Links(nodeSet, ValveInlet));
        Assert.Empty(ExportConformanceTests.DanglingReferences(nodeSet));
    }

    [Fact]
    public void D20_compatibility_mode_drops_the_link_as_the_XSLT_does()
    {
        var nodeSet = Export(compat: true);

        Assert.Empty(Links(nodeSet, Outlet));
        Assert.Empty(Links(nodeSet, Inlet));
    }
}
