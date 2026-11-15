using OpcUaAml.Server;

namespace OpcUaAml.Tests;

/// <summary>
/// A CAEX attribute names its unit as text; OPC UA states it as EUInformation
/// after UNECE Recommendation 20 (OPC 10000-8), with the UnitId derived from
/// the unit's common code. The numbers are those of the OPC Foundation's
/// UNECE_to_OPCUA.csv.
/// </summary>
public class EngineeringUnitTests
{
    [Fact]
    public void A_symbol_names_its_UNECE_unit()
    {
        var unit = EngineeringUnit.From("°C");

        Assert.Equal("CEL", unit.Code);
        Assert.Equal(4408652, unit.UnitId);
        Assert.Equal("°C", unit.DisplayName);
        Assert.Equal("degree Celsius", unit.Description);
        Assert.Equal("http://www.opcfoundation.org/UA/units/un/cefact", unit.NamespaceUri);
    }

    [Fact]
    public void Case_matters_in_a_symbol()
    {
        Assert.Equal("AMP", EngineeringUnit.From("A").Code);
        Assert.Equal("ARE", EngineeringUnit.From("a").Code);
        Assert.Equal("MMT", EngineeringUnit.From("mm").Code);
        Assert.Equal("MAM", EngineeringUnit.From("Mm").Code);
    }

    [Theory]
    [InlineData("1/min", "C94", "min⁻¹")]
    [InlineData("rpm", "RPM", "r/min")]
    [InlineData("U/min", "RPM", "r/min")]
    [InlineData("1/h", "H10", "1/h")]
    [InlineData("m2", "MTK", "m²")]
    [InlineData("m^3", "MTQ", "m³")]
    [InlineData("Nm", "NU", "N·m")]
    [InlineData("N*m", "NU", "N·m")]
    [InlineData("%", "P1", "% or pct")]
    [InlineData(" kg ", "KGM", "kg")]
    public void A_plain_text_spelling_finds_its_unit(string text, string code, string symbol)
    {
        var unit = EngineeringUnit.From(text);

        Assert.Equal(code, unit.Code);
        Assert.Equal(symbol, unit.DisplayName);
    }

    [Fact]
    public void A_common_code_or_a_name_finds_its_unit()
    {
        Assert.Equal("CEL", EngineeringUnit.From("CEL").Code);
        Assert.Equal("CEL", EngineeringUnit.From("degree Celsius").Code);
        Assert.Equal("MMT", EngineeringUnit.From("Millimetre").Code);
    }

    [Fact]
    public void An_unknown_unit_keeps_its_text_without_a_UnitId()
    {
        var unit = EngineeringUnit.From("strokes");

        Assert.Null(unit.Code);
        Assert.Equal(-1, unit.UnitId);
        Assert.Equal("strokes", unit.DisplayName);
        Assert.Null(unit.NamespaceUri);
    }
}
