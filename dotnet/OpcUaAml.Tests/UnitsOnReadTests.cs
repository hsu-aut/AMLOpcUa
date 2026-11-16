using Aml.Engine.CAEX;
using OpcUaAml.Addressing;
using OpcUaAml.Server;

namespace OpcUaAml.Tests;

/// <summary>
/// Values read from a server bring their unit along: where the server states
/// one (EngineeringUnits) and the Value attribute has none yet, it becomes the
/// attribute's CAEX Unit. A unit the model gives already stays.
/// </summary>
[Trait("Speed", "Slow")]
public class UnitsOnReadTests
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

    /// <summary>A plant whose spindle speed is in 1/min, served; and a client on it.</summary>
    private static async Task<(AmlServerHost Host, UaClient Client, string Pki)> ServeAsync()
    {
        var plant = CAEXDocument.New_CAEXDocument();
        var speed = plant.CAEXFile.InstanceHierarchy.Append("Plant").InternalElement.Append("Drill")
            .InternalElement.Append("SpindleSpeed").Attribute.Append("Value");
        speed.AttributeDataType = "xs:double";
        speed.Value = "1450";
        speed.Unit = "1/min";
        var pki = Path.Combine(Path.GetTempPath(), "amlopcua-units-pki-" + Guid.NewGuid().ToString("N")[..8]);
        var host = await AmlServerHost.StartAsync(plant, new AmlServerOptions { Port = FreePort(), PkiRoot = Path.Combine(pki, "server") });
        var client = await UaClient.ConnectAsync(new UaConnectOptions
        {
            EndpointUrl = host.EndpointUrl, UseSecurity = false, AcceptUntrustedServerCertificates = true, PkiRoot = Path.Combine(pki, "client"),
        });
        return (host, client, pki);
    }

    /// <summary>A model of one's own with two elements bound to the speed, one with a unit of its own.</summary>
    private static (CAEXDocument Doc, AttributeType Plain, AttributeType Own) Bound()
    {
        var doc = CAEXDocument.New_CAEXDocument();
        var station = doc.CAEXFile.InstanceHierarchy.Append("Model").InternalElement.Append("Station");
        var plain = station.InternalElement.Append("SpindleSpeed");
        AnnexANodeId.Write(plain, Speed);
        var own = station.InternalElement.Append("SpeedForDisplay");
        AnnexANodeId.Write(own, Speed);
        var ownValue = own.Attribute.Append("Value");
        ownValue.Unit = "rpm";
        return (doc, plain.Attribute.Append("Value"), ownValue);
    }

    [Fact]
    public async Task Reading_values_once_writes_the_unit_where_the_model_has_none()
    {
        var (host, client, pki) = await ServeAsync();
        try
        {
            var (doc, plain, own) = Bound();

            await ValueSnapshot.ApplyAsync(doc, client);

            Assert.Equal("1450", plain.Value);
            Assert.Equal("min⁻¹", plain.Unit);
            Assert.Equal("rpm", own.Unit);
        }
        finally
        {
            await client.DisposeAsync();
            await host.DisposeAsync();
            try { Directory.Delete(pki, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Keeping_values_live_writes_the_unit_when_it_starts()
    {
        var (host, client, pki) = await ServeAsync();
        try
        {
            var (doc, plain, own) = Bound();

            await using (await LiveValues.FollowAsync(doc, client, write => write()))
            {
                Assert.Equal("min⁻¹", plain.Unit);
                Assert.Equal("rpm", own.Unit);
            }
        }
        finally
        {
            await client.DisposeAsync();
            await host.DisposeAsync();
            try { Directory.Delete(pki, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Taking_nodes_writes_the_unit_of_each_variable()
    {
        var (host, client, pki) = await ServeAsync();
        try
        {
            var doc = CAEXDocument.New_CAEXDocument();
            var ih = doc.CAEXFile.InstanceHierarchy.Append("Taken");
            var plant = (await client.BrowseAsync()).Single(i => i.BrowseName == "Plant");

            await AddressSpaceMirror.MirrorAsync(client, plant, ih);

            var speed = ih.InternalElement["Plant"]!.InternalElement["Drill"]!.InternalElement["SpindleSpeed"]!;
            Assert.Equal("min⁻¹", speed.Attribute["Value"]!.Unit);
        }
        finally
        {
            await client.DisposeAsync();
            await host.DisposeAsync();
            try { Directory.Delete(pki, recursive: true); } catch (IOException) { }
        }
    }
}
