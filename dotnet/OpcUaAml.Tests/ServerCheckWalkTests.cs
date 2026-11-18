using OpcUaAml.Addressing;
using OpcUaAml.Checks;
using OpcUaAml.Server;

namespace OpcUaAml.Tests;

/// <summary>
/// How Check server walks a server, from the review of 2026-11-18: a node
/// reached on two ways (a folder organizing a part of the plant, or a circle of
/// Organizes) was checked once per way, a start node the server does not have
/// passed as checked.
/// </summary>
[Trait("Speed", "Slow")]
public class ServerCheckWalkTests : IAsyncLifetime
{
    private NodeSetServerHost _host = null!;
    private UaClient _client = null!;

    public async Task InitializeAsync()
    {
        var pki = Path.Combine(Path.GetTempPath(), "amlopcua-test-pki-" + Guid.NewGuid().ToString("N")[..8]);
        // The check fixture with a folder Area that Plant organizes and that organizes Plant again.
        _host = await NodeSetServerHost.StartAsync(new[] { Fixtures.Path("servercheck", "Cycle.NodeSet2.xml") },
            new NodeSetServerOptions { Port = Uaaml.FreePort(), PkiRoot = Path.Combine(pki, "server") });
        _client = await UaClient.ConnectAsync(new UaConnectOptions
        {
            EndpointUrl = _host.EndpointUrl, UseSecurity = false, AcceptUntrustedServerCertificates = true,
            PkiRoot = Path.Combine(pki, "client"),
        });
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task A_node_reached_on_two_ways_is_checked_once()
    {
        var result = await ServerChecker.CheckAsync(_client, limit: 400);

        Assert.False(result.Truncated);
        Assert.Equal(result.Findings.Count, result.Findings.Select(f => (f.Rule, f.ElementId, f.Message)).Distinct().Count());
    }

    [Fact]
    public async Task A_start_node_the_server_does_not_have_is_an_error() =>
        await Assert.ThrowsAsync<UaConnectionException>(() =>
            ServerChecker.CheckAsync(_client, new UaNodeAddress("http://example.org/Check/", UaIdType.Numeric, "987654")));

    [Fact]
    public async Task A_start_node_that_is_no_object_or_variable_is_an_error() =>
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ServerChecker.CheckAsync(_client, new UaNodeAddress("http://opcfoundation.org/UA/", UaIdType.Numeric, "58")));
}
