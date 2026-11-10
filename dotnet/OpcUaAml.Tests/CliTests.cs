using Aml.Engine.CAEX;
using OpcUaAml.Import;

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
        var (o, e) = (Console.Out, Console.Error);
        Console.SetOut(TextWriter.Synchronized(output));
        Console.SetError(TextWriter.Synchronized(error));
        OpcUaAml.Tool.Program.PkiFolder = Pki;
        return Task.Run(() =>
        {
            try { return OpcUaAml.Tool.Program.Main(args); }
            finally
            {
                Console.SetOut(o);
                Console.SetError(e);
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
}
