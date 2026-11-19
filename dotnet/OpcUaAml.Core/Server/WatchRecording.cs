// The values a watch received, each with the time it arrived, for what a
// variable did while it was watched: kept up to a limit (the oldest go first)
// and written as CSV, one row per value, times in ISO 8601 with the offset.
// The list separator and the decimal sign are the user's culture's, as the
// spreadsheet on that computer reads them (semicolon and comma in German); a
// text that starts like a formula gets an apostrophe, so it stays text.

using System.Globalization;
using System.Text;
using OpcUaAml.Addressing;

namespace OpcUaAml.Server;

/// <summary>One value received.</summary>
public sealed record WatchEntry(DateTimeOffset Time, string Node, UaNodeAddress Address, string Value, string Unit, string Status, object? Raw = null);

public sealed class WatchRecording(int limit = 100_000)
{
    private readonly Queue<WatchEntry> _entries = new();
    private readonly object _lock = new();

    public int Count { get { lock (_lock) return _entries.Count; } }

    /// <summary>Values that went because the limit was reached.</summary>
    public int Dropped { get; private set; }

    public IReadOnlyList<WatchEntry> Entries { get { lock (_lock) return _entries.ToList(); } }

    public void Add(DateTimeOffset time, string node, string unit, UaReadResult read)
    {
        // An Uncertain value (beyond its engineering range, say) is kept: that is when it matters.
        var keep = read.Value != null && !read.Status.StartsWith("Bad", StringComparison.Ordinal);
        var entry = new WatchEntry(time, node, read.Address, keep ? read.ValueText ?? "" : "", unit, read.Good ? "Good" : read.Status, keep ? read.Value : null);
        lock (_lock)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > limit)
            {
                _entries.Dequeue();
                Dropped++;
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            Dropped = 0;
        }
    }

    /// <summary>The values as CSV for <paramref name="culture"/>'s spreadsheet; the user's culture without one.</summary>
    public string ToCsv(CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var separator = Csv.Separator(culture);
        var text = new StringBuilder();
        static (string, bool) T(string s) => (s, false);
        Csv.Line(text, separator, T("Time"), T("Node"), T("NodeId"), T("Value"), T("Unit"), T("Status"));
        foreach (var e in Entries)
        {
            var number = Csv.Number(e.Raw, culture);
            Csv.Line(text, separator, (e.Time.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture), true), T(e.Node), T(e.Address.ToString()),
                number != null ? (number, true) : T(e.Value), T(e.Unit), T(e.Status));
        }
        return text.ToString();
    }
}
