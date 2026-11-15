using Aml.Editor.Plugin.OpcUa;
using Aml.Editor.Plugin.OpcUa.Localization;
using OpcUaAml.Addressing;
using OpcUaAml.Checks;
using OpcUaAml.Server;

namespace PluginTests;

/// <summary>
/// A screen reader names a row of a list by what the row's object says of
/// itself (UI Automation takes ToString when nothing else names it). The rows
/// of the watch list were read out as "Aml.Editor.Plugin.OpcUa.OpcUaPlugin+WatchRow".
/// </summary>
public class ListRowNamesTests
{
    private static readonly UaNodeAddress Speed = new("urn:amlopcua:document", UaIdType.String, "Plant/ProcessingStation/Drill/SpindleSpeed");

    [Fact]
    public void A_watched_variable_is_named_by_its_node_and_value()
    {
        var row = new OpcUaPlugin.WatchRow { Address = Speed, Node = "SpindleSpeed" };
        row.Update(new UaReadResult(Speed, 1450.5, "Double", true, "Good"));

        Assert.Equal("SpindleSpeed: 1450.5, Good", row.ToString());
    }

    [Fact]
    public void A_watched_variable_with_a_unit_names_it_after_the_value()
    {
        var row = new OpcUaPlugin.WatchRow { Address = Speed, Node = "SpindleSpeed" };
        row.SetUnit("min⁻¹");
        row.Update(new UaReadResult(Speed, 1450.5, "Double", true, "Good"));

        Assert.Equal("min⁻¹", row.Unit);
        Assert.Equal("SpindleSpeed: 1450.5 min⁻¹, Good", row.ToString());
    }

    [Fact]
    public void A_watched_variable_without_a_value_yet_says_so()
    {
        var row = new OpcUaPlugin.WatchRow { Address = Speed, Node = "SpindleSpeed" };

        // In the language of the machine, as the list shows it.
        Assert.Equal("SpindleSpeed: " + L.T("waiting for the first value"), row.ToString());
    }

    [Fact]
    public void A_finding_is_named_by_its_severity_rule_element_and_message()
    {
        var row = new FindingRow(new Finding("UA003", Severity.Error, "Plant/Conveyor", "Mandatory child 'Temperature' of 'ConveyorType' is missing.", null));

        Assert.Equal(L.T("Error") + " UA003 Plant/Conveyor: Mandatory child 'Temperature' of 'ConveyorType' is missing.", row.ToString());
    }

    [Fact]
    public void A_namespace_is_named_by_its_uri_and_version()
    {
        var row = new OpcUaPlugin.NamespaceRow("http://example.org/Conveyor/", "1.0.0", "2026-11-15", 1);

        Assert.Equal("http://example.org/Conveyor/ 1.0.0", row.ToString());
    }
}
