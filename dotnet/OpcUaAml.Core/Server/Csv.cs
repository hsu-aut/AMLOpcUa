// CSV for the spreadsheet on this computer: the list separator and the
// decimal sign of the user's culture (semicolon and comma in German), and a
// text that starts like a formula gets an apostrophe, so it stays text.

using System.Globalization;
using System.Text;

namespace OpcUaAml.Server;

internal static class Csv
{
    /// <summary>The culture's list separator; a semicolon where it has none.</summary>
    public static string Separator(CultureInfo culture) =>
        string.IsNullOrEmpty(culture.TextInfo.ListSeparator) ? ";" : culture.TextInfo.ListSeparator;

    /// <summary>Appends one line of cells; a number is written as it is, a text guarded and quoted where needed.</summary>
    public static void Line(StringBuilder text, string separator, params (string Text, bool Number)[] cells) =>
        text.Append(string.Join(separator, cells.Select(c => Cell(c.Text, c.Number, separator)))).Append(Environment.NewLine);

    /// <summary>A number in the culture's notation; null for anything else (arrays too, which stay text).</summary>
    public static string? Number(object? raw, CultureInfo culture) => raw switch
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
