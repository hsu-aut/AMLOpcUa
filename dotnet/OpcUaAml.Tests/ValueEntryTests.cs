using OpcUaAml.Server;

namespace OpcUaAml.Tests;

/// <summary>A value typed by a user, made the value of a variable's data type before it is written or passed to a method.</summary>
public class ValueEntryTests
{
    [Theory]
    [InlineData("Boolean", "true", true)]
    [InlineData("Boolean", "0", false)]
    [InlineData("Int32", "-12", -12)]
    [InlineData("UInt16", "65535", (ushort)65535)]
    [InlineData("Byte", "255", (byte)255)]
    [InlineData("Int64", "9000000000", 9000000000L)]
    [InlineData("Double", "1.5", 1.5)]
    [InlineData("Double", "1,5", 1.5)]
    [InlineData("Float", "2.25", 2.25f)]
    [InlineData("String", " two words ", " two words ")]
    [InlineData("Enumeration", "3", 3)]
    public void A_scalar_is_read_as_its_type(string type, string text, object expected) =>
        Assert.Equal(expected, ValueEntry.Parse(type, -1, text));

    [Fact]
    public void A_time_is_read_as_UTC()
    {
        var value = (DateTime)ValueEntry.Parse("DateTime", -1, "2026-11-24T10:00:00Z");

        Assert.Equal(new DateTime(2026, 11, 24, 10, 0, 0, DateTimeKind.Utc), value);
        Assert.Equal(DateTimeKind.Utc, value.Kind);
    }

    [Fact]
    public void An_array_is_its_elements_separated_by_spaces()
    {
        Assert.Equal(new[] { 1.0, 2.5, 3.0 }, ValueEntry.Parse("Double", 1, "1 2.5  3"));
        Assert.Equal(new[] { true, false }, ValueEntry.Parse("Boolean", 1, "true false"));
        Assert.Equal(Array.Empty<int>(), ValueEntry.Parse("Int32", 1, " "));
    }

    [Theory]
    [InlineData("Int32", "abc", "whole number")]
    [InlineData("Byte", "300", "0 to 255")]
    [InlineData("Boolean", "maybe", "true or false")]
    [InlineData("Double", "1.2.3", "number")]
    [InlineData("DateTime", "tomorrow", "2026-11-24T10:00:00Z")]
    [InlineData("ExtensionObject", "x", "cannot be typed")]
    public void A_text_that_is_not_a_value_of_the_type_says_what_is(string type, string text, string hint)
    {
        var ex = Assert.Throws<ValueEntryException>(() => ValueEntry.Parse(type, -1, text));

        Assert.Contains(hint, ex.Message);
    }

    [Fact]
    public void Matrices_cannot_be_typed() =>
        Assert.Throws<ValueEntryException>(() => ValueEntry.Parse("Double", 2, "1 2"));

    // Review of 2026-11-18: "01.02.2026" (1 February) was written as 2 January,
    // "10:00" as today 10:00 UTC; the server took both as Good.
    [Theory]
    [InlineData("01.02.2026")]
    [InlineData("02/01/2026")]
    [InlineData("10:00")]
    [InlineData("Feb 1 2026")]
    public void Only_ISO_8601_is_read_as_a_time(string text)
    {
        var ex = Assert.Throws<ValueEntryException>(() => ValueEntry.Parse("DateTime", -1, text));
        Assert.Equal(ValueProblem.NotTime, ex.Problem);
    }

    [Theory]
    [InlineData("2026-02-01", "2026-02-01T00:00:00.0000000Z")]
    [InlineData("2026-02-01T10:00Z", "2026-02-01T10:00:00.0000000Z")]
    [InlineData("2026-02-01T10:00:00", "2026-02-01T10:00:00.0000000Z")]
    [InlineData("2026-02-01T10:00:00.5+01:00", "2026-02-01T09:00:00.5000000Z")]
    [InlineData("2026-02-01 10:00:00Z", "2026-02-01T10:00:00.0000000Z")]
    public void ISO_8601_times_are_read_as_UTC(string text, string utc) =>
        Assert.Equal(utc, ((DateTime)ValueEntry.Parse("DateTime", -1, text)).ToString("o"));

