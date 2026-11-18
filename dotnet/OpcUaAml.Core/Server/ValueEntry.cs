// A value as a user types it, made a value of a variable's built-in type
// before it is written into a server or passed to a method: the forms
// ValueFormatting shows (true/false, numbers with a point, ISO 8601 times,
// Base64, arrays as elements separated by spaces), and a comma for the
// decimal point, as German keyboards have it.

using System.Globalization;
using System.Numerics;
using System.Text;

namespace OpcUaAml.Server;

public enum ValueProblem
{
    NotBoolean,
    NotWholeNumber,
    OutOfRange,
    NotNumber,
    NotTime,
    NotGuid,
    NotBase64,
    NotTypeable,
    Matrix,
    OpenQuote,
}

/// <summary>A text that is no value of the type asked for; Problem says why, for a message in the user's language.</summary>
public sealed class ValueEntryException : FormatException
{
    public ValueEntryException(ValueProblem problem, string text, string type, string? range = null, string? argument = null, string? min = null, string? max = null)
        : base((argument != null ? argument + ": " : "") + Describe(problem, text, type, range))
    {
        Problem = problem;
        Text = text;
        Type = type;
        Range = range;
        Argument = argument;
        Min = min;
        Max = max;
    }

    public ValueProblem Problem { get; }
    public string Text { get; }
    public string Type { get; }
    /// <summary>The values a type holds, for OutOfRange: "0 to 255".</summary>
    public string? Range { get; }
    /// <summary>The method argument the text was for, if any.</summary>
    public string? Argument { get; }
    /// <summary>The ends of the range apart, for OutOfRange, so a message in another language can join them.</summary>
    public string? Min { get; }
    public string? Max { get; }

    /// <summary>The same problem, for a method argument.</summary>
    public ValueEntryException For(string argument) => new(Problem, Text, Type, Range, argument, Min, Max);

    private static string Describe(ValueProblem problem, string text, string type, string? range) => problem switch
    {
        ValueProblem.NotBoolean => $"'{text}' is not a Boolean: write true or false.",
        ValueProblem.NotWholeNumber => $"'{text}' is not a whole number.",
        ValueProblem.OutOfRange => $"'{text}' is out of range: a {type} is {range}.",
        ValueProblem.NotNumber => $"'{text}' is not a number: write 1.5, for instance.",
        ValueProblem.NotTime => $"'{text}' is not a time: write 2026-11-24T10:00:00Z, for instance.",
        ValueProblem.NotGuid => $"'{text}' is not a GUID.",
        ValueProblem.NotBase64 => $"'{text}' is not Base64.",
        ValueProblem.Matrix => $"Values with {range} dimensions cannot be typed in.",
        ValueProblem.OpenQuote => $"'{text}': a quote is not closed.",
        _ => $"Values of the type {type} cannot be typed in.",
    };
}

public static class ValueEntry
{
    /// <summary>
    /// The value of <paramref name="text"/> as the built-in type
    /// <paramref name="type"/> (a name of OPC 10000-6, as UaReadResult.DataType
    /// gives it): a scalar, or for ValueRank 0 or 1 an array of the elements
    /// separated by spaces; an element with spaces, or an empty one, is in
    /// quotes, a quote in it doubled ("Line 1" ""). Throws ValueEntryException
    /// for a text that is none.
    /// </summary>
    public static object Parse(string type, int valueRank, string text)
    {
        if (valueRank > 1) throw new ValueEntryException(ValueProblem.Matrix, text, type, valueRank.ToString(CultureInfo.InvariantCulture));
        if (valueRank is 0 or 1)
        {
            var element = ElementType(type) ?? throw new ValueEntryException(ValueProblem.NotTypeable, text, type);
            var parts = Elements(text, type);
            var array = Array.CreateInstance(element, parts.Count);
            for (var i = 0; i < parts.Count; i++) array.SetValue(Scalar(type, parts[i]), i);
            return array;
        }
        return Scalar(type, text);
    }

