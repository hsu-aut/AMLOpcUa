using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using OpcUaAml.Addressing;
using OpcUaAml.Server;

namespace OpcUaAml.Tests;

public class MirrorTests(TestServer server, DiDocument di) : IClassFixture<TestServer>, IClassFixture<DiDocument>
{
    private UaConnectOptions Options() => new()
    {
        EndpointUrl = server.EndpointUrl,
        UseSecurity = false,
        AcceptUntrustedServerCertificates = true,
        PkiRoot = Path.Combine(server.PkiRoot, "client-" + Guid.NewGuid().ToString("N")[..6]),
    };

    private static async Task<UaBrowseItem> PlantFolder(UaClient client) =>
        (await client.BrowseAsync()).Single(i => i.BrowseName == "Plant");

    [Fact]
    public async Task Mirrors_a_subtree_with_types_NodeIds_and_values()
    {
        await using var client = await UaClient.ConnectAsync(Options());
        var ih = di.Hierarchy("Mirror");

        var result = await AddressSpaceMirror.MirrorAsync(client, await PlantFolder(client), ih);

        Assert.Equal(9, result.Nodes);
        Assert.False(result.Truncated);
        var plant = ih.InternalElement["Plant"]!;
        Assert.Equal("[SUC_http://opcfoundation.org/UA/]/[FolderType]", plant.RefBaseSystemUnitPath);
        var pump = plant.InternalElement["Pump1"]!;
        Assert.Equal("[SUC_http://opcfoundation.org/UA/]/[BaseObjectType]", pump.RefBaseSystemUnitPath);
        var speed = pump.InternalElement["Speed"]!;
        Assert.Equal("[SUC_http://opcfoundation.org/UA/]/[BaseDataVariableType]", speed.RefBaseSystemUnitPath);
        Assert.Equal("12.5", speed.Attribute["Value"]!.Value);
        Assert.Equal(new UaNodeAddress(TestServer.Namespace, UaIdType.String, "Plant.Pump1.Speed", client.ServerUri), AnnexANodeId.Of(speed));
        Assert.Equal("1 2 3", pump.InternalElement["Motor"]!.InternalElement["Samples"]!.Attribute["Value"]!.Value);
        Assert.Equal(9, result.Typed);
    }

    [Fact]
    public async Task Depth_and_node_limit_are_respected()
    {
        await using var client = await UaClient.ConnectAsync(Options());
        var plantFolder = await PlantFolder(client);

        var shallow = await AddressSpaceMirror.MirrorAsync(client, plantFolder, di.Hierarchy("Shallow"), new MirrorOptions { Depth = 1 });
        var capped = await AddressSpaceMirror.MirrorAsync(client, plantFolder, di.Hierarchy("Capped"), new MirrorOptions { MaxNodes = 3 });

        Assert.Equal(3, shallow.Nodes);
        Assert.Equal(3, capped.Nodes);
        Assert.True(capped.Truncated);
    }

    [Fact]
    public async Task A_snapshot_refreshes_bound_elements_and_data_variables()
    {
        await using var client = await UaClient.ConnectAsync(Options());
        var ih = di.Hierarchy("Snapshot");
        await AddressSpaceMirror.MirrorAsync(client, await PlantFolder(client), ih, new MirrorOptions { ReadValues = false });
        var speed = ih.InternalElement["Plant"]!.InternalElement["Pump1"]!.InternalElement["Speed"]!;
        speed.Attribute.Append("Value").Value = "0";

        // A BPR DataVariable on this server, one on another server, and a
        // binding to a node that does not exist.
        var machine = ih.InternalElement.Append("Machine");
        var here = machine.InternalElement.Append("Server here");
        here.Attribute.Append("EndpointURL").Value = server.EndpointUrl;
        var elsewhere = machine.InternalElement.Append("Server elsewhere");
        elsewhere.Attribute.Append("EndpointURL").Value = "opc.tcp://elsewhere:4840";
        var temperature = new UaNodeAddress(TestServer.Namespace, UaIdType.String, "Plant.Pump1.Motor.Temperature");
        BprDataVariable.Write(machine, "Temperature", temperature, here);
        BprDataVariable.Write(machine, "Remote", temperature, elsewhere);
        var ghost = ih.InternalElement.Append("Ghost");
        AnnexANodeId.Write(ghost, new UaNodeAddress(TestServer.Namespace, UaIdType.String, "Plant.Nothing"));
        ghost.Attribute.Append("Value");

        var result = await ValueSnapshot.ApplyAsync(di.Document, client);

        Assert.Equal("12.5", speed.Attribute["Value"]!.Value);
        Assert.Equal("42", machine.Attribute["Temperature"]!.Value);
        Assert.Null(machine.Attribute["Remote"]!.Value);
        Assert.True(result.Skipped >= 1);
        Assert.Contains(result.Problems, p => p.StartsWith("Ghost") && p.Contains("BadNodeIdUnknown"));
    }
}
