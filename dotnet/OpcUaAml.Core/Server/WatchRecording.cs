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
        var separator = string.IsNullOrEmpty(culture.TextInfo.ListSeparator) ? ";" : culture.TextInfo.ListSeparator;
        var text = new StringBuilder();
        void Line(params (string Text, bool Number)[] cells) =>
            text.Append(string.Join(separator, cells.Select(c => Cell(c.Text, c.Number, separator)))).Append(Environment.NewLine);
        static (string, bool) T(string s) => (s, false);
        Line(T("Time"), T("Node"), T("NodeId"), T("Value"), T("Unit"), T("Status"));
        foreach (var e in Entries)
        {
            var number = Number(e.Raw, culture);
            Line((e.Time.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture), true), T(e.Node), T(e.Address.ToString()),
                number != null ? (number, true) : T(e.Value), T(e.Unit), T(e.Status));
        }
        return text.ToString();
    }

    /// <summary>A number in the culture's notation; null for anything else (arrays too, which stay text).</summary>
    private static string? Number(object? raw, CultureInfo culture) => raw switch
    {
        float f => f.ToString("R", culture),
        double d => d.ToString("R", culture),
        decimal or sbyte or byte or short or ushort or int or uint or long or ulong => ((IFormattable)raw).ToString(null, culture),
        _ => null,
    };

    private static string Cell(string s, bool number, string separator)
    {
        if (!number && s.Length > 0 && "=+-@\t\r".Contains(s[0])) s = "'" + s;
        return s.Contains(separator) || s.IndexOfAny(['"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
