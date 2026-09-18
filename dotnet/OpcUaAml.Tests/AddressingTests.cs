using Aml.Engine.CAEX;
using OpcUaAml.Addressing;

namespace OpcUaAml.Tests;

public class AddressingTests
{
    private static readonly string[] Table = { "http://opcfoundation.org/UA/", "urn:server", "http://example.org/Plant/" };

    [Theory]
    [InlineData("nsu=http://example.org/Plant/;i=5", "http://example.org/Plant/", UaIdType.Numeric, "5")]
    [InlineData("nsu=http://example.org/Plant/;s=Line1.Motor", "http://example.org/Plant/", UaIdType.String, "Line1.Motor")]
    [InlineData("ns=2;s=Line1.Motor", "http://example.org/Plant/", UaIdType.String, "Line1.Motor")]
    [InlineData("i=85", "http://opcfoundation.org/UA/", UaIdType.Numeric, "85")]
    [InlineData("ns=2;g=0c7cb6c5-8b1e-4e2c-9f1a-2a0f3f6b8d11", "http://example.org/Plant/", UaIdType.Guid, "0c7cb6c5-8b1e-4e2c-9f1a-2a0f3f6b8d11")]
    [InlineData("nsu=urn:a;b;c;s=x;y", "urn:a;b;c", UaIdType.String, "x;y")]
    public void Parses_the_text_forms(string text, string uri, UaIdType type, string id)
    {
        var a = UaNodeAddress.Parse(text, Table);
        Assert.Equal(uri, a.NamespaceUri);
        Assert.Equal(type, a.IdType);
        Assert.Equal(id, a.Identifier);
    }

    [Fact]
    public void Formats_with_URI_and_with_index()
    {
        var a = new UaNodeAddress("http://example.org/Plant/", UaIdType.String, "Line1.Motor");
        Assert.Equal("nsu=http://example.org/Plant/;s=Line1.Motor", a.ToString());
        Assert.Equal("ns=2;s=Line1.Motor", a.ToNodeIdString(Table));
        Assert.Equal("i=85", new UaNodeAddress(Table[0], UaIdType.Numeric, "85").ToNodeIdString(Table));
    }

    [Theory]
    [InlineData("ns=7;i=1")]
    [InlineData("nsu=x;q=1")]
    [InlineData("nsu=x;i=abc")]
    [InlineData("")]
    public void Rejects_malformed_or_unresolvable_ids(string text) =>
        Assert.False(UaNodeAddress.TryParse(text, Table, out _));

    [Fact]
    public void AnnexA_NodeId_round_trips_every_identifier_type()
    {
        var doc = CAEXDocument.New_CAEXDocument();
        var ie = doc.CAEXFile.InstanceHierarchy.Append("IH").InternalElement.Append("Motor");
        foreach (var a in new[]
        {
            new UaNodeAddress("http://example.org/Plant/", UaIdType.Numeric, "42"),
            new UaNodeAddress("http://example.org/Plant/", UaIdType.String, "Line1.Motor", "urn:server:1"),
            new UaNodeAddress("http://example.org/Plant/", UaIdType.Guid, "0c7cb6c5-8b1e-4e2c-9f1a-2a0f3f6b8d11"),
            new UaNodeAddress("http://example.org/Plant/", UaIdType.Opaque, "AQID"),
        })
        {
            AnnexANodeId.Write(ie, a);
            Assert.Equal(a, AnnexANodeId.Of(ie));
            Assert.Single(ie.Attribute, x => x.Name == "NodeId");
        }
        var root = ie.Attribute["NodeId"]!.Attribute["RootNodeId"]!;
        Assert.Equal("ATL_OpcAmlMetaModel/ExplicitNodeId", root.RefAttributeType);
    }

    [Fact]
    public void AnnexA_BrowsePath_form_is_reported_not_guessed()
    {
        var doc = CAEXDocument.New_CAEXDocument();
        var ie = doc.CAEXFile.InstanceHierarchy.Append("IH").InternalElement.Append("Motor");
        var nodeId = AnnexANodeId.Write(ie, new UaNodeAddress("http://opcfoundation.org/UA/", UaIdType.Numeric, "85"));
        nodeId.Attribute.Append("BrowsePath").Attribute.Append("Elements").Attribute.Append("RelativePathElement");

        var ex = Assert.Throws<AddressingException>(() => AnnexANodeId.Read(nodeId));
        Assert.Contains("BrowsePath", ex.Message);
    }

