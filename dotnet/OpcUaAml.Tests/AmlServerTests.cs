using Aml.Engine.CAEX;
using OpcUaAml.Addressing;
using OpcUaAml.Server;
using OpcUaAml.Types;

namespace OpcUaAml.Tests;

public class AmlServerTests(DiDocument di) : IClassFixture<DiDocument>
{
    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static string TempPki() => Path.Combine(Path.GetTempPath(), "amlopcua-test-pki-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public async Task Serves_the_instance_hierarchies_and_mirrors_back_to_the_same_structure()
    {
        // A modelled line with values, and an instance of a DI type.
        var ih = di.Hierarchy("Served");
        var line = ih.InternalElement.Append("Line");
        var conveyor = line.InternalElement.Append("Conveyor");
        var speed = conveyor.InternalElement.Append("Speed");
        var v = speed.Attribute.Append("Value");
        v.AttributeDataType = "xs:double";
        v.Value = "1.5";
        var running = conveyor.InternalElement.Append("Running");
        var r = running.Attribute.Append("Value");
        r.AttributeDataType = "xs:boolean";
        r.Value = "true";
        var firmware = TypeInstantiator.Instantiate(di.Type("SoftwareVersionType"), "Firmware").Instance;
        ih.InternalElement.Insert(firmware, asFirst: false);
        AnnexANodeId.Write(ih.InternalElement["Firmware"]!, new UaNodeAddress("http://example.org/Plant/", UaIdType.Numeric, "5001"));

        var pki = TempPki();
        await using var host = await AmlServerHost.StartAsync(di.Document, new AmlServerOptions { Port = FreePort(), PkiRoot = Path.Combine(pki, "server") });
        await using var client = await UaClient.ConnectAsync(new UaConnectOptions
        {
            EndpointUrl = host.EndpointUrl,
            UseSecurity = false,
            AcceptUntrustedServerCertificates = true,
            PkiRoot = Path.Combine(pki, "client"),
        });

        var folder = (await client.BrowseAsync()).Single(i => i.BrowseName == "Served");
        var top = await client.BrowseAsync(folder.Address);
        Assert.Contains(top, i => i.BrowseName == "Line");
        var fw = top.Single(i => i.BrowseName == "Firmware");
        Assert.Equal(new UaNodeAddress("http://example.org/Plant/", UaIdType.Numeric, "5001"), fw.Address);

        var speedNode = new UaNodeAddress("urn:amlopcua:document", UaIdType.String, "Served/Line/Conveyor/Speed");
        var read = await client.ReadAsync(speedNode);
        Assert.True(read.Good, read.Status);
        Assert.Equal("Double", read.DataType);
        Assert.Equal("1.5", read.ValueText);
        Assert.Contains(await client.BrowseAsync(fw.Address), i => i.BrowseName == "SoftwareRevision" && i.NodeClass == "Variable");

        // Back into AML: the structure and the values come back.
        var back = CAEXDocument.New_CAEXDocument();
        var backIh = back.CAEXFile.InstanceHierarchy.Append("Back");
        await AddressSpaceMirror.MirrorAsync(client, folder, backIh, new MirrorOptions { Depth = 5 });
        var served = backIh.InternalElement["Served"]!;
        Assert.Equal("1.5", served.InternalElement["Line"]!.InternalElement["Conveyor"]!.InternalElement["Speed"]!.Attribute["Value"]!.Value);
        Assert.Equal("true", served.InternalElement["Line"]!.InternalElement["Conveyor"]!.InternalElement["Running"]!.Attribute["Value"]!.Value);
        Assert.Equal(
            firmware.InternalElement.Select(c => c.Name).OrderBy(n => n),
            served.InternalElement["Firmware"]!.InternalElement.Select(c => c.Name).OrderBy(n => n));
    }

    [Fact]
    public void Duplicate_NodeIds_are_made_unique()
    {
        var doc = CAEXDocument.New_CAEXDocument();
        var ih = doc.CAEXFile.InstanceHierarchy.Append("Dup");
        var a = new UaNodeAddress("http://example.org/Plant/", UaIdType.Numeric, "7");
        AnnexANodeId.Write(ih.InternalElement.Append("A"), a);
        AnnexANodeId.Write(ih.InternalElement.Append("B"), a);

        var space = AmlAddressSpace.From(doc, "urn:test");

        var addresses = space.Roots[0].Children.Select(c => c.Address).ToList();
        Assert.Equal(2, addresses.Distinct().Count());
        Assert.Contains("http://example.org/Plant/", space.NamespaceUris);
    }
}
