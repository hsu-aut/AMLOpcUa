using OpcUaAml.Addressing;
using OpcUaAml.Server;

namespace OpcUaAml.Tests;

/// <summary>
/// Writing a value into a running server and calling a method of it, from
/// what a user types: the client looks up the data types and argument lists
/// itself, refuses what the server does not let it write, and reports the
/// server's answer.
/// </summary>
[Trait("Speed", "Slow")]
public class WriteCallTests : IAsyncLifetime
{
    private const string Ns = "http://example.org/WriteCall/";
    private NodeSetServerHost _host = null!;
    private UaClient _client = null!;

    private static UaNodeAddress Node(uint id) => new(Ns, UaIdType.Numeric, id.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static readonly UaNodeAddress Machine = Node(1000), Running = Node(1001), Count = Node(1002), Speed = Node(1003),
        Label = Node(1004), State = Node(1005), Limits = Node(1006), SerialNumber = Node(1007), Scale = Node(2001);

    public async Task InitializeAsync()
    {
        var pki = Path.Combine(Path.GetTempPath(), "amlopcua-test-pki-" + Guid.NewGuid().ToString("N")[..8]);
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        _host = await NodeSetServerHost.StartAsync(new[] { Fixtures.Path("writecall", "WriteCall.NodeSet2.xml") },
            new NodeSetServerOptions { Port = port, PkiRoot = Path.Combine(pki, "server") });
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
    public async Task A_variable_says_its_type_and_whether_it_may_be_written()
    {
        var speed = await _client.DescribeVariableAsync(Speed);
        var state = await _client.DescribeVariableAsync(State);
        var serial = await _client.DescribeVariableAsync(SerialNumber);

        Assert.Equal(("Double", "Double", -1, true), (speed.DataType, speed.BuiltInType, speed.ValueRank, speed.Writable));
        Assert.Equal(("ServerState", "Enumeration"), (state.DataType, state.BuiltInType));
        Assert.False(serial.Writable);
    }

    [Theory]
    [InlineData(1001u, "true", "true")]
    [InlineData(1002u, "42", "42")]
    [InlineData(1003u, "2,75", "2.75")]
    [InlineData(1004u, "second label", "second label")]
    [InlineData(1005u, "3", "3")]
    [InlineData(1006u, "1 2.5 7", "1 2.5 7")]
    public async Task A_typed_value_is_written_and_read_back(uint id, string text, string readBack)
    {
        var result = await _client.WriteValueAsync(Node(id), text);

        Assert.True(result.Good, result.Status);
        Assert.Equal(readBack, (await _client.ReadAsync(Node(id))).ValueText);
    }

    // Review of 2026-11-18: texts with spaces fell apart, a Byte array came back
    // as Base64 that could not be written again. (That a Byte array goes out as
    // Byte[], not ByteString, ValueEntryTests shows: this server keeps a written
    // byte[] without its type and reads it back as a ByteString either way.)
    [Theory]
    [InlineData(1008u, "Byte", "7 8 9")]
    [InlineData(1009u, "String", "\"Line 1\" \"Line 2\" \"\"")]
    public async Task An_array_reads_back_as_it_was_typed(uint id, string type, string text)
    {
        var result = await _client.WriteValueAsync(Node(id), text);
        var read = await _client.ReadAsync(Node(id));

        Assert.True(result.Good, result.Status);
        Assert.Equal(text, ValueEntry.ToText(type, 1, read.Value));
    }

    [Fact]
    public async Task A_variable_the_server_lets_only_be_read_is_not_written()
    {
        var result = await _client.WriteValueAsync(SerialNumber, "SN-2");

        Assert.False(result.Good);
        Assert.Equal("BadNotWritable", result.Status);
        Assert.Equal("SN-1", (await _client.ReadAsync(SerialNumber)).ValueText);
    }

    [Fact]
    public async Task A_text_that_is_no_value_of_the_type_is_not_written()
    {
        await Assert.ThrowsAsync<ValueEntryException>(() => _client.WriteValueAsync(Count, "many"));

        Assert.Equal("0", (await _client.ReadAsync(Count)).ValueText);
    }

    [Fact]
    public async Task A_method_lists_its_arguments_with_their_types()
    {
        var scale = await _client.DescribeMethodAsync(Scale);

        var input = Assert.Single(scale.Inputs);
        Assert.Equal(("Factor", "Double", "Double", "How much to scale by"), (input.Name, input.DataType, input.BuiltInType, input.Description));
        Assert.Equal("Result", Assert.Single(scale.Outputs).Name);
    }

    [Fact]
    public async Task A_method_without_an_implementation_answers_with_the_servers_status()
    {
        var result = await _client.CallAsync(Machine, Scale, ["2"]);

        Assert.False(result.Good);
        Assert.StartsWith("Bad", result.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_argument_that_is_no_value_of_its_type_names_the_argument()
    {
        var ex = await Assert.ThrowsAsync<ValueEntryException>(() => _client.CallAsync(Machine, Scale, ["twice"]));

        Assert.StartsWith("Factor: ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_published_NodeSet_is_read_through_its_file_methods()
    {
        // The server publishes the NodeSet it serves as a file (OPC 10000-5,
        // NamespaceFile) whose Open, Read and Close it implements.
        var (file, open, read, close) = await NamespaceFileAsync();

        var opened = await _client.CallAsync(file, open, ["1"]);
        var handle = Assert.Single(opened.Outputs).Value;
        var data = await _client.CallAsync(file, read, [handle, "64"]);
        var closed = await _client.CallAsync(file, close, [handle]);
        var refused = await _client.CallAsync(file, open, ["2"]);

        Assert.True(opened.Good, opened.Status);
        Assert.True(data.Good, data.Status);
        Assert.Equal("Data", Assert.Single(data.Outputs).Name);
        Assert.False(string.IsNullOrEmpty(data.Outputs[0].Value));
        Assert.True(closed.Good, closed.Status);
        Assert.Equal("BadNotWritable", refused.Status);
    }

    private async Task<(UaNodeAddress File, UaNodeAddress Open, UaNodeAddress Read, UaNodeAddress Close)> NamespaceFileAsync()
    {
        var server = new UaNodeAddress("http://opcfoundation.org/UA/", UaIdType.Numeric, "2253");
        var namespaces = (await _client.BrowseAsync(server)).Single(i => i.BrowseName == "Namespaces");
        var metadata = (await _client.BrowseAsync(namespaces.Address)).Single(i => i.BrowseName == Ns);
        var file = (await _client.BrowseAsync(metadata.Address)).Single(i => i.BrowseName == "NamespaceFile");
        var methods = await _client.BrowseAsync(file.Address);
        UaNodeAddress M(string name) => methods.Single(m => m.BrowseName == name).Address;
        return (file.Address, M("Open"), M("Read"), M("Close"));
    }
}
