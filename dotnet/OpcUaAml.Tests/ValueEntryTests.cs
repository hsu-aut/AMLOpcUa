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
}
