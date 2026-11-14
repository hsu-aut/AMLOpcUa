using Aml.Engine.CAEX;
using Opc.Ua;
using OpcUaAml.Addressing;
using OpcUaAml.Server;

namespace OpcUaAml.Tests;

/// <summary>
/// Values read from a server, as they go into the document. A NodeId value was
/// written with the namespace index of the session ("ns=2;i=1143"), which no
/// other session needs to share; an array got the type of its elements
/// (xs:double for "1 2 3"), which its text is not.
/// </summary>
public class ValueWritingTests
{
    [Fact]
    public void A_NodeId_value_is_written_with_its_namespace_URI()
    {
        var table = new NamespaceTable();
        table.Append("urn:plant");

        var value = UaClient.Portable(new NodeId(1143u, 1), table);

        Assert.Equal("nsu=urn:plant;i=1143", ValueFormatting.ToAml(value));
        Assert.Equal("nsu=urn:plant;s=Motor nsu=http://opcfoundation.org/UA/;i=85",
            ValueFormatting.ToAml(UaClient.Portable(new[] { new NodeId("Motor", 1), ObjectIds.ObjectsFolder }, table)));
        // Other values pass as they are.
        Assert.Equal(1.5, UaClient.Portable(1.5, table));
    }

    [Fact]
    public void An_array_gets_no_type_of_its_elements()
    {
        var doc = CAEXDocument.New_CAEXDocument();
        var element = doc.CAEXFile.InstanceHierarchy.Append("Plant").InternalElement.Append("Positions");
        var address = new UaNodeAddress("urn:plant", UaIdType.String, "Positions");

        ValueSnapshot.SetValue(element, new UaReadResult(address, new[] { 1.0, 2.0, 3.0 }, "Double", true, "Good"));

        Assert.Equal("1 2 3", element.Attribute["Value"]!.Value);
        Assert.True(string.IsNullOrEmpty(element.Attribute["Value"]!.AttributeDataType), element.Attribute["Value"]!.AttributeDataType);

        // A scalar still gets its type.
        var single = doc.CAEXFile.InstanceHierarchy["Plant"]!.InternalElement.Append("Speed");
        ValueSnapshot.SetValue(single, new UaReadResult(address, 2.5, "Double", true, "Good"));
        Assert.Equal("xs:double", single.Attribute["Value"]!.AttributeDataType);
    }
}
