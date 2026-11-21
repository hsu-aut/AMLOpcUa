using Aml.Editor.Plugin.OpcUa;
using Aml.Engine.CAEX;
using static PluginTests.LessonHarness;

namespace PluginTests;

/// <summary>
/// Rainer Drath, 2026-11-21: Bind to element… takes the element selected in the
/// editor: its name in the search field, the element selected in the list.
/// </summary>
public class ElementPickerTests
{
    private static CAEXDocument Plant(out InternalElementType lamp)
    {
        var document = CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX3_0);
        var station = document.CAEXFile.InstanceHierarchy.Append("Plant").InternalElement.Append("Station");
        station.InternalElement.Append("Motor");
        lamp = station.InternalElement.Append("Lampe");
        station.InternalElement.Append("Lampe2");
        return document;
    }

    [Fact]
    public void The_element_selected_in_the_editor_is_chosen_already()
    {
        string search = "";
        InternalElementType? chosen = null;
        var document = Plant(out var lamp);
        OnUiThread(() =>
        {
            var picker = new ElementPickerWindow(document, "Bind PartsProduced to", lamp);
            search = picker.SearchText;
            chosen = picker.Chosen;
            picker.Close();
            return Task.CompletedTask;
        });

        Assert.Equal("Lampe", search);
        Assert.Same(lamp.Node, chosen?.Node);
    }

    [Fact]
    public void Without_a_selection_in_the_editor_nothing_is_chosen()
    {
        string search = "x";
        InternalElementType? chosen = null;
        var document = Plant(out _);
        OnUiThread(() =>
        {
            var picker = new ElementPickerWindow(document, "Bind PartsProduced to", null);
            search = picker.SearchText;
            chosen = picker.Chosen;
            picker.Close();
            return Task.CompletedTask;
        });

        Assert.Equal("", search);
        Assert.Null(chosen);
    }

    [Fact]
    public void An_element_of_another_document_selected_in_the_editor_is_not_chosen()
    {
        string search = "x";
        InternalElementType? chosen = null;
        var document = Plant(out _);
        Plant(out var foreign);
        OnUiThread(() =>
        {
            var picker = new ElementPickerWindow(document, "Bind PartsProduced to", foreign);
            search = picker.SearchText;
            chosen = picker.Chosen;
            picker.Close();
            return Task.CompletedTask;
        });

        Assert.Equal("", search);
        Assert.Null(chosen);
    }
}
