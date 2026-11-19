// The events and alarms a server sent, as a history: every change of a
// condition is a row of its own, the newest first, kept up to a limit (the
// oldest go first). What is still open is the last row of each condition
// while the server retains it. Connecting adds a row too, so a history that
// spans several connections or servers says which server each row came from.
// Written as CSV like the watched values: the user's list separator and
// numbers, times in ISO 8601 UTC, the server by its application name only.

using System.Globalization;
using System.Text;
using OpcUaAml.Addressing;

namespace OpcUaAml.Server;

/// <summary>
/// One event as a server sent it: the fields of BaseEventType, and for a
/// condition (an alarm) those of AlarmConditionType. <paramref name="Time"/> is UTC.
/// </summary>
public sealed record UaEvent(byte[] EventId, UaNodeAddress? EventType, UaNodeAddress? SourceNode, string SourceName, DateTime Time,
    string Message, ushort Severity, UaNodeAddress? ConditionId, string? ConditionName, bool? Active, bool? Acked, bool? Retain,
    string? Comment, string? ClientUserId)
{
    /// <summary>The EventId as hexadecimal text: how two events are told apart.</summary>
    public string EventIdHex => Convert.ToHexString(EventId);
}

/// <summary>A row of the history: an event, or with no event the moment the plugin connected to <paramref name="Server"/>.</summary>
public sealed record EventRow(DateTimeOffset Time, string Server, UaEvent? Event)
{
    public bool IsCondition => Event?.ConditionId != null;
}

public sealed class EventHistory(int limit = 10_000)
{
    private readonly LinkedList<EventRow> _rows = new();   // newest first
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public int Count { get { lock (_lock) return _rows.Count; } }

    /// <summary>Rows that went because the limit was reached.</summary>
    public int Dropped { get; private set; }

    /// <summary>Every row, the newest first.</summary>
    public IReadOnlyList<EventRow> Rows { get { lock (_lock) return _rows.ToList(); } }

    /// <summary>Adds an event; null when an event with its EventId is listed already (a condition refreshed again).</summary>
    public EventRow? Add(UaEvent e, string server)
    {
        lock (_lock)
        {
            if (!_ids.Add(e.EventIdHex)) return null;
            var row = new EventRow(new DateTimeOffset(DateTime.SpecifyKind(e.Time, DateTimeKind.Utc)), server, e);
            Insert(row);
            return row;
        }
    }

    /// <summary>A row that says the plugin connected to <paramref name="server"/>.</summary>
    public EventRow AddConnected(string server, DateTimeOffset time)
    {
        var row = new EventRow(time, server, null);
        lock (_lock) Insert(row);
        return row;
    }

    private void Insert(EventRow row)
    {
        _rows.AddFirst(row);
        while (_rows.Count > limit)
        {
            if (_rows.Last!.Value.Event is { } gone) _ids.Remove(gone.EventIdHex);
            _rows.RemoveLast();
            Dropped++;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _rows.Clear();
            _ids.Clear();
            Dropped = 0;
        }
    }

    /// <summary>
    /// The rows to show: with <paramref name="onlyOpen"/> the last row of each
    /// condition while it is open, otherwise every row; either from
    /// <paramref name="minSeverity"/> on. A connection row shows only in the full list.
    /// </summary>
    public IReadOnlyList<EventRow> Filter(bool onlyOpen, int minSeverity)
    {
        lock (_lock)
        {
            if (!onlyOpen)
                return _rows.Where(r => r.Event == null ? minSeverity == 0 : r.Event.Severity >= minSeverity).ToList();
            var seen = new HashSet<UaNodeAddress>();
            var open = new List<EventRow>();
            foreach (var row in Current())
            {
                if (row.Event?.ConditionId is not { } condition || !seen.Add(condition)) continue;
                if (IsOpen(row.Event) && row.Event.Severity >= minSeverity) open.Add(row);
            }
            return open;
        }
    }

    /// <summary>
    /// The rows since the newest connection, the newest first: what the server
    /// connected now sent. An alarm seen only before may have closed meanwhile,
    /// or belong to another server; its state is not known any more.
    /// </summary>
    private IEnumerable<EventRow> Current() => _rows.TakeWhile(r => r.Event != null);

    /// <summary>Open while the server retains it; a server that does not say: while active or not acknowledged.</summary>
    private static bool IsOpen(UaEvent e) => e.Retain ?? (e.Active == true || e.Acked == false);

    /// <summary>The newest row of a condition, or null.</summary>
    public EventRow? LatestOf(UaNodeAddress conditionId)
    {
        lock (_lock) return Current().FirstOrDefault(r => r.Event?.ConditionId == conditionId);
    }

    /// <summary>
    /// Since when the alarm of <paramref name="row"/> is active: the time of the
    /// first of the active rows of its condition that lead up to it; null when
    /// the row's state is not active.
    /// </summary>
    public DateTimeOffset? ActiveSince(EventRow row)
    {
        if (row.Event is not { ConditionId: { } condition, Active: true }) return null;
        lock (_lock)
        {
            DateTimeOffset? since = null;
            var reached = false;
            foreach (var r in _rows)
            {
                if (ReferenceEquals(r, row)) reached = true;
                if (!reached || r.Event?.ConditionId != condition) continue;
                if (r.Event.Active != true) break;
                since = r.Time;
            }
            return since;
        }
    }

    /// <summary>Whether a row may be acknowledged: it is the newest of its condition, and that is not acknowledged.</summary>
    public bool CanAcknowledge(EventRow row) =>
        row.Event is { ConditionId: { } condition, Acked: false } && ReferenceEquals(LatestOf(condition), row);

    /// <summary>The rows as CSV for <paramref name="culture"/>'s spreadsheet, the oldest first; the user's culture without one.</summary>
    public string ToCsv(CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var separator = Csv.Separator(culture);
        var text = new StringBuilder();
        static (string, bool) T(string? s) => (s ?? "", false);
        Csv.Line(text, separator, T("Time"), T("Server"), T("Source"), T("Severity"), T("Message"), T("State"),
            T("Comment"), T("User"), T("Condition"), T("EventType"), T("EventId"));
        foreach (var row in Rows.AsEnumerable().Reverse())
        {
            var time = (row.Time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture), true);
            if (row.Event is not { } e)
            {
                Csv.Line(text, separator, time, T(row.Server), T(""), T(""), T("Connected to " + row.Server), T("connection"),
                    T(""), T(""), T(""), T(""), T(""));
                continue;
            }
            Csv.Line(text, separator, time, T(row.Server), T(e.SourceName), (e.Severity.ToString(culture), true), T(e.Message), T(State(e)),
                T(e.Comment), T(e.ClientUserId), T(e.ConditionId?.ToString()), T(e.EventType?.ToString()), T(e.EventIdHex));
        }
        return text.ToString();
    }

    /// <summary>The state in fixed English words, so an evaluation does not depend on the language.</summary>
    internal static string State(UaEvent e) =>
        e.ConditionId == null ? "event"
        : (e.Active == true ? "active" : "inactive") + ", " + (e.Acked == true ? "acknowledged" : "unacknowledged");
}
