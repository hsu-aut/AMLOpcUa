using OpcUaAml.Server;

namespace OpcUaAml.Tests;

/// <summary>uaaml check-server: a running server checked against its types from the command line.</summary>
[Trait("Speed", "Slow")]
public class CheckServerCliTests
{
    private static async Task<NodeSetServerHost> ServeAsync(string pki) =>
        await NodeSetServerHost.StartAsync(new[] { Fixtures.Path("servercheck", "Check.NodeSet2.xml") },
            new NodeSetServerOptions { Port = Uaaml.FreePort(), PkiRoot = Path.Combine(pki, "server") });

    [Fact]
    public async Task The_findings_are_listed_and_errors_end_with_exit_code_1()
    {
        using var uaaml = new Uaaml();
        await using var server = await ServeAsync(uaaml.Folder);

        var run = await uaaml.RunAsync("check-server", server.EndpointUrl, "--insecure", "--accept");

        Assert.Equal(1, run.Exit);
        Assert.Contains("SV003 Plant/NoSpeed: Mandatory child 'Speed' of 'MachineType' is missing.", run.Out);
        Assert.Contains("8 error(s), 0 warning(s)", run.Out);
    }

    [Fact]
    public async Task Below_a_node_that_follows_its_type_nothing_is_found()
    {
        using var uaaml = new Uaaml();
        await using var server = await ServeAsync(uaaml.Folder);

        var run = await uaaml.RunAsync("check-server", server.EndpointUrl, "--below", "/Plant/GoodMachine", "--insecure", "--accept");

        Assert.Equal(0, run.Exit);
        Assert.Contains("0 error(s), 0 warning(s)", run.Out);
    }

    // Review of 2026-11-18: a NodeId the server does not have passed as "1 node(s) checked", exit 0.
    [Fact]
    public async Task Below_a_node_the_server_does_not_have_the_check_fails()
    {
        using var uaaml = new Uaaml();
        await using var server = await ServeAsync(uaaml.Folder);

        var run = await uaaml.RunAsync("check-server", server.EndpointUrl, "--below", "nsu=http://example.org/Check/;i=987654", "--insecure", "--accept");

        Assert.NotEqual(0, run.Exit);
        Assert.DoesNotContain("node(s) checked", run.Out);
    }

    // --limit abc or 0 silently became 20000.
    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    public async Task A_limit_that_is_no_number_of_nodes_is_refused(string limit)
    {
        using var uaaml = new Uaaml();

        var run = await uaaml.RunAsync("check-server", "opc.tcp://localhost:1", "--limit", limit);

        Assert.NotEqual(0, run.Exit);
        Assert.Contains("--limit", run.Error + run.Out);
    }

    // serve took --types with --nodeset, and --search without --types, and did nothing with them.
    [Theory]
    [InlineData("--types", "--nodeset", "x.xml", "--types")]
    [InlineData("--search", "doc.aml", "--search", "folder")]
    [InlineData("--port", "doc.aml", "--port", "port")]
    public async Task Serve_refuses_an_option_it_would_not_use(string named, params string[] args)
    {
        using var uaaml = new Uaaml();

        var run = await uaaml.RunAsync(["serve", .. args]);

        Assert.NotEqual(0, run.Exit);
        Assert.Contains(named, run.Error + run.Out);
    }
}
