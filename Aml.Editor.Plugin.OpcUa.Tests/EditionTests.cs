using System.Windows;
using System.Xml.Linq;
using Aml.Editor.Plugin.OpcUa;
using Aml.Engine.CAEX;
using static PluginTests.LessonHarness;

namespace PluginTests;

/// <summary>
/// Rainer Drath, 2026-11-20: the plugin shows which AutomationML edition the
/// open document is written for, works with Edition 2 only, and says so for
/// another edition (the beta of the editor for Edition 3 references by ID;
/// importing the OPC UA types gave 150796 violations there).
/// </summary>
public class EditionTests
{
    private static CAEXDocument Caex3(string standard)
    {
        var doc = CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX3_0);
        var file = doc.CAEXFile.Node;
        foreach (var old in file.Elements().Where(e => e.Name.LocalName == "SuperiorStandardVersion").ToList()) old.Remove();
        file.AddFirst(new XElement(file.Name.Namespace + "SuperiorStandardVersion", standard));
        return doc;
    }

    private sealed record Seen(string Edition, bool Band, bool Import, bool Instance, bool Check, bool Export, bool Serve, bool Connect, string Placeholder);

    private static Seen Look(OpcUaPlugin p) => new(
        p.EditionText.Text, p.EditionBand.Visibility == Visibility.Visible,
        p.ImportButton.IsEnabled, p.InstanceButton.IsEnabled, p.CheckButton.IsEnabled, p.ExportButton.IsEnabled,
        p.ServeButton.IsEnabled, p.ConnectButton.IsEnabled, p.Placeholder.Text);

    [Fact]
    public void An_Edition_2_document_is_named_and_worked_with()
    {
        Seen? seen = null;
        OnUiThread(() =>
        {
            var plugin = new OpcUaPlugin();
            plugin.DocumentLoaded(Caex3("AutomationML 2.10"));
            seen = Look(plugin);
            return Task.CompletedTask;
        });

        Assert.Equal("AutomationML Edition 2 (AutomationML 2.10)", seen!.Edition);
        Assert.True(seen.Band);
        Assert.True(seen.Import);
        Assert.True(seen.Instance);
        Assert.True(seen.Serve);
    }

    [Fact]
    public void A_document_of_another_edition_is_left_alone_and_the_plugin_says_why()
    {
        Seen? seen = null;
        OnUiThread(() =>
        {
            var plugin = new OpcUaPlugin();
            plugin.DocumentLoaded(Caex3("AutomationML 3.0"));
            seen = Look(plugin);
            return Task.CompletedTask;
        });

        Assert.True(seen!.Band);
        Assert.Contains("AutomationML 3.0", seen.Edition);
        Assert.Contains("Edition 2", seen.Edition);
        Assert.False(seen.Import);
        Assert.False(seen.Instance);
        Assert.False(seen.Check);
        Assert.False(seen.Export);
        Assert.False(seen.Serve);
        // A server needs no document: connecting, browsing and watching go on.
        Assert.True(seen.Connect);
        Assert.Contains("Edition 2", seen.Placeholder);
    }

    [Fact]
    public void An_Edition_2_document_after_one_of_another_edition_is_worked_with_again()
    {
        Seen? seen = null;
        OnUiThread(() =>
        {
            var plugin = new OpcUaPlugin();
            plugin.DocumentLoaded(Caex3("AutomationML 3.0"));
            plugin.DocumentLoaded(Caex3("AutomationML 2.1"));
            seen = Look(plugin);
            return Task.CompletedTask;
        });

        Assert.Equal("AutomationML Edition 2 (AutomationML 2.1)", seen!.Edition);
        Assert.True(seen.Import);
    }

    [Fact]
    public void An_Edition_1_document_is_named_as_such()
    {
        Seen? seen = null;
        OnUiThread(() =>
        {
            var plugin = new OpcUaPlugin();
            plugin.DocumentLoaded(CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX2_15));
            seen = Look(plugin);
            return Task.CompletedTask;
        });

        Assert.Equal("AutomationML Edition 1 (CAEX 2.15)", seen!.Edition);
        Assert.False(seen.Import);
    }

    [Fact]
    public void Without_a_document_no_edition_is_shown()
    {
        bool band = true;
        OnUiThread(() =>
        {
            var plugin = new OpcUaPlugin();
            plugin.DocumentLoaded(Caex3("AutomationML 2.10"));
            plugin.DocumentUnLoaded();
            band = plugin.EditionBand.Visibility == Visibility.Visible;
            return Task.CompletedTask;
        });

        Assert.False(band);
    }
}
