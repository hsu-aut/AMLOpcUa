using OpcUaAml.Server;

namespace OpcUaAml.Tests;

/// <summary>
/// Finding servers instead of typing their address: a discovery server (a
/// Local Discovery Server on port 4840, or any server about itself) lists the
/// servers it knows, and each says which endpoints it offers.
/// </summary>
public class DiscoveryTests
{
    [Theory]
    [InlineData("plc", "opc.tcp://plc:4840")]
    [InlineData("plc:4841", "opc.tcp://plc:4841")]
    [InlineData(" 192.168.0.10 ", "opc.tcp://192.168.0.10:4840")]
    [InlineData("opc.tcp://plc:48400/AMLOpcUa", "opc.tcp://plc:48400/AMLOpcUa")]
    [InlineData("opc.tcp://plc", "opc.tcp://plc:4840")]
    public void A_host_becomes_the_discovery_url_of_port_4840(string text, string url) =>
        Assert.Equal(url, UaDiscovery.DiscoveryUrl(text));

    [Fact]
    public void Nothing_typed_is_this_computer() =>
        Assert.Equal("opc.tcp://localhost:4840", UaDiscovery.DiscoveryUrl(""));
}

/// <summary>Discovery against a running server.</summary>
[Trait("Speed", "Slow")]
public class DiscoveryServerTests
{
    [Fact]
    public async Task A_server_names_itself_and_its_endpoints()
    {
        var pki = Path.Combine(Path.GetTempPath(), "amlopcua-test-pki-" + Guid.NewGuid().ToString("N")[..8]);
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        await using var host = await NodeSetServerHost.StartAsync(new[] { DiTestServer.DiPath },
            new NodeSetServerOptions { Port = port, PkiRoot = Path.Combine(pki, "server") });

        var servers = await UaDiscovery.FindServersAsync($"127.0.0.1:{port}", Path.Combine(pki, "client"));
        var server = Assert.Single(servers);
        var endpoints = await UaDiscovery.GetEndpointsAsync(server.DiscoveryUrls[0], Path.Combine(pki, "client"));

        Assert.Equal("AMLOpcUa NodeSet server", server.Name);
        Assert.Equal("Server", server.Kind);
        Assert.Contains(server.DiscoveryUrls, u => u.Contains($":{port}", StringComparison.Ordinal));
        Assert.Contains(endpoints, e => e.SecurityMode == "None" && e.UserTokens.Contains("Anonymous"));
        Assert.Contains(endpoints, e => e.SecurityMode == "SignAndEncrypt");
    }

    [Fact]
    public async Task Where_no_server_answers_it_says_so()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();

        var ex = await Assert.ThrowsAsync<UaConnectionException>(() => UaDiscovery.FindServersAsync($"127.0.0.1:{port}"));

        Assert.Contains($"127.0.0.1:{port}", ex.Message);
    }
}
