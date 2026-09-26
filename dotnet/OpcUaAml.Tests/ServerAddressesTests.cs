using System.Xml.Linq;
using OpcUaAml.Server;

namespace OpcUaAml.Tests;

public class ServerAddressesTests
{
    // A mirrored server, a node below it, a DIN SPEC 16592 server element, a
    // value that names the host, and the NodeId type of an Annex A library,
    // which declares ServerInstanceUri without a value.
    private const string Document = """
        <CAEXFile xmlns="http://www.dke.de/CAEX" SchemaVersion="3.0" FileName="Plant.aml">
          <InstanceHierarchy Name="IH_Server">
            <InternalElement Name="MPS500 server" ID="s">
              <Attribute Name="ServerUri" AttributeDataType="xs:anyURI"><Value>urn:plc-hall3:UA:MPS500</Value></Attribute>
              <Attribute Name="EndpointUrl" AttributeDataType="xs:anyURI"><Value>opc.tcp://plc-hall3.factory.local:4840</Value></Attribute>
              <InternalElement Name="ST10" ID="n">
                <Attribute Name="NodeId">
                  <Attribute Name="ServerInstanceUri" AttributeDataType="xs:anyURI"><Value>urn:plc-hall3:UA:MPS500</Value></Attribute>
                  <Attribute Name="RootNodeId">
                    <Attribute Name="NamespaceUri"><Value>http://hsu-hh.de/UA/MPS500/</Value></Attribute>
                    <Attribute Name="NumericId"><Value>5001</Value></Attribute>
                  </Attribute>
                </Attribute>
                <Attribute Name="Description"><Value>Commissioned from PLC-HALL3</Value></Attribute>
              </InternalElement>
            </InternalElement>
            <InternalElement Name="OPC UA server" ID="d">
              <Attribute Name="EndpointURL"><Value>opc.tcp://plc-hall3.factory.local:4840</Value></Attribute>
              <Attribute Name="DiscoveryURL"><Value>opc.tcp://plc-hall3.factory.local:4840</Value></Attribute>
              <Attribute Name="SecurityPolicy"><Value>http://opcfoundation.org/UA/SecurityPolicy#Basic256Sha256</Value></Attribute>
            </InternalElement>
          </InstanceHierarchy>
          <AttributeTypeLib Name="ATL_OpcAmlMetaModel">
            <AttributeType Name="NodeId">
              <Attribute Name="ServerInstanceUri" AttributeDataType="xs:anyURI" />
            </AttributeType>
          </AttributeTypeLib>
        </CAEXFile>
        """;

    private static IEnumerable<string> Names(XContainer doc) =>
        doc.Descendants().Where(e => e.Name.LocalName == "Attribute").Select(e => (string)e.Attribute("Name")!);

    [Fact]
    public void Removes_the_addresses_of_servers_and_of_every_node()
    {
        var doc = XDocument.Parse(Document);

        var report = ServerAddresses.Strip(doc);

        var left = Names(doc.Root!.Elements().First()).ToList();
        Assert.DoesNotContain("ServerUri", left);
        Assert.DoesNotContain("EndpointUrl", left);
        Assert.DoesNotContain("ServerInstanceUri", left);
        Assert.DoesNotContain("EndpointURL", left);
        Assert.DoesNotContain("DiscoveryURL", left);
        Assert.Equal(5, report.Removed);
    }

    [Fact]
    public void Keeps_what_a_node_is_known_by_and_how_a_server_is_secured()
    {
        var doc = XDocument.Parse(Document);

        ServerAddresses.Strip(doc);

        var left = Names(doc).ToList();
        Assert.Contains("NamespaceUri", left);
        Assert.Contains("NumericId", left);
        Assert.Contains("SecurityPolicy", left);
    }

    [Fact]
    public void Leaves_the_libraries_alone()
    {
        var doc = XDocument.Parse(Document);

        ServerAddresses.Strip(doc);

        var library = doc.Root!.Elements().Single(e => e.Name.LocalName == "AttributeTypeLib");
        Assert.Contains("ServerInstanceUri", Names(library));
    }

    [Fact]
    public void Names_the_hosts_and_where_they_still_appear()
    {
        var doc = XDocument.Parse(Document);

        var report = ServerAddresses.Strip(doc);

        Assert.Equal(new[] { "plc-hall3", "plc-hall3.factory.local" }, report.Hosts);
        var remaining = Assert.Single(report.Remaining);
        Assert.Contains("ST10", remaining);
        Assert.Contains("Description", remaining);
    }
}
