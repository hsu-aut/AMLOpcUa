using Aml.Engine.CAEX;
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
