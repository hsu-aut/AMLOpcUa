// The values a watch received, each with the time it arrived, for what a
// variable did while it was watched: kept up to a limit (the oldest go first)
// and written as CSV, one row per value, semicolons as spreadsheets here read
// them, times in ISO 8601 with the offset, values as ValueFormatting gives them.

using System.Globalization;
using System.Text;
using OpcUaAml.Addressing;

namespace OpcUaAml.Server;

/// <summary>One value received.</summary>
public sealed record WatchEntry(DateTimeOffset Time, string Node, UaNodeAddress Address, string Value, string Unit, string Status);

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
        var entry = new WatchEntry(time, node, read.Address, read.Good ? read.ValueText ?? "" : "", unit, read.Good ? "Good" : read.Status);
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

    public string ToCsv()
    {
        var text = new StringBuilder();
        void Line(params string[] cells) => text.Append(string.Join(";", cells.Select(Cell))).Append(Environment.NewLine);
        Line("Time", "Node", "NodeId", "Value", "Unit", "Status");
        foreach (var e in Entries)
            Line(e.Time.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture), e.Node, e.Address.ToString(), e.Value, e.Unit, e.Status);
        return text.ToString();
    }

    private static string Cell(string s) =>
        s.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
