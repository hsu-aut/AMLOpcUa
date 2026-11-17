using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using OpcUaAml.Addressing;
using OpcUaAml.Server;

namespace OpcUaAml.Tests;

/// <summary>
/// Keeping values live follows what is bound while it runs: an element bound or
/// copied after it started gets values too, a deleted one is dropped, without
/// switching live off and on (Rainer Drath, 2026-11-17: "Eine Kopie tuts nicht").
/// </summary>
[Trait("Speed", "Slow")]
public class LiveFollowsBindingsTests
{
    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static readonly UaNodeAddress Speed = new("urn:amlopcua:document", UaIdType.String, "Plant/Drill/SpindleSpeed");

    private static async Task<(AmlServerHost Host, UaClient Client, string Pki)> ServeAsync()
    {
        var plant = CAEXDocument.New_CAEXDocument();
        var speed = plant.CAEXFile.InstanceHierarchy.Append("Plant").InternalElement.Append("Drill")
            .InternalElement.Append("SpindleSpeed").Attribute.Append("Value");
        speed.AttributeDataType = "xs:double";
        speed.Value = "1450";
        var pki = Path.Combine(Path.GetTempPath(), "amlopcua-livebind-pki-" + Guid.NewGuid().ToString("N")[..8]);
        var host = await AmlServerHost.StartAsync(plant, new AmlServerOptions { Port = FreePort(), PkiRoot = Path.Combine(pki, "server") });
        var client = await UaClient.ConnectAsync(new UaConnectOptions
        {
            EndpointUrl = host.EndpointUrl, UseSecurity = false, AcceptUntrustedServerCertificates = true, PkiRoot = Path.Combine(pki, "client"),
        });
        return (host, client, pki);
    }

    private static InternalElementType Bound(InternalElementType parent, string name)
    {
        var ie = parent.InternalElement.Append(name);
        AnnexANodeId.Write(ie, Speed);
        ie.Attribute.Append("Value");
        return ie;
    }

    private static async Task<string?> ValueOf(InternalElementType ie)
    {
        for (var i = 0; i < 50 && string.IsNullOrEmpty(ie.Attribute["Value"]?.Value); i++) await Task.Delay(100);
        return ie.Attribute["Value"]?.Value;
    }

    private static async Task WithServer(Func<UaClient, Task> body)
    {
        var (host, client, pki) = await ServeAsync();
        try { await body(client); }
        finally
        {
            await client.DisposeAsync();
            await host.DisposeAsync();
            try { Directory.Delete(pki, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public Task An_element_bound_after_live_started_gets_values() => WithServer(async client =>
    {
        var doc = CAEXDocument.New_CAEXDocument();
        var model = doc.CAEXFile.InstanceHierarchy.Append("Model").InternalElement.Append("Station");
        Bound(model, "SpindleSpeed");
        var live = await LiveValues.FollowAsync(doc, client, write => write());
        try
        {
            var blinker = Bound(model, "SpeedForDisplay");

            var change = await live.FollowChangesAsync();

            Assert.NotNull(change);
            live = change!.Live;
            Assert.Equal(new[] { "SpeedForDisplay" }, change.Added);
            Assert.Empty(change.Removed);
            Assert.Equal(2, live.Count);
            Assert.Equal("1450", await ValueOf(blinker));
        }
        finally
        {
            await live.DisposeAsync();
        }
    });

    [Fact]
    public Task Without_a_change_live_goes_on_as_it_is() => WithServer(async client =>
    {
        var doc = CAEXDocument.New_CAEXDocument();
        Bound(doc.CAEXFile.InstanceHierarchy.Append("Model").InternalElement.Append("Station"), "SpindleSpeed");
        await using var live = await LiveValues.FollowAsync(doc, client, write => write());

        Assert.Null(await live.FollowChangesAsync());
        Assert.Equal(1, live.Count);
    });

    [Fact]
    public Task A_deleted_element_is_dropped_and_the_count_of_written_values_goes_on() => WithServer(async client =>
    {
        var doc = CAEXDocument.New_CAEXDocument();
        var model = doc.CAEXFile.InstanceHierarchy.Append("Model").InternalElement.Append("Station");
        var first = Bound(model, "SpindleSpeed");
        var second = Bound(model, "SpeedForDisplay");
        var live = await LiveValues.FollowAsync(doc, client, write => write());
        try
        {
            await ValueOf(first);
            for (var i = 0; i < 50 && live.Updates < 2; i++) await Task.Delay(100);
            var before = live.Updates;

            second.Remove();
            var change = await live.FollowChangesAsync();

            Assert.NotNull(change);
            live = change!.Live;
            Assert.Equal(new[] { "SpeedForDisplay" }, change.Removed);
            Assert.Equal(1, live.Count);
            Assert.True(live.Updates >= before, $"{live.Updates} < {before}");
        }
        finally
        {
            await live.DisposeAsync();
        }
    });
}
