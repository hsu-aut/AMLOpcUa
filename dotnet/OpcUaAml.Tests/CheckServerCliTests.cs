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
}
