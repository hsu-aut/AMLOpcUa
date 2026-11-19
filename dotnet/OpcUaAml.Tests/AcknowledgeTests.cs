using OpcUaAml.Server;

namespace OpcUaAml.Tests;

/// <summary>
/// Acknowledging an alarm (OPC 10000-9 Acknowledge) with the EventId of its
/// newest state and a comment; an older EventId has been overtaken and is
/// refused, and a server that is gone is told, not thrown.
/// </summary>
[Trait("Speed", "Slow")]
public class AcknowledgeTests(SlowAlarmPlantFixture plant) : IClassFixture<SlowAlarmPlantFixture>
{
    private static async Task<bool> Until(Func<bool> condition, int seconds)
    {
        for (var i = 0; i < seconds * 10 && !condition(); i++) await Task.Delay(100);
        return condition();
    }

    private static UaEvent? Latest(List<UaEvent> events, string condition)
    {
        lock (events) return events.LastOrDefault(e => e.ConditionName == condition);
    }

    [Fact]
    public async Task Acknowledging_sets_the_acked_state_with_the_comment()
    {
        await using var client = await plant.ConnectAsync();
        var events = new List<UaEvent>();
        await using var subscription = await client.SubscribeEventsAsync(e => { lock (events) events.Add(e); });
        Assert.True(await Until(() => Latest(events, "MagazineEmpty") is { Active: true, Acked: false }, 40));
        var active = Latest(events, "MagazineEmpty")!;

        var result = await client.AcknowledgeAsync(active.ConditionId!, active.EventId, "Bearing checked");

        Assert.True(result.Good, result.Status);
        Assert.True(await Until(() => { lock (events) return events.Any(e => e.ConditionName == "MagazineEmpty" && e.Acked == true && e.Comment == "Bearing checked"); }, 10));
    }

    [Fact]
    public async Task An_older_event_id_is_refused()
    {
        await using var client = await plant.ConnectAsync();
        var events = new List<UaEvent>();
        await using var subscription = await client.SubscribeEventsAsync(e => { lock (events) events.Add(e); });
        Assert.True(await Until(() => { lock (events) return events.Count(e => e.ConditionName == "RobotControllerOffline") >= 2; }, 40));
        UaEvent older;
        lock (events) older = events.First(e => e.ConditionName == "RobotControllerOffline");

        var result = await client.AcknowledgeAsync(older.ConditionId!, older.EventId, "too late");

        Assert.False(result.Good);
        Assert.Equal("BadEventIdUnknown", result.Status);
    }

    [Fact]
    public async Task Acknowledging_after_the_server_stopped_reports_the_status()
    {
        var pki = Path.Combine(Path.GetTempPath(), "amlopcua-ack-pki-" + Guid.NewGuid().ToString("N")[..8]);
        var host = await NodeSetServerHost.StartAsync(new[] { AlarmPlantFixture.Mps500 }, new NodeSetServerOptions
        {
            Port = NodeSetServerFixture.FreePort(),
            PkiRoot = Path.Combine(pki, "server"),
            Simulate = true,
            Alarms = PracticePlantAlarms.For(),
            WatchedPace = new SimulationPace(4, 1),
        });
        await using var client = await UaClient.ConnectAsync(new UaConnectOptions
        {
            EndpointUrl = host.EndpointUrl, UseSecurity = false, AcceptUntrustedServerCertificates = true, PkiRoot = Path.Combine(pki, "client"),
        });
        var events = new List<UaEvent>();
        var subscription = await client.SubscribeEventsAsync(e => { lock (events) events.Add(e); });
        Assert.True(await Until(() => { lock (events) return events.Any(e => e.ConditionId != null); }, 20));
        UaEvent any;
        lock (events) any = events.First(e => e.ConditionId != null);
        await host.DisposeAsync();

        var result = await client.AcknowledgeAsync(any.ConditionId!, any.EventId, "gone");

        Assert.False(result.Good);
        Assert.StartsWith("Bad", result.Status);
        await subscription.DisposeAsync();
        try { Directory.Delete(pki, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Review 2026-11-19: in the practice plant an acknowledgement sent no event;
/// the acknowledged state showed only with the next value change, which the
/// tests above waited for.
/// </summary>
[Trait("Speed", "Slow")]
public class AcknowledgeEventTests(StillAlarmPlantFixture plant) : IClassFixture<StillAlarmPlantFixture>
{
    [Fact]
    public async Task The_acknowledged_state_comes_at_once_without_a_value_change()
    {
        await using var client = await plant.ConnectAsync();
        var events = new List<UaEvent>();
        await using var subscription = await client.SubscribeEventsAsync(e => { lock (events) events.Add(e); });
        UaEvent? active = null;
        for (var i = 0; i < 100 && active == null; i++)
        {
            lock (events) active = events.LastOrDefault(e => e.ConditionName == "MagazineEmpty" && e.Active == true && e.Acked == false);
            if (active == null) await Task.Delay(100);
        }
        Assert.NotNull(active);

        var result = await client.AcknowledgeAsync(active!.ConditionId!, active.EventId, "checked");
        Assert.True(result.Good, result.Status);

        var acknowledged = false;
        for (var i = 0; i < 30 && !acknowledged; i++)
        {
            lock (events) acknowledged = events.Any(e => e.ConditionName == "MagazineEmpty" && e.Active == true && e.Acked == true && e.Comment == "checked");
            if (!acknowledged) await Task.Delay(100);
        }
        Assert.True(acknowledged, "no event of the acknowledgement within 3 s");
    }
}
