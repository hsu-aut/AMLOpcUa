using Aml.Engine.CAEX;
using OpcUaAml.Addressing;
using OpcUaAml.Export;
using OpcUaAml.Roundtrip;

namespace OpcUaAml.Tests;

/// <summary>D16: attributes bound to a server node (BPR 007 DataVariable) become AMLOpcUaConnectionType variables.</summary>
public class ExportConnectionTests
{
    private static (CAEXDocument Doc, InternalElementType Server, InternalElementType Pump) Plant(string? endpoint)
    {
        var doc = CAEXDocument.New_CAEXDocument();
        var ih = doc.CAEXFile.InstanceHierarchy.Append("Plant");
        var server = ih.InternalElement.Append("PlcServer");
        if (endpoint != null) server.Attribute.Append("EndpointURL").Value = endpoint;
        var pump = ih.InternalElement.Append("Pump");
        BprDataVariable.Write(pump, "Speed", new UaNodeAddress("http://example.org/Plant/", UaIdType.Numeric, "123"), server);
        return (doc, server, pump);
    }

    private static UaGraphNode Speed(UaGraph g) => g.Nodes.Values.Single(n => n.NodeClass == "UAVariable" && n.BrowseName == "Speed");

    private static string? Component(UaGraph g, UaGraphNode owner, string name) =>
        g.Children(owner, "HasComponent").SingleOrDefault(c => c.BrowseName == name)?.Value;

    [Fact]
    public void A_DataVariable_is_typed_as_connection_with_node_and_server()
    {
        var (doc, _, _) = Plant("opc.tcp://plc:4840");
        var g = UaGraph.Load(NodeSetExporter.Export(doc));
        var speed = Speed(g);

        Assert.Equal("http://opcfoundation.org/UA/AML/|i=3002", speed.References.Single(r => r.Type == "HasTypeDefinition").Target);
        Assert.Equal("nsu=http://example.org/Plant/;i=123", Component(g, speed, "VariableNodeId"));
        Assert.Equal("opc.tcp://plc:4840", Component(g, speed, "ServerAddress"));
        Assert.Equal("PlcServer", Component(g, speed, "ServerAlias"));
        // The BPR sub-attributes stay as they were.
        Assert.Equal("ns=1;i=123", Component(g, speed, "NodeId"));
    }

    [Fact]
    public void Without_a_server_address_the_attribute_stays_a_plain_AML_variable()
    {
        var (doc, _, _) = Plant(endpoint: null);
        var speed = Speed(UaGraph.Load(NodeSetExporter.Export(doc)));

        Assert.Equal("http://opcfoundation.org/UA/AML/|i=3001", speed.References.Single(r => r.Type == "HasTypeDefinition").Target);
    }

    [Fact]
    public void The_XSLT_compatible_export_has_no_connection()
    {
        var (doc, _, _) = Plant("opc.tcp://plc:4840");
        var g = UaGraph.Load(NodeSetExporter.Export(doc, new NodeSetExportOptions { XsltCompatibility = true }));

        Assert.Null(Component(g, Speed(g), "VariableNodeId"));
    }
}
