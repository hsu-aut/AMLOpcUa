using Aml.Editor.Plugin.OpcUa;
using Aml.Engine.CAEX;

namespace PluginTests;

/// <summary>
/// Commands that cannot work are greyed out. "Check again", "Add missing
/// Mandatory children" and "Export SVG…" stayed enabled without a document or
/// a diagram, and a click did nothing.
/// </summary>
public class CommandStateTests
{
    private static void OnUiThread(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void Without_a_document_the_check_commands_are_greyed_out()
    {
        bool again = true, upgrade = true, svg = true;
        OnUiThread(() =>
        {
            // Shown, as in the editor: the state is set when the view is loaded.
            var plugin = new OpcUaPlugin();
            var window = new System.Windows.Window { Content = plugin, Width = 900, Height = 700, ShowInTaskbar = false, Left = -4000 };
            window.Show();
            again = plugin.CheckAgainButton.IsEnabled;
            upgrade = plugin.UpgradeButton.IsEnabled;
            svg = plugin.DiagramExportButton.IsEnabled;
            window.Close();
        });

        Assert.False(again);
        Assert.False(upgrade);
        Assert.False(svg);
    }

    [Fact]
    public void With_a_document_the_check_commands_work_and_SVG_waits_for_a_diagram()
    {
        bool again = false, upgrade = false, svg = true;
        OnUiThread(() =>
        {
            var plugin = new OpcUaPlugin();
            plugin.DocumentLoaded(CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX3_0));
            again = plugin.CheckAgainButton.IsEnabled;
            upgrade = plugin.UpgradeButton.IsEnabled;
            svg = plugin.DiagramExportButton.IsEnabled;
        });

        Assert.True(again);
        Assert.True(upgrade);
        Assert.False(svg);
    }
}
