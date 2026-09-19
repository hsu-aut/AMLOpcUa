using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using OpcUaAml.Addressing;
using OpcUaAml.Links;
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

    [Fact]
    public async Task Live_values_follow_the_server()
    {
        await using var client = await UaClient.ConnectAsync(Options());
        var ih = di.Hierarchy("Live");
        await AddressSpaceMirror.MirrorAsync(client, await PlantFolder(client), ih, new MirrorOptions { ReadValues = false });
        var counter = ih.InternalElement["Plant"]!.InternalElement["Counter"]!;
        counter.Attribute.Append("Value").Value = "-1";

        // Writes happen under one lock, as the plugin runs them on one thread.
        var gate = new object();
        var seen = new List<string?>();
        await using (var live = await LiveValues.FollowAsync(di.Document, client, write => { lock (gate) write(); },
            u => { if (u.What == "Counter") seen.Add(u.Value); }, publishingIntervalMs: 100))
        {
            Assert.True(live.Count >= 1);
            for (var i = 0; i < 50 && seen.Distinct().Count() < 3; i++) await Task.Delay(100);
            Assert.True(live.Updates >= 3, $"{live.Updates} update(s)");
        }

        lock (gate)
        {
            Assert.True(seen.Distinct().Count() >= 3, string.Join(",", seen));
            Assert.Equal(seen[^1], counter.Attribute["Value"]!.Value);
            Assert.NotEqual("-1", counter.Attribute["Value"]!.Value);
        }
    }

    [Fact]
    public async Task A_snapshot_fills_the_older_aml_opcua_variable_binding()
    {
        await using var client = await UaClient.ConnectAsync(Options());
        var index = client.NamespaceTable.ToList().IndexOf(TestServer.Namespace);
        var element = di.Hierarchy("Legacy").InternalElement.Append("Motor");
        var value = element.Attribute.Append("Temperature");
        var binding = value.Attribute.Append(LegacyOpcUaVariable.SubAttribute);
        binding.Attribute.Append("ServerAddress").Value = server.EndpointUrl;
        binding.Attribute.Append("VariableNodeId").Value = $"ns={index};s=Plant.Pump1.Motor.Temperature";

        await ValueSnapshot.ApplyAsync(di.Document, client);

        Assert.Equal("42", value.Value);
    }

    [Fact]
    public async Task A_mirrored_node_becomes_an_aspect_of_its_planned_element()
    {
        await using var client = await UaClient.ConnectAsync(Options());
        var plan = di.Hierarchy("Plan");
        var pump = plan.InternalElement.Append("Pump P-101");
        AnnexANodeId.Write(pump, new UaNodeAddress(TestServer.Namespace, UaIdType.String, "Plant.Pump1"));
        // Two planned elements claim the same node: no link, a note.
        foreach (var name in new[] { "Speed A", "Speed B" })
            AnnexANodeId.Write(plan.InternalElement.Append(name), new UaNodeAddress(TestServer.Namespace, UaIdType.String, "Plant.Pump1.Speed"));

        var result = await AddressSpaceMirror.MirrorAsync(client, await PlantFolder(client), di.Hierarchy("AsBuilt"),
            new MirrorOptions { PlannedIn = plan, ReadValues = false });

        var mirrored = result.Root.InternalElement["Pump1"]!;
        var reference = mirrored.Attribute[ObjectReferences.RefBaseObj]!;
        Assert.Equal(pump.ID, reference.Value);
        Assert.Equal("AutomationML_ObjectReferences_AttributeTypeLib/refBaseObj", reference.RefAttributeType);
        Assert.NotNull(di.Document.CAEXFile.AttributeTypeLib[ObjectReferences.Lib]);
        Assert.Null(pump.Attribute[ObjectReferences.RefBaseObj]);
        Assert.Null(mirrored.InternalElement["Speed"]!.Attribute[ObjectReferences.RefBaseObj]);
        Assert.Equal(1, result.Linked);
        Assert.Contains(result.Notes, n => n.StartsWith("Speed") && n.Contains("2 planned elements"));
    }
}
