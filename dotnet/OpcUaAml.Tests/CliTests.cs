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
public class CliTests(TestServer server) : IClassFixture<TestServer>
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

    [Fact]
    public async Task Serve_keeps_serving_when_its_input_is_at_its_end()
    {
        // Started by a script, a scheduled task or another program, the input is at
        // its end at once; serve stopped then, before any client came.
        using var uaaml = new Uaaml();
        var file = uaaml.Save(Plant());
        var port = Uaaml.FreePort();
        var (output, error) = (new StringWriter(), new StringWriter());

        var run = uaaml.StartAsync(output, error, "serve", file, "--port", Number(port), "--for", "5");
        for (var i = 0; i < 300 && !run.IsCompleted && !output.ToString().Contains("Serving", StringComparison.Ordinal); i++) await Task.Delay(100);
        await Task.Delay(1000);

        Assert.False(run.IsCompleted, $"serve stopped: {output}{error}");
        using (var tcp = new System.Net.Sockets.TcpClient()) await tcp.ConnectAsync(System.Net.IPAddress.Loopback, port);
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Contains("Ctrl+C", output.ToString());
    }

    [Fact]
    public async Task A_document_asking_for_no_security_needs_insecure()
    {
        // A document from elsewhere names its servers and how to reach them; that
        // it says "None" is no reason to connect unsecured without being told to.
        using var uaaml = new Uaaml();
        var doc = CAEXDocument.New_CAEXDocument();
        var machine = doc.CAEXFile.InstanceHierarchy.Append("Line").InternalElement.Append("Machine");
        var source = machine.InternalElement.Append("Plant server");
        source.Attribute.Append("EndpointURL").Value = server.EndpointUrl;
        source.Attribute.Append("MessageSecurityMode").Value = "None";
        OpcUaAml.Addressing.BprDataVariable.Write(machine, "Temperature",
            new OpcUaAml.Addressing.UaNodeAddress(TestServer.Namespace, OpcUaAml.Addressing.UaIdType.String, "Plant.Pump1.Motor.Temperature"), source);
        var file = uaaml.Save(doc);

        var refused = await uaaml.RunAsync("snapshot", file);
        var unchanged = AmlFiles.Load(file);
        var allowed = await uaaml.RunAsync("snapshot", file, "--insecure");

        Assert.Equal(1, refused.Exit);
        Assert.Contains("--insecure", refused.Error);
        Assert.Contains("Plant server", refused.Error);
        Assert.Null(unchanged.CAEXFile.InstanceHierarchy["Line"]!.InternalElement["Machine"]!.Attribute["Temperature"]!.Value);
        Assert.True(allowed.Exit == 0, allowed.Error);
        Assert.Equal("42", AmlFiles.Load(file).CAEXFile.InstanceHierarchy["Line"]!.InternalElement["Machine"]!.Attribute["Temperature"]!.Value);
    }

    [Fact]
    public async Task The_types_of_a_server_go_into_a_CAEX_2_15_document_converted_first()
    {
        // As import --into does: the OPC UA libraries need CAEX 3.0.
        using var uaaml = new Uaaml();
        await using var host = await NodeSetServerHost.StartAsync(new[] { DiTestServer.DiPath },
            new NodeSetServerOptions { Port = Uaaml.FreePort(), PkiRoot = Path.Combine(uaaml.Folder, "server") });
        var old = uaaml.Save(CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX2_15), "old.aml");
        var converted = Path.Combine(uaaml.Folder, "new.aml");

        var run = await uaaml.RunAsync("nodeset", host.EndpointUrl, Fixtures.DiUri, "--into", old, "-o", converted, "--insecure", "--accept");

        Assert.True(run.Exit == 0, run.Error);
        Assert.Contains("CAEX 2.15", run.Error);
        var doc = AmlFiles.Load(converted);
        Assert.Equal("3.0", doc.CAEXFile.SchemaVersion);
        Assert.NotNull(doc.CAEXFile.SystemUnitClassLib["SUC_" + Fixtures.DiUri]);
    }

    private const string PasswordVariable = "AMLOPCUA_PASSWORD";

    /// <summary>Runs with the password variable set as given, and puts it back afterwards.</summary>
    private static async Task<T> WithPassword<T>(string? password, Func<Task<T>> run)
    {
        var before = Environment.GetEnvironmentVariable(PasswordVariable);
        Environment.SetEnvironmentVariable(PasswordVariable, password);
        try { return await run(); }
        finally { Environment.SetEnvironmentVariable(PasswordVariable, before); }
    }

    [Fact]
    public async Task A_user_logs_in_with_the_password_from_the_environment_never_from_the_command_line()
    {
        using var uaaml = new Uaaml();

        // Unsecured the password would travel in clear text, so the client refuses:
        // which shows that the user name reached it.
        var run = await WithPassword("secret", () => uaaml.RunAsync("read", server.EndpointUrl, "/Plant/Pump1/Speed", "--insecure", "--accept", "--user", "operator"));
        var onTheLine = await WithPassword("secret", () => uaaml.RunAsync("read", server.EndpointUrl, "/Plant/Pump1/Speed", "--accept", "--user", "operator", "--password", "secret"));

        Assert.Equal(1, run.Exit);
        Assert.Contains("clear text", run.Error);
        Assert.DoesNotContain("Password for", run.Error);
        Assert.Equal(1, onTheLine.Exit);
        Assert.Contains("Unknown option '--password'", onTheLine.Error);
    }

    [Fact]
    public async Task Without_the_environment_the_password_is_asked_for()
    {
        using var uaaml = new Uaaml { Input = "secret\n" };

        var run = await WithPassword(null, () => uaaml.RunAsync("browse", server.EndpointUrl, "--insecure", "--accept", "--user", "operator"));

        Assert.Contains("Password for operator: ", run.Error);
        Assert.Contains("clear text", run.Error);
    }
}
