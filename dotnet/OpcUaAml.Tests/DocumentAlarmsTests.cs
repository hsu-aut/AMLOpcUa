using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using OpcUaAml.Addressing;
using OpcUaAml.Alarms;
using OpcUaAml.Server;

namespace OpcUaAml.Tests;

/// <summary>
/// The document server raises the alarms the document describes: the
/// object above the watched value is their source, the alarm stands where
/// its element stands, and both simulation and edits of the document move it.
/// </summary>
[Trait("Speed", "Slow")]
public class DocumentAlarmsTests(DiDocument di) : IClassFixture<DiDocument>
{
    private static string TempPki() => Path.Combine(Path.GetTempPath(), "amlopcua-alarm-doc-pki-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>A document of its own with the base library and a drill whose spindle speed has an alarm.</summary>
    private (CAEXDocument Document, InternalElementType Drill, InternalElementType Speed, InternalElementType Alarm) Drill(string limit = "1200")
    {
        var doc = CAEXDocument.LoadFromString(di.Document.XDocument.ToString());
        foreach (var other in doc.CAEXFile.InstanceHierarchy.ToList()) doc.CAEXFile.InstanceHierarchy.RemoveElement(other);
        var ih = doc.CAEXFile.InstanceHierarchy.Append("Plant");
        var drill = ih.InternalElement.Append("Drill");
        var speed = drill.InternalElement.Append("SpindleSpeed");
        var v = speed.Attribute.Append("Value");
        v.AttributeDataType = "xs:double";
        v.Value = "1080";
        var alarm = AlarmElements.Create(speed, AlarmKind.High, 1200, null, 800, "Spindle overloaded", "SpindleSpeedHigh");
        alarm.InternalElement["HighLimit"]!.Attribute["Value"]!.Value = limit;
        return (doc, drill, speed, alarm);
    }

    private static async Task<(AmlServerHost Host, UaClient Client, List<UaEvent> Events, IAsyncDisposable Subscription)> Serve(CAEXDocument doc, bool simulate, string pki)
    {
        var host = await AmlServerHost.StartAsync(doc, new AmlServerOptions
        {
            Port = NodeSetServerFixture.FreePort(),
            PkiRoot = Path.Combine(pki, "server"),
            Simulate = simulate,
            WatchedPace = new SimulationPace(4, 1),
        });
        var client = await UaClient.ConnectAsync(new UaConnectOptions
        {
            EndpointUrl = host.EndpointUrl,
            UseSecurity = false,
            AcceptUntrustedServerCertificates = true,
            PkiRoot = Path.Combine(pki, "client"),
        });
        var events = new List<UaEvent>();
        var subscription = await client.SubscribeEventsAsync(e => { lock (events) events.Add(e); });
        return (host, client, events, subscription);
    }

    private static async Task Close((AmlServerHost Host, UaClient Client, List<UaEvent> Events, IAsyncDisposable Subscription) served, string pki)
    {
        await served.Subscription.DisposeAsync();
        await served.Client.DisposeAsync();
        await served.Host.DisposeAsync();
        try { Directory.Delete(pki, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static async Task<bool> Until(Func<bool> condition, int seconds)
    {
        for (var i = 0; i < seconds * 10 && !condition(); i++) await Task.Delay(100);
        return condition();
    }

    private static bool Active(List<UaEvent> events, string condition)
    {
        lock (events) return events.Any(e => e.ConditionName == condition && e.Active == true);
    }

    [Fact]
    public async Task The_document_server_raises_a_document_alarm()
    {
        var (doc, _, _, _) = Drill();
        var pki = TempPki();
        var served = await Serve(doc, simulate: true, pki);
        try
        {
            Assert.Equal(1, served.Host.Alarms);
            Assert.True(await Until(() => Active(served.Events, "SpindleSpeedHigh"), 20));
            UaEvent raised;
            lock (served.Events) raised = served.Events.First(e => e.ConditionName == "SpindleSpeedHigh" && e.Active == true);
            Assert.Equal("Drill", raised.SourceName);
            Assert.Equal(800, raised.Severity);
            Assert.Equal("Spindle overloaded", raised.Message);

            var drill = new UaNodeAddress("urn:amlopcua:document", UaIdType.String, "Plant/Drill");
            Assert.Contains(await served.Client.BrowseAsync(drill), c => c.BrowseName == "SpindleSpeedHigh");
            lock (served.Events)
                Assert.All(served.Events.GroupBy(e => e.EventIdHex), g => Assert.Single(g));
        }
        finally { await Close(served, pki); }
    }

    [Fact]
    public async Task An_edited_value_raises_it_without_simulation()
    {
        var (doc, _, speed, _) = Drill();
        var pki = TempPki();
        var served = await Serve(doc, simulate: false, pki);
        try
        {
            Assert.False(Active(served.Events, "SpindleSpeedHigh"));

            speed.Attribute["Value"]!.Value = "1300";
            served.Host.RefreshValues();

            Assert.True(await Until(() => Active(served.Events, "SpindleSpeedHigh"), 10));
        }
        finally { await Close(served, pki); }
    }

    [Fact]
    public async Task A_deleted_input_leaves_the_alarm_inactive()
    {
        var (doc, drill, speed, _) = Drill();
        var pki = TempPki();
        var served = await Serve(doc, simulate: false, pki);
        try
        {
            drill.InternalElement.RemoveElement(speed);
            served.Host.RefreshValues();
            await Task.Delay(1000);

            Assert.True(served.Host.StructureChanged);
            Assert.False(Active(served.Events, "SpindleSpeedHigh"));
        }
        finally { await Close(served, pki); }
    }

    [Fact]
    public async Task A_text_limit_is_not_evaluated()
    {
        var (doc, _, speed, _) = Drill(limit: "3,5");
        speed.Attribute["Value"]!.Value = "1300";
        var pki = TempPki();
        var served = await Serve(doc, simulate: false, pki);
        try
        {
            await Task.Delay(1000);

            Assert.Equal(0, served.Host.Alarms);
            Assert.Contains(served.Host.SkippedAlarms, s => s.Contains("SpindleSpeedHigh"));
            Assert.False(Active(served.Events, "SpindleSpeedHigh"));
        }
        finally { await Close(served, pki); }
    }

    [Fact]
    public async Task Other_alarm_classes_stay_objects()
    {
        var (doc, drill, _, _) = Drill();
        var door = Types.TypeInstantiator.Instantiate((SystemUnitFamilyType)doc.FindByPath("[SUC_http://opcfoundation.org/UA/]/[DiscreteAlarmType]")!, "Door").Instance;
        drill.InternalElement.Insert(door, asFirst: false);
        var pki = TempPki();
        var served = await Serve(doc, simulate: false, pki);
        try
        {
            Assert.Equal(1, served.Host.Alarms);
            Assert.Contains(served.Host.SkippedAlarms, s => s.Contains("Door") && s.Contains("DiscreteAlarmType"));
            var door2 = new UaNodeAddress("urn:amlopcua:document", UaIdType.String, "Plant/Drill/Door");
            Assert.Equal("Object", (await served.Client.DescribeAsync(door2)).NodeClass);
        }
        finally { await Close(served, pki); }
    }
}
