using System.Globalization;
using OpcUaAml.Addressing;
using OpcUaAml.Server;

namespace OpcUaAml.Tests;

/// <summary>
/// The events and alarms a server sent, kept as a history: each change a new
/// row, the newest first, filtered to the alarms still open, and written as
/// CSV for the documentation of a plant.
/// </summary>
public class EventHistoryTests
{
    private static readonly UaNodeAddress Spindle = new("urn:plant", UaIdType.String, "Drill/SpindleHigh");
    private static readonly UaNodeAddress Magazine = new("urn:plant", UaIdType.String, "Magazine/Empty");
    private static readonly DateTime T0 = new(2026, 11, 19, 10, 41, 12, DateTimeKind.Utc);
    private static int _next;

    private static UaEvent Alarm(UaNodeAddress condition, bool active, bool acked, bool retain, DateTime time, ushort severity = 800,
        string message = "Spindle overloaded", string? comment = null, string? user = null) =>
        new(NewId(), new UaNodeAddress("http://opcfoundation.org/UA/", UaIdType.Numeric, "9482"), null, "Drill", time, message, severity,
            condition, condition.Identifier, active, acked, retain, comment, user);

    private static UaEvent Plain(DateTime time, ushort severity = 100, string message = "Session opened") =>
        new(NewId(), new UaNodeAddress("http://opcfoundation.org/UA/", UaIdType.Numeric, "2041"), null, "Server", time, message, severity,
            null, null, null, null, null, null, null);

    private static byte[] NewId() => BitConverter.GetBytes(Interlocked.Increment(ref _next));

