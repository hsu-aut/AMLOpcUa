using Aml.Editor.Plugin.OpcUa;
using Aml.Editor.Plugin.OpcUa.Localization;
using OpcUaAml.Checks;

namespace PluginTests;

/// <summary>
/// The check list showed "Error" and the core's English message in the German
/// plugin, on the tab that lesson 2 sends everyone to.
/// </summary>
public class FindingRowTests
{
    private static readonly Finding Missing =
        new(Rules.MissingMandatory, Severity.Error, "Plant/Firmware", "Mandatory child 'Manufacturer' of 'Firmware' is missing.", "id-1")
        {
            Format = FindingTexts.MissingMandatory,
            Args = new[] { "Manufacturer", "Firmware" },
        };

    private static T In<T>(bool german, Func<T> f)
    {
        var before = L.German;
        L.German = german;
        try { return f(); }
        finally { L.German = before; }
    }

    [Fact]
    public void A_finding_is_shown_in_German_with_its_names()
    {
        var row = new FindingRow(Missing);

        Assert.Equal("Fehler", In(true, () => row.Severity));
        Assert.Equal("Das Mandatory-Kindelement 'Manufacturer' von 'Firmware' fehlt.", In(true, () => row.Message));
    }

    [Fact]
    public void In_English_it_reads_as_the_core_wrote_it()
    {
        var row = new FindingRow(Missing);

        Assert.Equal("Error", In(false, () => row.Severity));
        Assert.Equal(Missing.Message, In(false, () => row.Message));
    }

    [Fact]
    public void The_row_keeps_what_selecting_the_element_needs()
    {
        var row = new FindingRow(Missing with { Severity = Severity.Warning });

        Assert.False(row.IsError);
        Assert.Equal("Warnung", In(true, () => row.Severity));
        Assert.Equal("id-1", row.Finding.ElementId);
        Assert.Equal("Plant/Firmware", row.ElementPath);
        Assert.Equal(Rules.MissingMandatory, row.Rule);
    }
}