    // 1e39 became Infinity as a Float, 1e400 as a Double, and was written.
    [Theory]
    [InlineData("Float", "1e39")]
    [InlineData("Float", "-1e39")]
    [InlineData("Double", "1e400")]
    public void A_number_beyond_the_type_is_out_of_range(string type, string text)
    {
        var ex = Assert.Throws<ValueEntryException>(() => ValueEntry.Parse(type, -1, text));
        Assert.Equal(ValueProblem.OutOfRange, ex.Problem);
    }

    [Fact]
    public void Infinity_and_NaN_typed_as_such_are_values() =>
        Assert.Equal([double.NaN, double.PositiveInfinity], (double[])ValueEntry.Parse("Double", 1, "NaN Infinity"));

    [Fact]
    public void The_range_names_its_ends_apart_for_a_message_in_another_language()
    {
        var ex = Assert.Throws<ValueEntryException>(() => ValueEntry.Parse("Byte", -1, "300"));
        Assert.Equal(("0", "255"), (ex.Min, ex.Max));
        Assert.Equal(("0", "255"), (ex.For("Mode").Min, ex.For("Mode").Max));
    }

    // ["Line 1", "Line 2"] was shown as "Line 1 Line 2" and written back as four elements.
    [Fact]
    public void Array_elements_with_spaces_are_quoted_and_quotes_doubled()
    {
        Assert.Equal(["Line 1", "Line 2", "x", "", "say \"hi\""],
            (string[])ValueEntry.Parse("String", 1, "\"Line 1\" \"Line 2\" x \"\" \"say \"\"hi\"\"\""));
    }

    [Fact]
    public void An_open_quote_is_no_array()
    {
        var ex = Assert.Throws<ValueEntryException>(() => ValueEntry.Parse("String", 1, "\"Line 1"));
        Assert.Equal(ValueProblem.OpenQuote, ex.Problem);
    }

    [Fact]
    public void The_text_of_a_value_reads_back_as_the_same_value()
    {
        string[] lines = ["Line 1", "x", "", "say \"hi\""];
        var text = ValueEntry.ToText("String", 1, lines);
        Assert.Equal("\"Line 1\" x \"\" \"say \"\"hi\"\"\"", text);
        Assert.Equal(lines, (string[])ValueEntry.Parse("String", 1, text));

        // A Byte array as its numbers, not as Base64: the dialog shows what it writes back.
        Assert.Equal("7 8 9", ValueEntry.ToText("Byte", 1, new byte[] { 7, 8, 9 }));
        Assert.Equal(new byte[] { 7, 8, 9 }, (byte[])ValueEntry.Parse("Byte", 1, "7 8 9"));
        Assert.Equal("1.5", ValueEntry.ToText("Double", -1, 1.5));
        Assert.Equal("", ValueEntry.ToText("String", -1, null));
    }

    // A byte[] made a Variant of type ByteString; the server held a ByteString in a Byte[] variable.
    [Fact]
    public void A_Byte_array_is_written_as_an_array_of_Byte()
    {
        var variant = ValueEntry.ToVariant("Byte", 1, new byte[] { 7, 8, 9 });
        Assert.Equal(Opc.Ua.BuiltInType.Byte, variant.TypeInfo.BuiltInType);
        Assert.Equal(Opc.Ua.ValueRanks.OneDimension, variant.TypeInfo.ValueRank);

        var bytes = ValueEntry.ToVariant("ByteString", -1, new byte[] { 7 });
        Assert.Equal(Opc.Ua.BuiltInType.ByteString, bytes.TypeInfo.BuiltInType);
    }
}
