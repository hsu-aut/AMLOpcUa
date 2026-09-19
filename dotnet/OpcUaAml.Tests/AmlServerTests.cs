using Aml.Engine.CAEX;
using OpcUaAml.Addressing;
using OpcUaAml.Server;
using OpcUaAml.Types;

namespace OpcUaAml.Tests;

[Trait("Speed", "Slow")]
public class AmlServerTests(DiDocument di) : IClassFixture<DiDocument>
{
    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static string TempPki() => Path.Combine(Path.GetTempPath(), "amlopcua-test-pki-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public async Task Serves_the_instance_hierarchies_and_mirrors_back_to_the_same_structure()
    {
        // A modelled line with values, and an instance of a DI type.
        var ih = di.Hierarchy("Served");
        var line = ih.InternalElement.Append("Line");
        var conveyor = line.InternalElement.Append("Conveyor");
        var speed = conveyor.InternalElement.Append("Speed");
        var v = speed.Attribute.Append("Value");
        v.AttributeDataType = "xs:double";
        v.Value = "1.5";
        var running = conveyor.InternalElement.Append("Running");
        var r = running.Attribute.Append("Value");
        r.AttributeDataType = "xs:boolean";
        r.Value = "true";
        var firmware = TypeInstantiator.Instantiate(di.Type("SoftwareVersionType"), "Firmware").Instance;
        ih.InternalElement.Insert(firmware, asFirst: false);
        AnnexANodeId.Write(ih.InternalElement["Firmware"]!, new UaNodeAddress("http://example.org/Plant/", UaIdType.Numeric, "5001"));

        var pki = TempPki();
        await using var host = await AmlServerHost.StartAsync(di.Document, new AmlServerOptions { Port = FreePort(), PkiRoot = Path.Combine(pki, "server") });
        await using var client = await UaClient.ConnectAsync(new UaConnectOptions
        {
            EndpointUrl = host.EndpointUrl,
            UseSecurity = false,
            AcceptUntrustedServerCertificates = true,
            PkiRoot = Path.Combine(pki, "client"),
        });

        var folder = (await client.BrowseAsync()).Single(i => i.BrowseName == "Served");
        var top = await client.BrowseAsync(folder.Address);
        Assert.Contains(top, i => i.BrowseName == "Line");
        var fw = top.Single(i => i.BrowseName == "Firmware");
        Assert.Equal(new UaNodeAddress("http://example.org/Plant/", UaIdType.Numeric, "5001"), fw.Address);

        var speedNode = new UaNodeAddress("urn:amlopcua:document", UaIdType.String, "Served/Line/Conveyor/Speed");
        var read = await client.ReadAsync(speedNode);
        Assert.True(read.Good, read.Status);
        Assert.Equal("Double", read.DataType);
        Assert.Equal("1.5", read.ValueText);
        Assert.Contains(await client.BrowseAsync(fw.Address), i => i.BrowseName == "SoftwareRevision" && i.NodeClass == "Variable");

        // Back into AML: the structure and the values come back.
        var back = CAEXDocument.New_CAEXDocument();
        var backIh = back.CAEXFile.InstanceHierarchy.Append("Back");
        await AddressSpaceMirror.MirrorAsync(client, folder, backIh, new MirrorOptions { Depth = 5 });
        var served = backIh.InternalElement["Served"]!;
        Assert.Equal("1.5", served.InternalElement["Line"]!.InternalElement["Conveyor"]!.InternalElement["Speed"]!.Attribute["Value"]!.Value);
        Assert.Equal("true", served.InternalElement["Line"]!.InternalElement["Conveyor"]!.InternalElement["Running"]!.Attribute["Value"]!.Value);
        Assert.Equal(
            firmware.InternalElement.Select(c => c.Name).OrderBy(n => n),
            served.InternalElement["Firmware"]!.InternalElement.Select(c => c.Name).OrderBy(n => n));
    }

    [Fact]
    public async Task Served_values_follow_the_document()
    {
        var doc = CAEXDocument.New_CAEXDocument();
        var ih = doc.CAEXFile.InstanceHierarchy.Append("Live");
        var speed = ih.InternalElement.Append("Line").InternalElement.Append("Speed");
        var value = speed.Attribute.Append("Value");
        value.AttributeDataType = "xs:double";
        value.Value = "1.5";

        var pki = TempPki();
        await using var host = await AmlServerHost.StartAsync(doc, new AmlServerOptions { Port = FreePort(), PkiRoot = Path.Combine(pki, "server") });
        using var follow = host.FollowDocument(refresh => refresh(), quiet: TimeSpan.FromMilliseconds(50));
        await using var client = await UaClient.ConnectAsync(new UaConnectOptions
        {
            EndpointUrl = host.EndpointUrl,
            UseSecurity = false,
            AcceptUntrustedServerCertificates = true,
            PkiRoot = Path.Combine(pki, "client"),
        });
        var node = new UaNodeAddress("urn:amlopcua:document", UaIdType.String, "Live/Line/Speed");
        var seen = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        await using (await client.WatchAsync(new[] { node }, r => seen.Enqueue(r.ValueText), publishingIntervalMs: 100))
        {
            value.Value = "2.5";
            for (var i = 0; i < 50 && !seen.Contains("2.5"); i++) await Task.Delay(100);
        }

        Assert.Contains("2.5", seen);
        Assert.Equal("2.5", (await client.ReadAsync(node)).ValueText);
        Assert.False(host.StructureChanged);

        ih.InternalElement.Append("Pump");
        host.RefreshValues();
        Assert.True(host.StructureChanged);
    }

    [Fact]
    public async Task By_default_the_server_is_reachable_from_this_computer_only()
    {
        var doc = CAEXDocument.New_CAEXDocument();
        doc.CAEXFile.InstanceHierarchy.Append("Local");
        var port = FreePort();
        await using var host = await AmlServerHost.StartAsync(doc, new AmlServerOptions { Port = port, PkiRoot = Path.Combine(TempPki(), "server") });

        var listeners = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Where(l => l.Port == port).ToList();
        Assert.NotEmpty(listeners);
        Assert.All(listeners, l => Assert.True(System.Net.IPAddress.IsLoopback(l.Address), $"listens on {l.Address}"));
    }

    [Fact]
    public async Task Offered_to_the_network_the_server_admits_trusted_clients_only()
    {
        var doc = CAEXDocument.New_CAEXDocument();
        var value = doc.CAEXFile.InstanceHierarchy.Append("Net").InternalElement.Append("Speed").Attribute.Append("Value");
        value.AttributeDataType = "xs:int";
        value.Value = "3000000000"; // more than an Int32 holds: served as text, not a failure
        var pki = TempPki();
        var serverPki = Path.Combine(pki, "server");
        await using var host = await AmlServerHost.StartAsync(doc, new AmlServerOptions { Port = FreePort(), PkiRoot = serverPki, Network = true });
        var options = new UaConnectOptions
        {
            EndpointUrl = host.EndpointUrl, UseSecurity = true, AcceptUntrustedServerCertificates = true, PkiRoot = Path.Combine(pki, "client"),
        };

        await Assert.ThrowsAsync<UaConnectionException>(() => UaClient.ConnectAsync(new UaConnectOptions
        {
            EndpointUrl = host.EndpointUrl, UseSecurity = false, AcceptUntrustedServerCertificates = true, PkiRoot = Path.Combine(pki, "client"),
        }));
        await Assert.ThrowsAsync<UaConnectionException>(() => UaClient.ConnectAsync(options));
        var rejected = Assert.Single(AmlServerHost.RejectedClients(serverPki));
        Assert.Contains("AMLOpcUa", rejected.Subject);

        AmlServerHost.TrustClient(rejected, serverPki);
        await using var client = await UaClient.ConnectAsync(options);
        Assert.StartsWith("SignAndEncrypt", client.SecurityMode);
        var read = await client.ReadAsync(new UaNodeAddress("urn:amlopcua:document", UaIdType.String, "Net/Speed"));
        Assert.Equal("3000000000", read.ValueText);
        Assert.Single(AmlServerHost.TrustedClients(serverPki));
    }

    [Fact]
    public async Task A_server_that_goes_away_is_noticed()
    {
        var doc = CAEXDocument.New_CAEXDocument();
        doc.CAEXFile.InstanceHierarchy.Append("Gone");
        var pki = TempPki();
        var host = await AmlServerHost.StartAsync(doc, new AmlServerOptions { Port = FreePort(), PkiRoot = Path.Combine(pki, "server") });
        await using var client = await UaClient.ConnectAsync(new UaConnectOptions
        {
            EndpointUrl = host.EndpointUrl, UseSecurity = false, AcceptUntrustedServerCertificates = true, PkiRoot = Path.Combine(pki, "client"),
        });
        var lost = new TaskCompletionSource();
        client.ReachableChanged += reachable => { if (!reachable) lost.TrySetResult(); };
        Assert.True(client.Reachable);

        await host.DisposeAsync();

        await lost.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(client.Reachable);
    }

    [Fact]
    public void Duplicate_NodeIds_are_made_unique()
    {
        var doc = CAEXDocument.New_CAEXDocument();
        var ih = doc.CAEXFile.InstanceHierarchy.Append("Dup");
        var a = new UaNodeAddress("http://example.org/Plant/", UaIdType.Numeric, "7");
        AnnexANodeId.Write(ih.InternalElement.Append("A"), a);
        AnnexANodeId.Write(ih.InternalElement.Append("B"), a);

        var space = AmlAddressSpace.From(doc, "urn:test");

        var addresses = space.Roots[0].Children.Select(c => c.Address).ToList();
        Assert.Equal(2, addresses.Distinct().Count());
        Assert.Contains("http://example.org/Plant/", space.NamespaceUris);
    }
}
