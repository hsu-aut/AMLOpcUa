using Aml.Engine.CAEX;
using OpcUaAml.Addressing;
using OpcUaAml.Server;
using OpcUaAml.Types;

namespace OpcUaAml.Tests;

/// <summary>
/// The document as a server with types: an element that is an instance of a
/// UA type is served as an instance of that type, and the types come along
/// from the NodeSets of their models, without the instances those bring.
/// </summary>
[Trait("Speed", "Slow")]
public class ServeTypesTests(DiDocument di) : IClassFixture<DiDocument>
{
    private const string Di = "http://opcfoundation.org/UA/DI/";

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private async Task<(AmlServerHost Host, UaClient Client)> ServeAsync(bool withTypes)
    {
        var pki = Path.Combine(Path.GetTempPath(), "amlopcua-test-pki-" + Guid.NewGuid().ToString("N")[..8]);
        var host = await AmlServerHost.StartAsync(di.Document, new AmlServerOptions
        {
            Port = FreePort(), PkiRoot = Path.Combine(pki, "server"), WithTypes = withTypes,
        });
        var client = await UaClient.ConnectAsync(new UaConnectOptions
        {
            EndpointUrl = host.EndpointUrl, UseSecurity = false, AcceptUntrustedServerCertificates = true,
            PkiRoot = Path.Combine(pki, "client"),
        });
        return (host, client);
    }

    private InstanceHierarchyType WithFirmware(string hierarchy)
    {
        var ih = di.Hierarchy(hierarchy);
        ih.InternalElement.Insert(TypeInstantiator.Instantiate(di.Type("SoftwareVersionType"), "Firmware").Instance, asFirst: false);
        return ih;
    }

    private static async Task<UaBrowseItem> FirmwareAsync(UaClient client, string hierarchy)
    {
        var folder = (await client.BrowseAsync()).Single(i => i.BrowseName == hierarchy);
        return (await client.BrowseAsync(folder.Address)).Single(i => i.BrowseName == "Firmware");
    }

    [Fact]
    public async Task An_instance_of_a_UA_type_is_served_as_one_of_that_type()
    {
        WithFirmware("Typed");
        var (host, client) = await ServeAsync(withTypes: true);
        await using var _ = host;
        await using var __ = client;

        var firmware = await FirmwareAsync(client, "Typed");
        var type = AnnexANodeId.Of(di.Type("SoftwareVersionType"))! with { ServerUri = null };

        Assert.Equal(type, firmware.TypeDefinition! with { ServerUri = null });
        Assert.Equal("SoftwareVersionType", (await client.DescribeAsync(type)).DisplayName);
        // The type keeps its instance declarations.
        Assert.Contains(await client.BrowseAsync(type), c => c.BrowseName == "SoftwareRevision");
        Assert.Contains(Di, host.ServedTypes);
        Assert.Empty(host.MissingTypes);
    }

    [Fact]
    public async Task Without_types_an_instance_is_a_plain_object()
    {
        WithFirmware("Plain");
        var (host, client) = await ServeAsync(withTypes: false);
        await using var _ = host;
        await using var __ = client;

        var firmware = await FirmwareAsync(client, "Plain");

        Assert.Equal(new UaNodeAddress("http://opcfoundation.org/UA/", UaIdType.Numeric, "58"), firmware.TypeDefinition! with { ServerUri = null });
        Assert.Empty(host.ServedTypes);
    }

    [Fact]
    public async Task The_instances_a_model_brings_are_not_served_beside_the_documents()
    {
        WithFirmware("Beside");
        var (host, client) = await ServeAsync(withTypes: true);
        await using var _ = host;
        await using var __ = client;

        // DI declares DeviceSet below Objects; the document's own elements are what is served there.
        Assert.DoesNotContain(await client.BrowseAsync(), i => i.BrowseName == "DeviceSet");
        Assert.Equal("DeviceType", (await client.DescribeAsync(AnnexANodeId.Of(di.Type("DeviceType"))! with { ServerUri = null })).DisplayName);
    }

    [Fact]
    public async Task A_model_whose_NodeSet_is_nowhere_is_named_and_its_instances_stay_plain()
    {
        const string missing = "http://example.org/Missing/";
        var doc = CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX3_0);
        var lib = doc.CAEXFile.SystemUnitClassLib.Append("SUC_" + missing);
        var machineType = lib.SystemUnitClass.Append("MachineType");
        AnnexANodeId.Write(machineType, new UaNodeAddress(missing, UaIdType.Numeric, "1003"));
        var machine = doc.CAEXFile.InstanceHierarchy.Append("Lonely").InternalElement.Append("Machine");
        machine.RefBaseSystemUnitPath = $"[SUC_{missing}]/[MachineType]";
        var pki = Path.Combine(Path.GetTempPath(), "amlopcua-test-pki-" + Guid.NewGuid().ToString("N")[..8]);

        await using var host = await AmlServerHost.StartAsync(doc, new AmlServerOptions
        {
            Port = FreePort(), PkiRoot = Path.Combine(pki, "server"), WithTypes = true,
        });
        await using var client = await UaClient.ConnectAsync(new UaConnectOptions
        {
            EndpointUrl = host.EndpointUrl, UseSecurity = false, AcceptUntrustedServerCertificates = true, PkiRoot = Path.Combine(pki, "client"),
        });
        var folder = (await client.BrowseAsync()).Single(i => i.BrowseName == "Lonely");
        var served = (await client.BrowseAsync(folder.Address)).Single(i => i.BrowseName == "Machine");

        Assert.Equal([missing], host.MissingTypes);
        Assert.Equal("58", served.TypeDefinition!.Identifier);
    }
}
