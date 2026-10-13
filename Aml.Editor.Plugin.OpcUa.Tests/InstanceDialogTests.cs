using Aml.Editor.Plugin.OpcUa;

namespace PluginTests;

/// <summary>
/// The dialog lists the placeholders of a type with every Optional child in;
/// one below an Optional child that is not ticked is not created and asks for
/// nothing.
/// </summary>
public class InstanceDialogTests
{
    private static readonly string[] Optional = ["DeviceTypeImage", "Identification", "Identification/Extra"];

    [Theory]
    [InlineData("<GroupIdentifier>", true)]
    [InlineData("ParameterSet/<ParameterIdentifier>", true)]
    [InlineData("DeviceTypeImage/<ImageIdentifier>", false)]
    [InlineData("Identification/<GroupIdentifier>", true)]
    [InlineData("Identification/Extra/<Item>", false)]
    public void A_placeholder_counts_when_every_Optional_child_above_it_is_ticked(string placeholder, bool counts)
    {
        var ticked = new HashSet<string> { "Identification" };

        Assert.Equal(counts, InstanceWindow.IsCreated(placeholder, Optional, ticked));
    }
}
