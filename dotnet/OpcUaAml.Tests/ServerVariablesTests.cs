using Aml.Engine.CAEX;
using OpcUaAml.Addressing;
using OpcUaAml.Binding;
using OpcUaAml.Server;

namespace OpcUaAml.Tests;

/// <summary>The variables of a running server, collected for binding a model to them.</summary>
[Trait("Speed", "Slow")]
public class ServerVariablesTests
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

    /// <summary>The practice plant, as the plugin serves it, and a client of it.</summary>
    private static async Task<(NodeSetServerHost Host, UaClient Client)> PracticePlantAsync()
    {
        var pki = TempPki();
        var example = Path.Combine(Repository.Root, "examples", "mps500", "MPS500.NodeSet2.xml");
        var host = await NodeSetServerHost.StartAsync(new[] { example }, new NodeSetServerOptions
        {
            Port = FreePort(), PkiRoot = Path.Combine(pki, "server"), Simulate = true,
        });
        var client = await UaClient.ConnectAsync(new UaConnectOptions
        {
            EndpointUrl = host.EndpointUrl, UseSecurity = false, AcceptUntrustedServerCertificates = true,
            PkiRoot = Path.Combine(pki, "client"),
        });
        return (host, client);
    }

    [Fact]
    public async Task The_variables_of_the_practice_plant_come_with_their_paths_and_kinds()
    {
        var (host, client) = await PracticePlantAsync();
        await using var _ = host;
        await using var __ = client;

        var found = await ServerVariables.CollectAsync(client);

        var speed = found.Variables.Single(v => v.PathText == "MPS500/ST30_Processing/MD_Drilling_unit/DV_Drill_spindle/Speed");
        Assert.Equal("Double", speed.DataType);
        Assert.Contains(found.Variables, v => v.PathText == "MPS500/ST30_Processing/ParameterSet/PartsProduced" && v.DataType == "UInt32");
        // Not the server's own diagnostics, and not what describes a variable rather than being one.
        Assert.DoesNotContain(found.Variables, v => v.Address.NamespaceUri == "http://opcfoundation.org/UA/");
        Assert.DoesNotContain(found.Variables, v => v.Path[^1] is "EngineeringUnits" or "EURange");
        Assert.False(found.Truncated);
    }

    // Review of 2026-11-18: below a NodeId the server does not have (a typo), the search
    // found nothing and said nothing; uaaml bind printed "(none)" for every value and passed.
    [Fact]
    public async Task Collecting_below_a_node_the_server_does_not_have_is_an_error()
    {
        var (host, client) = await PracticePlantAsync();
        await using var _ = host;
        await using var __ = client;

        await Assert.ThrowsAsync<UaConnectionException>(() =>
            ServerVariables.CollectAsync(client, new UaNodeAddress("http://hsu-hh.de/UA/MPS500/", UaIdType.String, "Typo")));
    }

    // Review of 2026-11-18: a value bound by a browse path kept its binding, but its variable
    // was free for the others, so another value of the model could be proposed it.
    [Fact]
    public async Task A_value_bound_by_a_browse_path_keeps_its_variable_from_the_others()
    {
        var (host, client) = await PracticePlantAsync();
        await using var _ = host;
        await using var __ = client;
        async Task<UaBrowseItem> Child(UaNodeAddress? parent, string name) =>
            (await client.BrowseAsync(parent)).Single(i => i.BrowseName == name);
        var parameters = await Child((await Child((await Child(null, "MPS500")).Address, "ST30_Processing")).Address, "ParameterSet");
        var parts = await Child(parameters.Address, "PartsProduced");

        var doc = CAEXDocument.New_CAEXDocument();
        var plant = doc.CAEXFile.InstanceHierarchy.Append("Plant");
        var bound = plant.InternalElement.Append("Station").InternalElement.Append("PartsProduced");
        bound.Attribute.Append("Value");
        var nodeId = bound.Attribute.Append("NodeId");
        var root = nodeId.Attribute.Append("RootNodeId");
        root.Attribute.Append("NamespaceUri").Value = parameters.Address.NamespaceUri;
        root.Attribute.Append(parameters.Address.IdType == UaIdType.Numeric ? "NumericId" : "StringId").Value = parameters.Address.Identifier;
        var target = nodeId.Attribute.Append("BrowsePath").Attribute.Append("Elements").Attribute.Append("0").Attribute.Append("TargetName");
        target.Attribute.Append("NamespaceUri").Value = parts.Address.NamespaceUri;
        target.Attribute.Append("Name").Value = "PartsProduced";
        plant.InternalElement.Append("Line").InternalElement.Append("PartsProduced");

        var resolved = await ModelBinder.ResolveIndirectAsync(doc, client);
        var found = await ServerVariables.CollectAsync(client);
        var plan = ModelBinder.Propose(ModelBinder.OwnSignals(plant, resolved), found.Variables);

        var mine = plan.Proposals.Single(p => p.Signal.PathText == "Plant/Station/PartsProduced");
        var other = plan.Proposals.Single(p => p.Signal.PathText == "Plant/Line/PartsProduced");
        Assert.True(mine.BoundAlready);
        Assert.Equal(parts.Address with { ServerUri = null }, mine.Variable?.Address with { ServerUri = null });
        Assert.NotEqual(parts.Address with { ServerUri = null }, other.Variable?.Address with { ServerUri = null });
    }

    [Fact]
    public async Task Collecting_stops_at_the_limit_and_says_so()
    {
        var (host, client) = await PracticePlantAsync();
        await using var _ = host;
        await using var __ = client;

        var found = await ServerVariables.CollectAsync(client, limit: 10);

        Assert.Equal(10, found.Variables.Count);
        Assert.True(found.Truncated);
    }

    [Fact]
    public async Task The_model_of_the_binding_lesson_is_bound_to_the_practice_plant_and_read()
    {
        var (host, client) = await PracticePlantAsync();
        await using var _ = host;
        await using var __ = client;
        var doc = CAEXDocument.New_CAEXDocument();
        var ih = doc.CAEXFile.InstanceHierarchy.Append("Plant");
        var station = ih.InternalElement.Append("ProcessingStation");
        var speed = station.InternalElement.Append("Drill").InternalElement.Append("SpindleSpeed");
        station.InternalElement.Append("PartsCounter");
        station.InternalElement.Append("Turntable");

        var plan = ModelBinder.Propose(ModelBinder.Signals(ih), (await ServerVariables.CollectAsync(client)).Variables);
        var bound = ModelBinder.Apply(plan.Proposals.Where(p => p.Keep), client.ServerUri);
        var read = await ValueSnapshot.ApplyAsync(doc, client);

        Assert.True(plan.Proposals.Single(p => p.Signal.PathText.EndsWith("SpindleSpeed", StringComparison.Ordinal)).Keep, string.Join("\n", plan.Proposals));
        Assert.True(bound >= 1);
        Assert.False(string.IsNullOrEmpty(speed.Attribute["Value"]?.Value), read.ToString());
    }
}
