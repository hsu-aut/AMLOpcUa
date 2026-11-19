using OpcUaAml.Addressing;
using OpcUaAml.Server;

namespace OpcUaAml.Tests;

/// <summary>The practice plant with its fixed alarms, its watched values moving fast so a test does not wait minutes.</summary>
public class AlarmPlantFixture : IAsyncLifetime
{
    /// <summary>How fast the watched values move: fast enough that a test sees an alarm come and go.</summary>
    protected virtual SimulationPace Pace => new(4, 1);

    public NodeSetServerHost Host { get; private set; } = null!;
    public string PkiRoot { get; } = Path.Combine(Path.GetTempPath(), "amlopcua-alarm-pki-" + Guid.NewGuid().ToString("N")[..8]);

    public static string Mps500 => Path.Combine(Repository.Root, "examples", "mps500", "MPS500.NodeSet2.xml");

    public async Task InitializeAsync() =>
        Host = await NodeSetServerHost.StartAsync(new[] { Mps500 }, new NodeSetServerOptions
        {
            Port = NodeSetServerFixture.FreePort(),
            PkiRoot = Path.Combine(PkiRoot, "server"),
            Simulate = true,
            Alarms = PracticePlantAlarms.For(),
            WatchedPace = Pace,
        });

    public async Task DisposeAsync()
    {
        await Host.DisposeAsync();
        try { Directory.Delete(PkiRoot, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public Task<UaClient> ConnectAsync() => UaClient.ConnectAsync(new UaConnectOptions
    {
        EndpointUrl = Host.EndpointUrl,
        UseSecurity = false,
        AcceptUntrustedServerCertificates = true,
        PkiRoot = Path.Combine(PkiRoot, "client"),
    });
}

/// <summary>The practice plant with alarms that stay ten seconds: long enough to acknowledge the newest state.</summary>
public sealed class SlowAlarmPlantFixture : AlarmPlantFixture
{
    protected override SimulationPace Pace => new(20, 10);
}

/// <summary>The practice plant with watched values that do not move during a test: every event of an alarm comes from the alarm itself.</summary>
public sealed class StillAlarmPlantFixture : AlarmPlantFixture
{
    protected override SimulationPace Pace => new(3600, 3600);
}

[Trait("Speed", "Slow")]
public class ServedAlarmsTests(AlarmPlantFixture plant) : IClassFixture<AlarmPlantFixture>
{
    private const string Mps500Uri = "http://hsu-hh.de/UA/MPS500/";

    private static async Task<bool> Until(Func<bool> condition, int seconds)
    {
        for (var i = 0; i < seconds * 10; i++)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }
        return condition();
    }

    [Fact]
    public void The_plant_serves_its_four_alarms() => Assert.Equal(4, plant.Host.Alarms);

    [Fact]
    public async Task The_practice_plant_raises_its_alarms()
    {
        await using var client = await plant.ConnectAsync();
        var events = new List<UaEvent>();
        await using var subscription = await client.SubscribeEventsAsync(e => { lock (events) events.Add(e); });

        List<UaEvent> Spindle() { lock (events) return events.Where(e => e.ConditionName == "SpindleOverload").ToList(); }
        Assert.True(await Until(() => Spindle().Any(e => e.Active == true) && Spindle().SkipWhile(e => e.Active != true).Any(e => e.Active == false), 30),
            "SpindleOverload did not come and go: " + string.Join(", ", Spindle().Select(e => e.Active)));

        var raised = Spindle().First(e => e.Active == true);
        Assert.Equal(800, raised.Severity);
        Assert.False(raised.Acked);
        Assert.Equal("DV_Drill_spindle", raised.SourceName);
        Assert.Equal("Drill spindle overloaded", raised.Message);
        Assert.Equal(new UaNodeAddress(Mps500Uri, UaIdType.Numeric, "1164"), raised.SourceNode);
    }

    [Fact]
    public async Task A_state_alarm_comes_when_its_value_leaves_normal()
    {
        await using var client = await plant.ConnectAsync();
        var events = new List<UaEvent>();
        await using var subscription = await client.SubscribeEventsAsync(e => { lock (events) events.Add(e); });

        Assert.True(await Until(() => { lock (events) return events.Any(e => e.ConditionName == "MagazineEmpty" && e.Active == true); }, 20));
    }

    [Fact]
    public async Task Condition_refresh_lists_the_alarms_still_open()
    {
        await using var first = await plant.ConnectAsync();
        var seen = new List<UaEvent>();
        await using var watching = await first.SubscribeEventsAsync(e => { lock (seen) seen.Add(e); });
        Assert.True(await Until(() => { lock (seen) return seen.Any(e => e.ConditionId != null); }, 20));

        // Never acknowledged, the alarms that came stay open (Retain): a new client gets their last state at once.
        await using var second = await plant.ConnectAsync();
        var refreshed = new List<UaEvent>();
        await using var late = await second.SubscribeEventsAsync(e => { lock (refreshed) refreshed.Add(e); });

        Assert.True(await Until(() =>
        {
            HashSet<string> known;
            lock (seen) known = seen.Select(e => e.EventIdHex).ToHashSet();
            lock (refreshed) return refreshed.Any(e => e.ConditionId != null && known.Contains(e.EventIdHex));
        }, 10), "no event of the refresh repeated a state the first client had seen");
    }

    [Fact]
    public async Task The_plant_keeps_its_hierarchy()
    {
        await using var client = await plant.ConnectAsync();
        var spindle = new UaNodeAddress(Mps500Uri, UaIdType.Numeric, "1164");

        var children = await client.BrowseAsync(spindle);

        Assert.Contains(children, c => c.BrowseName == "Speed");
        Assert.DoesNotContain(children, c => c.BrowseName == "SpindleOverload");
    }
}