    [Fact]
    public void MTP_items_carry_the_identifier_type_in_the_data_type()
    {
        var doc = CAEXDocument.New_CAEXDocument();
        var server = doc.CAEXFile.InstanceHierarchy.Append("IH").InternalElement.Append("OPCUAServer");
        var numeric = new UaNodeAddress("http://www.siemens.com/simatic-s7-opcua", UaIdType.Numeric, "17");
        var text = new UaNodeAddress("CODESYSSPV3/3S/IecVarAccess", UaIdType.String, "|var|PLC.Application.V1");

        var a = MtpOpcUaItem.Write(server, "V1", numeric);
        var b = MtpOpcUaItem.Write(server, "V2", text, access: 1);

        Assert.Equal("xs:int", a.Attribute["Identifier"]!.AttributeDataType);
        Assert.Equal(numeric, MtpOpcUaItem.Read(a).Address);
        Assert.Equal(new MtpItem(text, 1), MtpOpcUaItem.Read(b));
        Assert.True(MtpOpcUaItem.IsItem(a));
    }

    [Fact]
    public void DataVariables_resolve_the_index_through_the_server_namespace_table()
    {
        var doc = CAEXDocument.New_CAEXDocument();
        var ih = doc.CAEXFile.InstanceHierarchy.Append("IH");
        var machine = ih.InternalElement.Append("Machine");
        var server = machine.InternalElement.Append("OPCUA Server");
        server.Attribute.Append("DiscoveryURL").Value = "opc.tcp://localhost:4840";
        var address = new UaNodeAddress("http://example.org/Plant/", UaIdType.Numeric, "123");

        var dv = BprDataVariable.Write(machine, "Speed", address, server);

        Assert.Equal("ns=1;i=123", dv.Attribute["NodeId"]!.Value);
        Assert.True(BprDataVariable.IsDataVariable(dv));
        var (read, source) = BprDataVariable.Read(dv, doc);
        Assert.Equal(address, read);
        Assert.Equal("opc.tcp://localhost:4840", source.DiscoveryUrl);
        Assert.Equal(new[] { "http://opcfoundation.org/UA/", "http://example.org/Plant/" }, source.NamespaceTable);
    }

    [Fact]
    public void A_data_source_reads_the_DIN_SPEC_description_and_asks_for_security_as_described()
    {
        var doc = CAEXDocument.New_CAEXDocument();
        var ih = doc.CAEXFile.InstanceHierarchy.Append("IH");
        var plc = ih.InternalElement.Append("PLC");
        plc.Attribute.Append("EndpointURL").Value = "opc.tcp://plc:4840";
        plc.Attribute.Append("MessageSecurityMode").Value = "None";
        plc.Attribute.Append("TransportProfileURI").Value = "http://opcfoundation.org/UA-Profile/Transport/uatcp-uasc-uabinary";
        plc.Attribute.Append("UserToken").Value = "Anonymous";
        var secure = ih.InternalElement.Append("Secure");
        secure.Attribute.Append("DiscoveryURL").Value = "opc.tcp://secure:4840";
        secure.Attribute.Append("SecurityPolicy").Value = "http://opcfoundation.org/UA/SecurityPolicy#Basic256Sha256";
        var address = new UaNodeAddress("http://example.org/Plant/", UaIdType.Numeric, "1");
        BprDataVariable.Write(ih.InternalElement.Append("A"), "X", address, plc);
        BprDataVariable.Write(ih.InternalElement.Append("B"), "Y", address, plc);
        BprDataVariable.Write(ih.InternalElement.Append("C"), "Z", address, secure);

        var sources = BprDataVariable.SourcesIn(doc);

        Assert.Equal(new[] { "PLC", "Secure" }, sources.Select(s => s.Element.Name));
        var first = sources[0];
        Assert.Equal("Anonymous", first.UserToken);
        Assert.EndsWith("uatcp-uasc-uabinary", first.TransportProfileUri);
        Assert.False(first.ToConnectOptions().UseSecurity);
        var second = sources[1].ToConnectOptions();
        Assert.Equal("opc.tcp://secure:4840", second.EndpointUrl);
        Assert.True(second.UseSecurity);
    }

    [Fact]
    public void The_three_conventions_convert_into_each_other()
    {
        var doc = CAEXDocument.New_CAEXDocument();
        var ih = doc.CAEXFile.InstanceHierarchy.Append("IH");
        var server = ih.InternalElement.Append("Server");
        var element = ih.InternalElement.Append("Pump");
        var original = new UaNodeAddress("http://example.org/Plant/", UaIdType.String, "Pump1.Speed");

        var mtp = MtpOpcUaItem.Read(MtpOpcUaItem.Write(server, "Pump1.Speed", original)).Address;
        var annexA = AnnexANodeId.Read(AnnexANodeId.Write(element, mtp));
        var (bpr, _) = BprDataVariable.Read(BprDataVariable.Write(element, "Speed", annexA, server), doc);

        Assert.Equal(original, bpr);
    }
}
