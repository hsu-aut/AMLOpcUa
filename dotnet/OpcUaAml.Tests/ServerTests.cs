using OpcUaAml.Addressing;
using OpcUaAml.Server;

namespace OpcUaAml.Tests;

public class ServerTests(TestServer server) : IClassFixture<TestServer>
{
    private static UaNodeAddress Plant(string id) => new(TestServer.Namespace, UaIdType.String, id);

    private UaConnectOptions Options(bool security = false, bool accept = true) => new()
    {
        EndpointUrl = server.EndpointUrl,
        UseSecurity = security,
        AcceptUntrustedServerCertificates = accept,
        PkiRoot = Path.Combine(server.PkiRoot, "client-" + Guid.NewGuid().ToString("N")[..6]),
    };

    [Fact]
    public async Task Connects_and_reads_the_namespace_table()
    {
        await using var client = await UaClient.ConnectAsync(Options());

        Assert.Equal("http://opcfoundation.org/UA/", client.NamespaceTable[0]);
        Assert.Contains(TestServer.Namespace, client.NamespaceTable);
    }

    [Fact]
    public async Task Browses_from_the_objects_folder_down()
    {
        await using var client = await UaClient.ConnectAsync(Options());

        var top = await client.BrowseAsync();
        var plant = Assert.Single(top, i => i.BrowseName == "Plant");
        Assert.Equal(Plant("Plant"), plant.Address);

        var pump = Assert.Single(await client.BrowseAsync(plant.Address), i => i.BrowseName == "Pump1");
        Assert.Equal("Pump1", pump.BrowseName);
        Assert.Equal("Object", pump.NodeClass);

        var children = await client.BrowseAsync(pump.Address);
        Assert.Equal(new[] { "Label", "Motor", "Running", "Speed" }, children.Select(c => c.BrowseName).OrderBy(n => n));
        var speed = children.Single(c => c.BrowseName == "Speed");
        Assert.Equal("Variable", speed.NodeClass);
        Assert.Equal("HasComponent", speed.ReferenceType);
        Assert.Equal(new UaNodeAddress("http://opcfoundation.org/UA/", UaIdType.Numeric, "63"), speed.TypeDefinition);
    }

    [Fact]
    public async Task Reads_values_of_several_types_in_one_request()
    {
        await using var client = await UaClient.ConnectAsync(Options());

        var results = await client.ReadManyAsync(new[]
        {
            Plant("Plant.Pump1.Speed"),
            Plant("Plant.Pump1.Running"),
            Plant("Plant.Pump1.Label"),
            Plant("Plant.Pump1.Motor.Temperature"),
            Plant("Plant.Pump1.Motor.Samples"),
        });

        Assert.All(results, r => Assert.True(r.Good, r.Status));
        Assert.Equal(new[] { "12.5", "true", "Pump 1", "42", "1 2 3" }, results.Select(r => r.ValueText));
        Assert.Equal("Double", results[0].DataType);
    }

    [Fact]
    public async Task An_unknown_node_is_a_bad_result_not_an_exception()
    {
        await using var client = await UaClient.ConnectAsync(Options());

        var results = await client.ReadManyAsync(new[]
        {
            Plant("Plant.Pump1.Nothing"),
            new UaNodeAddress("http://not.on.this/server/", UaIdType.Numeric, "1"),
            Plant("Plant.Pump1.Speed"),
        });

        Assert.False(results[0].Good);
        Assert.Contains("BadNodeIdUnknown", results[0].Status);
        Assert.False(results[1].Good);
        Assert.Contains("namespace table", results[1].Status);
        Assert.True(results[2].Good);
    }

    [Fact]
    public async Task An_untrusted_server_certificate_is_refused_with_a_hint()
    {
        var ex = await Assert.ThrowsAsync<UaConnectionException>(() => UaClient.ConnectAsync(Options(security: true, accept: false)));

        Assert.Contains("not trusted", ex.Message);
    }

    [Fact]
    public async Task A_secured_session_works_once_the_certificate_is_accepted()
    {
        await using var client = await UaClient.ConnectAsync(Options(security: true, accept: true));

        Assert.Contains("SignAndEncrypt", client.SecurityMode);
        Assert.True((await client.ReadAsync(Plant("Plant.Pump1.Speed"))).Good);
    }

    [Fact]
    public async Task No_server_is_a_connection_error()
    {
        var options = new UaConnectOptions { EndpointUrl = "opc.tcp://localhost:1/none", UseSecurity = false, OperationTimeoutMs = 2000 };

        await Assert.ThrowsAsync<UaConnectionException>(() => UaClient.ConnectAsync(options));
    }

    [Fact]
    public async Task Watching_delivers_changing_values_until_disposed()
    {
        await using var client = await UaClient.ConnectAsync(Options());
        var seen = new System.Collections.Concurrent.ConcurrentQueue<UaReadResult>();

        var watch = await client.WatchAsync(new[] { Plant("Plant.Counter") }, seen.Enqueue, publishingIntervalMs: 100);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (seen.Count < 3 && DateTime.UtcNow < deadline) await Task.Delay(50);
        await watch.DisposeAsync();

        Assert.True(seen.Count >= 3, $"only {seen.Count} notification(s)");
        var values = seen.Select(r => uint.Parse(r.ValueText!)).ToList();
        Assert.True(values.Last() > values.First());
        Assert.All(seen, r => Assert.True(r.Good));
    }
}
