using System.Xml.Linq;
using OpcUaAml.Compare;
using OpcUaAml.NodeSets;
using OpcUaAml.Server;

namespace OpcUaAml.Tests;

/// <summary>
/// A server whose address space is a NodeSet: the plant that does not exist
/// yet. DI stands in for it here, because it ships with every build; the
/// example beside it (examples/mps500) is the same thing with a model of a
/// learning factory.
/// </summary>
public sealed class NodeSetServerFixture : IAsyncLifetime
{
    public NodeSetServerHost Host { get; private set; } = null!;
    public string PkiRoot { get; } = Path.Combine(Path.GetTempPath(), "amlopcua-nodeset-pki-" + Guid.NewGuid().ToString("N")[..8]);

    public async Task InitializeAsync() =>
        Host = await NodeSetServerHost.StartAsync(new[] { DiTestServer.DiPath }, new NodeSetServerOptions
        {
            Port = FreePort(),
            PkiRoot = Path.Combine(PkiRoot, "server"),
            Simulate = false,
        });

    public async Task DisposeAsync()
    {
        await Host.DisposeAsync();
        try { Directory.Delete(PkiRoot, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

[Trait("Speed", "Slow")]
public class NodeSetServerTests(NodeSetServerFixture fixture) : IClassFixture<NodeSetServerFixture>
{
    private Task<UaClient> ConnectAsync() => UaClient.ConnectAsync(new UaConnectOptions
    {
        EndpointUrl = fixture.Host.EndpointUrl,
        UseSecurity = false,
        AcceptUntrustedServerCertificates = true,
        PkiRoot = Path.Combine(fixture.PkiRoot, "client"),
    });

    [Fact]
    public void Serves_the_model_and_says_which()
    {
        Assert.Equal(new[] { Fixtures.DiUri }, fixture.Host.Served);
        Assert.True(fixture.Host.Nodes > 100);
        Assert.False(fixture.Host.Simulating);
    }

    [Fact]
    public async Task A_client_takes_the_NodeSet_from_the_server_unchanged()
    {
        // The whole point of the published file: what the client gets back is
        // the model, not what browsing could reconstruct of it.
        await using var client = await ConnectAsync();

        var namespaces = await ServerNodeSets.ListAsync(client);
        var set = await ServerNodeSets.FetchAsync(client, Fixtures.DiUri);

        var di = namespaces.Single(n => n.Uri == Fixtures.DiUri);
        Assert.True(di.HasFile);
        Assert.Equal(ServerNodeSetSource.NamespaceFile, set.Source);
        Assert.Empty(new NodeSetComparer().Compare(XDocument.Load(DiTestServer.DiPath), set.Document));
    }

    [Fact]
    public async Task The_types_of_the_model_are_there_to_browse()
    {
        await using var client = await ConnectAsync();

        var set = await ServerNodeSets.FetchAsync(client, Fixtures.DiUri, new ServerNodeSetOptions { PreferNamespaceFile = false });

        Assert.Equal(ServerNodeSetSource.Browsed, set.Source);
        Assert.Contains(set.Document.Root!.Elements(), e => (string?)e.Attribute("BrowseName") is { } n && n.EndsWith(":DeviceType", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_publishing_the_client_rebuilds_the_NodeSet_by_browsing()
    {
        await using var host = await NodeSetServerHost.StartAsync(new[] { DiTestServer.DiPath }, new NodeSetServerOptions
        {
            Port = NodeSetServerFixture.FreePort(),
            PkiRoot = Path.Combine(fixture.PkiRoot, "server-no-file"),
            PublishNodeSets = false,
        });
        await using var client = await UaClient.ConnectAsync(new UaConnectOptions
        {
            EndpointUrl = host.EndpointUrl, UseSecurity = false, AcceptUntrustedServerCertificates = true,
            PkiRoot = Path.Combine(fixture.PkiRoot, "client"),
        });

        var namespaces = await ServerNodeSets.ListAsync(client);
        var set = await ServerNodeSets.FetchAsync(client, Fixtures.DiUri);

        Assert.False(namespaces.Single(n => n.Uri == Fixtures.DiUri).HasFile);
        Assert.Equal(ServerNodeSetSource.Browsed, set.Source);
    }

    [Fact]
    public async Task A_model_that_requires_another_is_served_with_it()
    {
        // The example plant requires DI; serving it alone would leave its
        // types without their supertypes.
        var example = Path.Combine(Repository.Root, "examples", "mps500", "MPS500.NodeSet2.xml");
        Assert.True(File.Exists(example), $"'{example}' is missing; run examples/mps500/build.mjs.");

        await using var host = await NodeSetServerHost.StartAsync(new[] { example }, new NodeSetServerOptions
        {
            Port = NodeSetServerFixture.FreePort(),
            PkiRoot = Path.Combine(fixture.PkiRoot, "server-plant"),
        });

        Assert.Equal(new[] { Fixtures.DiUri, "http://hsu-hh.de/UA/MPS500/" }, host.Served);
    }

    [Fact]
    public async Task The_values_move_when_the_plant_is_simulated()
    {
        var example = Path.Combine(Repository.Root, "examples", "mps500", "MPS500.NodeSet2.xml");
        await using var host = await NodeSetServerHost.StartAsync(new[] { example }, new NodeSetServerOptions
        {
            Port = NodeSetServerFixture.FreePort(),
            PkiRoot = Path.Combine(fixture.PkiRoot, "server-simulated"),
            Simulate = true,
        });
        Assert.True(host.Simulating);

        await using var client = await UaClient.ConnectAsync(new UaConnectOptions
        {
            EndpointUrl = host.EndpointUrl, UseSecurity = false, AcceptUntrustedServerCertificates = true,
            PkiRoot = Path.Combine(fixture.PkiRoot, "client"),
        });
        var children = await client.BrowseAsync(null);
        var line = children.Single(c => c.DisplayName == "MPS500");
        var stations = await client.BrowseAsync(line.Address);

        Assert.Equal(6, stations.Count);
        Assert.Contains(stations, s => s.DisplayName == "ST30_Processing");
    }
}