    private static CultureInfo Semicolons()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.TextInfo.ListSeparator = ";";
        return culture;
    }

    [Fact]
    public void Each_change_is_a_new_row_newest_first()
    {
        var history = new EventHistory();

        history.Add(Alarm(Spindle, active: true, acked: false, retain: true, T0), "Plant");
        history.Add(Alarm(Spindle, active: true, acked: true, retain: true, T0.AddSeconds(5)), "Plant");

        Assert.Equal(2, history.Count);
        Assert.Equal(true, history.Rows[0].Event!.Acked);
        Assert.Equal(false, history.Rows[1].Event!.Acked);
    }

    [Fact]
    public void Only_open_alarms_shows_the_last_state_of_each_condition_while_it_is_retained()
    {
        var history = new EventHistory();
        history.Add(Alarm(Spindle, active: true, acked: false, retain: true, T0), "Plant");
        history.Add(Alarm(Magazine, active: true, acked: false, retain: true, T0.AddSeconds(1)), "Plant");
        history.Add(Alarm(Magazine, active: false, acked: true, retain: false, T0.AddSeconds(2)), "Plant");
        history.Add(Plain(T0.AddSeconds(3)), "Plant");

        var open = history.Filter(onlyOpen: true, minSeverity: 0);

        var row = Assert.Single(open);
        Assert.Equal(Spindle, row.Event!.ConditionId);
    }

    [Fact]
    public void Without_retain_a_condition_is_open_while_active_or_unacknowledged()
    {
        var history = new EventHistory();
        history.Add(Alarm(Spindle, active: false, acked: false, retain: true, T0) with { Retain = null }, "Plant");
        history.Add(Alarm(Magazine, active: false, acked: true, retain: true, T0) with { Retain = null }, "Plant");

        var row = Assert.Single(history.Filter(onlyOpen: true, minSeverity: 0));
        Assert.Equal(Spindle, row.Event!.ConditionId);
    }

    [Fact]
    public void Severity_filter_keeps_rows_from_the_threshold()
    {
        var history = new EventHistory();
        history.Add(Plain(T0, severity: 100), "Plant");
        history.Add(Plain(T0, severity: 500), "Plant");
        history.Add(Plain(T0, severity: 800), "Plant");

        Assert.Equal(2, history.Filter(onlyOpen: false, minSeverity: 500).Count);
    }

    [Fact]
    public void An_event_already_listed_is_not_listed_again()
    {
        var history = new EventHistory();
        var e = Alarm(Spindle, active: true, acked: false, retain: true, T0);

        Assert.NotNull(history.Add(e, "Plant"));
        Assert.Null(history.Add(e with { }, "Plant"));
        Assert.Equal(1, history.Count);
    }

    [Fact]
    public void The_oldest_rows_go_beyond_the_limit()
    {
        var history = new EventHistory(limit: 3);
        for (var i = 0; i < 5; i++) history.Add(Plain(T0.AddSeconds(i)), "Plant");

        Assert.Equal(3, history.Count);
        Assert.Equal(2, history.Dropped);
        Assert.Equal(T0.AddSeconds(4), history.Rows[0].Event!.Time);
    }

    [Fact]
    public void An_event_that_went_with_the_limit_is_taken_again()
    {
        var history = new EventHistory(limit: 1);
        var first = Plain(T0);
        history.Add(first, "Plant");
        history.Add(Plain(T0.AddSeconds(1)), "Plant");

        Assert.NotNull(history.Add(first, "Plant"));
    }

    [Fact]
    public void Acknowledging_needs_the_latest_state_unacknowledged()
    {
        var history = new EventHistory();
        var older = history.Add(Alarm(Spindle, active: true, acked: false, retain: true, T0), "Plant")!;
        var latest = history.Add(Alarm(Spindle, active: false, acked: false, retain: true, T0.AddSeconds(1)), "Plant")!;
        var acked = history.Add(Alarm(Magazine, active: true, acked: true, retain: true, T0.AddSeconds(2)), "Plant")!;
        var plain = history.Add(Plain(T0.AddSeconds(3)), "Plant")!;

        Assert.True(history.CanAcknowledge(latest));
        Assert.False(history.CanAcknowledge(older));
        Assert.False(history.CanAcknowledge(acked));
        Assert.False(history.CanAcknowledge(plain));
        Assert.Same(latest, history.LatestOf(Spindle));
    }

    [Fact]
    public void A_connection_is_a_row_of_its_own()
    {
        var history = new EventHistory();
        var row = history.AddConnected("AMLOpcUa NodeSet server", new DateTimeOffset(T0));

        Assert.Null(row.Event);
        Assert.Equal("AMLOpcUa NodeSet server", row.Server);
        Assert.Single(history.Rows);
        Assert.Empty(history.Filter(onlyOpen: true, minSeverity: 0));
    }

    [Fact]
    public void Csv_has_server_comment_user_and_iso_utc_times()
    {
        var history = new EventHistory();
        history.AddConnected("Plant server", new DateTimeOffset(T0.AddSeconds(-1)));
        history.Add(Alarm(Spindle, active: true, acked: true, retain: true, T0, comment: "Bearing checked", user: "operator"), "Plant server");
        history.Add(Plain(T0.AddSeconds(1), message: "=SUM(A1)"), "Plant server");

        var lines = history.ToCsv(Semicolons()).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("Time;Server;Source;Severity;Message;State;Comment;User;Condition;EventType;EventId", lines[0]);
        Assert.StartsWith("2026-11-19T10:41:11.000Z;Plant server;;;Connected to Plant server;connection;", lines[1]);
        Assert.StartsWith("2026-11-19T10:41:12.000Z;Plant server;Drill;800;Spindle overloaded;active, acknowledged;Bearing checked;operator;\"nsu=urn:plant;s=Drill/SpindleHigh\";\"nsu=http://opcfoundation.org/UA/;i=9482\";", lines[2]);
        Assert.StartsWith("2026-11-19T10:41:13.000Z;Plant server;Server;100;'=SUM(A1);event;", lines[3]);
    }

    [Fact]
    public void Csv_follows_the_culture_of_the_spreadsheet()
    {
        var history = new EventHistory();
        history.Add(Plain(T0, message: "a;b"), "Plant");

        var lines = history.ToCsv(new CultureInfo("de-DE")).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains(";\"a;b\";", lines[1]);
    }
}
