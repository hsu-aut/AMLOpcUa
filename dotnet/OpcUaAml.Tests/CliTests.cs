using Aml.Engine.CAEX;
using OpcUaAml.Import;
using OpcUaAml.Server;

namespace OpcUaAml.Tests;

/// <summary>
/// uaaml run in the test process: its output and errors captured, its
/// certificates in a folder of the test's own, never the user's.
/// </summary>
internal sealed class Uaaml : IDisposable
{
    public string Folder { get; } = Directory.CreateTempSubdirectory("uaaml-cli-").FullName;

    public string Pki => Path.Combine(Folder, "pki-root");

    public sealed record Run(int Exit, string Out, string Error);

    /// <summary>What uaaml reads from its input; nothing (at its end at once) by default, as from a script.</summary>
    public string Input { get; set; } = "";

    /// <summary>Runs uaaml with these arguments to its end.</summary>
    public async Task<Run> RunAsync(params string[] args)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var exit = await StartAsync(output, error, args);
        return new Run(exit, output.ToString(), error.ToString());
    }

    /// <summary>Starts uaaml; its output goes to the writers given while it runs.</summary>
    public Task<int> StartAsync(TextWriter output, TextWriter error, params string[] args)
    {
        var (o, e, i) = (Console.Out, Console.Error, Console.In);
        Console.SetOut(TextWriter.Synchronized(output));
        Console.SetError(TextWriter.Synchronized(error));
        Console.SetIn(new StringReader(Input));
        OpcUaAml.Tool.Program.PkiFolder = Pki;
        return Task.Run(() =>
        {
            try { return OpcUaAml.Tool.Program.Main(args); }
            finally
            {
                Console.SetOut(o);
                Console.SetError(e);
                Console.SetIn(i);
                OpcUaAml.Tool.Program.PkiFolder = null;
            }
        });
    }

    /// <summary>A document saved in the test's folder.</summary>
    public string Save(CAEXDocument doc, string name = "doc.aml")
    {
        var path = Path.Combine(Folder, name);
        AmlFiles.Save(doc, path);
        return path;
    }

    public static int FreePort() => NodeSetServerFixture.FreePort();

    public void Dispose()
    {
        try { Directory.Delete(Folder, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

[Trait("Speed", "Slow")]
public class CliTests
{
    private static CAEXDocument Plant()
    {
        var doc = CAEXDocument.New_CAEXDocument();
        var speed = doc.CAEXFile.InstanceHierarchy.Append("Plant").InternalElement.Append("Speed").Attribute.Append("Value");
        speed.AttributeDataType = "xs:double";
        speed.Value = "1.5";
        return doc;
    }

    [Fact]
    public async Task Serve_on_a_port_in_use_says_so_in_a_sentence()
    {
        using var uaaml = new Uaaml();
        var file = uaaml.Save(Plant());
        var taken = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        taken.Start();
        try
        {
            var port = ((System.Net.IPEndPoint)taken.LocalEndpoint).Port;

            var run = await uaaml.RunAsync("serve", file, "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture));

            Assert.Equal(1, run.Exit);
            Assert.Contains($"error: Port {port} is in use", run.Error);
            Assert.Contains("--port", run.Error);
            Assert.DoesNotContain("   at ", run.Error);
        }
        finally
        {
            taken.Stop();
        }
    }

    private static string Number(int n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task The_clients_of_the_NodeSet_server_are_trusted_with_nodeset()
    {
        // The NodeSet server keeps its certificates apart from the document server's.
        using var uaaml = new Uaaml();
        var serverPki = Path.Combine(uaaml.Pki, "pki-nodeset-server");
        await using var host = await NodeSetServerHost.StartAsync(new[] { DiTestServer.DiPath },
            new NodeSetServerOptions { Port = Uaaml.FreePort(), PkiRoot = serverPki, Network = true });
        var options = new UaConnectOptions
        {
            EndpointUrl = host.EndpointUrl, UseSecurity = true, AcceptUntrustedServerCertificates = true, PkiRoot = Path.Combine(uaaml.Folder, "client"),
        };
        await Assert.ThrowsAsync<UaConnectionException>(() => UaClient.ConnectAsync(options));
        var refused = Assert.Single(AmlServerHost.RejectedClients(serverPki));

        var list = await uaaml.RunAsync("clients", "--nodeset");
        var trust = await uaaml.RunAsync("clients", "--nodeset", "--trust", refused.Thumbprint[..8]);

        Assert.True(list.Exit == 0, list.Error);
        Assert.Contains($"refused  {refused.Thumbprint}", list.Out);
        Assert.True(trust.Exit == 0, trust.Error);
        Assert.Contains($"trusted  {refused.Thumbprint}", trust.Out);
        await using var client = await UaClient.ConnectAsync(options);
    }

    [Fact]
    public async Task Serving_a_NodeSet_to_the_network_names_the_command_for_its_clients()
    {
        using var uaaml = new Uaaml { Input = "\n" };

        var run = await uaaml.RunAsync("serve", "--nodeset", DiTestServer.DiPath, "--port", Number(Uaaml.FreePort()), "--network");

        Assert.True(run.Exit == 0, run.Error);
        Assert.Contains("'uaaml clients --nodeset'", run.Out);
    }
}
