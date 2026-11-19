using Opc.Ua;
using OpcUaAml.Addressing;
using OpcUaAml.Server;

namespace OpcUaAml.Tests;

/// <summary>
/// Events of a server as the client receives them: the fields the event
/// filter selects become a <see cref="UaEvent"/>, and a subscription at the
/// Server object is accepted by a server that has no alarms at all.
/// </summary>
public class ClientEventsTests
{
    private static NamespaceTable Table()
    {
        var table = new NamespaceTable();
        table.Append("urn:plant");
        return table;
    }

    /// <summary>The fields in the order of <see cref="UaClient.EventFields"/>.</summary>
    private static List<Variant> Fields(params object?[] values) => values.Select(v => new Variant(v)).ToList();

    [Fact]
    public void Fields_become_an_event()
    {
        var time = new DateTime(2026, 11, 19, 10, 41, 12, DateTimeKind.Utc);
        var fields = Fields(
            new byte[] { 1, 2, 3 },
            ObjectTypeIds.ExclusiveLevelAlarmType,
            new NodeId("Drill", 1),
            "Drill",
            time,
            new LocalizedText("Spindle overloaded"),
            (ushort)800,
            new NodeId("Drill/SpindleHigh", 1),
            "SpindleHigh",
            true,
            false,
            true,
            new LocalizedText("Bearing checked"),
            "operator");

        var e = UaClient.ToEvent(fields, Table())!;

        Assert.Equal("010203", e.EventIdHex);
        Assert.Equal(new UaNodeAddress("http://opcfoundation.org/UA/", UaIdType.Numeric, "9482"), e.EventType);
        Assert.Equal(new UaNodeAddress("urn:plant", UaIdType.String, "Drill"), e.SourceNode);
        Assert.Equal("Drill", e.SourceName);
        Assert.Equal(time, e.Time);
        Assert.Equal("Spindle overloaded", e.Message);
        Assert.Equal(800, e.Severity);
        Assert.Equal(new UaNodeAddress("urn:plant", UaIdType.String, "Drill/SpindleHigh"), e.ConditionId);
        Assert.Equal("SpindleHigh", e.ConditionName);
        Assert.True(e.Active);
        Assert.False(e.Acked);
        Assert.True(e.Retain);
        Assert.Equal("Bearing checked", e.Comment);
        Assert.Equal("operator", e.ClientUserId);
    }

    [Fact]
    public void An_event_without_a_condition_has_no_condition_fields()
    {
        var fields = Fields(new byte[] { 9 }, ObjectTypeIds.AuditEventType, ObjectIds.Server, "Server",
            DateTime.UtcNow, new LocalizedText("Session opened"), (ushort)100, null, null, null, null, null, null, null);

        var e = UaClient.ToEvent(fields, Table())!;

        Assert.Null(e.ConditionId);
        Assert.Null(e.Active);
        Assert.Null(e.Acked);
        Assert.Null(e.Retain);
    }

    [Fact]
    public void A_refresh_marker_is_no_event()
    {
        foreach (var marker in new[] { ObjectTypeIds.RefreshStartEventType, ObjectTypeIds.RefreshEndEventType })
        {
            var fields = Fields(new byte[] { 9 }, marker, ObjectIds.Server, "Server",
                DateTime.UtcNow, new LocalizedText(""), (ushort)1, null, null, null, null, null, null, null);

            Assert.Null(UaClient.ToEvent(fields, Table()));
        }
    }

    [Fact]
    public void An_event_without_an_id_is_no_event()
    {
        var fields = Fields(null, ObjectTypeIds.BaseEventType, ObjectIds.Server, "Server",
            DateTime.UtcNow, new LocalizedText("x"), (ushort)1, null, null, null, null, null, null, null);

        Assert.Null(UaClient.ToEvent(fields, Table()));
    }
}

[Trait("Speed", "Slow")]
public class ClientEventsServerTests(NodeSetServerFixture fixture) : IClassFixture<NodeSetServerFixture>
{
    private Task<UaClient> ConnectAsync() => UaClient.ConnectAsync(new UaConnectOptions
    {
        EndpointUrl = fixture.Host.EndpointUrl,
        UseSecurity = false,
        AcceptUntrustedServerCertificates = true,
        PkiRoot = Path.Combine(fixture.PkiRoot, "client"),
    });

    [Fact]
    public async Task The_server_name_is_its_application_name()
    {
        await using var client = await ConnectAsync();

        Assert.Equal("AMLOpcUa NodeSet server", client.ServerName);
    }

    [Fact]
    public async Task A_server_without_alarms_accepts_the_subscription()
    {
        await using var client = await ConnectAsync();
        var received = new List<UaEvent>();

        var subscription = await client.SubscribeEventsAsync(e => { lock (received) received.Add(e); });
        await subscription.DisposeAsync();

        lock (received) Assert.DoesNotContain(received, e => e.ConditionId != null);
    }
}