    /// <summary>The elements of an array as typed: separated by spaces, in quotes when they hold spaces.</summary>
    private static List<string> Elements(string text, string type)
    {
        var elements = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            if (text[i] != '"')
            {
                var start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
                elements.Add(text[start..i]);
                continue;
            }
            var quoted = new StringBuilder();
            for (i++; ; i++)
            {
                if (i >= text.Length) throw new ValueEntryException(ValueProblem.OpenQuote, text, type);
                if (text[i] != '"') { quoted.Append(text[i]); continue; }
                if (i + 1 < text.Length && text[i + 1] == '"') { quoted.Append('"'); i++; continue; }
                i++;
                break;
            }
            elements.Add(quoted.ToString());
        }
        return elements;
    }

    /// <summary>
    /// A value as it is typed, so that <see cref="Parse"/> reads it back as the
    /// same value: array elements in quotes where needed, a Byte array as its numbers.
    /// </summary>
    public static string ToText(string type, int valueRank, object? value) => value switch
    {
        null => "",
        byte[] bytes when type != "ByteString" => string.Join(" ", bytes.Select(b => b.ToString(CultureInfo.InvariantCulture))),
        byte[] bytes => Convert.ToBase64String(bytes),
        Array array => string.Join(" ", array.Cast<object?>().Select(e => Quoted(ValueFormatting.ToAml(e) ?? ""))),
        _ => ValueFormatting.ToAml(value) ?? "",
    };

    private static string Quoted(string element) =>
        element.Length == 0 || element.Any(c => char.IsWhiteSpace(c) || c == '"') ? "\"" + element.Replace("\"", "\"\"") + "\"" : element;

    /// <summary>
    /// The value parsed, as the server is to receive it: a Byte array as an
    /// array of Byte, which a plain Variant would make a ByteString.
    /// </summary>
    public static Opc.Ua.Variant ToVariant(string type, int valueRank, object value) =>
        value is byte[] && type == "Byte"
            ? new Opc.Ua.Variant(value, new Opc.Ua.TypeInfo(Opc.Ua.BuiltInType.Byte, Opc.Ua.ValueRanks.OneDimension))
            : new Opc.Ua.Variant(value);

    private static Type? ElementType(string type) => type switch
    {
        "Boolean" => typeof(bool),
        "SByte" => typeof(sbyte),
        "Byte" => typeof(byte),
        "Int16" => typeof(short),
        "UInt16" => typeof(ushort),
        "Int32" or "Enumeration" => typeof(int),
        "UInt32" => typeof(uint),
        "Int64" => typeof(long),
        "UInt64" => typeof(ulong),
        "Float" => typeof(float),
        "Double" => typeof(double),
        "String" => typeof(string),
        "DateTime" => typeof(DateTime),
        "Guid" => typeof(Guid),
        _ => null,
    };

    private static object Scalar(string type, string text)
    {
        var t = text.Trim();
        switch (type)
        {
            case "Boolean":
                return t.ToLowerInvariant() switch
                {
                    "true" or "1" => true,
                    "false" or "0" => false,
                    _ => throw new ValueEntryException(ValueProblem.NotBoolean, text, type),
                };
            case "SByte": return (sbyte)Whole(type, t, sbyte.MinValue, sbyte.MaxValue);
            case "Byte": return (byte)Whole(type, t, byte.MinValue, byte.MaxValue);
            case "Int16": return (short)Whole(type, t, short.MinValue, short.MaxValue);
            case "UInt16": return (ushort)Whole(type, t, ushort.MinValue, ushort.MaxValue);
            case "Int32" or "Enumeration": return (int)Whole(type, t, int.MinValue, int.MaxValue);
            case "UInt32": return (uint)Whole(type, t, uint.MinValue, uint.MaxValue);
            case "Int64": return (long)Whole(type, t, long.MinValue, long.MaxValue);
            case "UInt64": return (ulong)Whole(type, t, ulong.MinValue, ulong.MaxValue);
            case "Float": return (float)Number(type, t);
            case "Double": return Number(type, t);
            case "String": return text;
            case "LocalizedText": return new Opc.Ua.LocalizedText(text);
            case "DateTime":
                // ISO 8601 only: "01.02.2026" was read as 2 January, "10:00" as today.
                return DateTime.TryParseExact(t, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)
                    ? time : throw new ValueEntryException(ValueProblem.NotTime, text, type);
            case "Guid":
                return Guid.TryParse(t, out var guid) ? guid : throw new ValueEntryException(ValueProblem.NotGuid, text, type);
            case "ByteString":
                try { return Convert.FromBase64String(t); }
                catch (FormatException) { throw new ValueEntryException(ValueProblem.NotBase64, text, type); }
            default:
                throw new ValueEntryException(ValueProblem.NotTypeable, text, type);
        }
    }

    private static BigInteger Whole(string type, string text, BigInteger min, BigInteger max)
    {
        if (!BigInteger.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
            throw new ValueEntryException(ValueProblem.NotWholeNumber, text, type);
        if (n < min || n > max)
            throw OutOfRange(text, type, min.ToString(CultureInfo.InvariantCulture), max.ToString(CultureInfo.InvariantCulture));
        return n;
    }

    private static ValueEntryException OutOfRange(string text, string type, string min, string max) =>
        new(ValueProblem.OutOfRange, text, type, $"{min} to {max}", min: min, max: max);

    private static readonly string[] TimeFormats = new[] { "yyyy-MM-dd" }
        .Concat(from sep in new[] { "'T'", " " }
                from time in new[] { "HH:mm", "HH:mm:ss", "HH:mm:ss.FFFFFFF" }
                from zone in new[] { "", "K" }
                select $"yyyy-MM-dd{sep}{time}{zone}")
        .ToArray();

    private static double Number(string type, string text)
    {
        // A comma stands for the decimal point when there is no point.
        var t = text.Contains('.') ? text : text.Replace(',', '.');
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            throw new ValueEntryException(ValueProblem.NotNumber, text, type);
        // Beyond the type the parse gives Infinity; only Infinity typed as such is one.
        var max = type == "Float" ? float.MaxValue : double.MaxValue;
        var infinityTyped = t.TrimStart('+', '-').Equals("Infinity", StringComparison.OrdinalIgnoreCase);
        if (!infinityTyped && !double.IsNaN(d) && (double.IsInfinity(d) || Math.Abs(d) > max))
        {
            var end = type == "Float" ? float.MaxValue.ToString("R", CultureInfo.InvariantCulture) : double.MaxValue.ToString("R", CultureInfo.InvariantCulture);
            throw OutOfRange(text, type, "-" + end, end);
        }
        return d;
    }
}
