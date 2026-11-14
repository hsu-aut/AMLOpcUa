using System.IO;
using System.Windows;
using System.Windows.Controls;
using Aml.Editor.Plugin.OpcUa;
using Aml.Engine.CAEX;
using OpcUaAml.Server;
using static PluginTests.LessonHarness;

namespace PluginTests;

/// <summary>
/// The Node details beside the address space show a variable's value as read
/// when it was selected. With document values kept live, the value beside it
/// stood still while the document followed the plant: two different numbers
/// for the same variable.
/// </summary>
public class NodeDetailsLiveTests
{
    /// <summary>The text shown in the details' Value row, or null.</summary>
    private static string? ValueShown(OpcUaPlugin plugin)
    {
        var label = plugin.NodeDetails.Children.OfType<TextBlock>().FirstOrDefault(t => t.Text == "Value");
        if (label == null) return null;
        var row = Grid.GetRow(label);
        var cell = plugin.NodeDetails.Children.OfType<DockPanel>().FirstOrDefault(c => Grid.GetRow(c) == row);
        return cell?.Children.OfType<TextBlock>().LastOrDefault()?.Text;
    }

    [Fact]
    public void The_value_in_the_node_details_follows_live_values()
    {
        ProvideConverter();
        var pki = Directory.CreateTempSubdirectory("opcuaaml-details-pki-").FullName;
        var shown = new List<string?>();
        try
        {
            OnUiThread(async () =>
            {
                var plugin = new OpcUaPlugin();
                try
                {
                    var document = CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX3_0);
                    plugin.DocumentLoaded(document);
                    await plugin.StartPracticePlantAsync(FreePort(), Path.Combine(pki, "server"));
                    var url = plugin.PracticePlant!.EndpointUrl;
                    var client = await UaClient.ConnectAsync(new UaConnectOptions { EndpointUrl = url, UseSecurity = false, PkiRoot = Path.Combine(pki, "client") });
                    await plugin.AttachClientAsync(client, url);
                    await plugin.LoadRootAsync();

                    // One station into the document, its values kept live.
                    var mps = plugin.AddressTree.Items.OfType<TreeViewItem>().First(i => i.Tag is UaBrowseItem { DisplayName: "MPS500" });
                    var station = await Child(mps, "ST30_Processing");
                    var box = Logical(station).OfType<CheckBox>().First();
                    ((System.Windows.Automation.Provider.IToggleProvider)System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(box)).Toggle();
                    Click(plugin.MirrorButton);
                    await Task.Delay(200);
                    await Until(() => plugin.Busy.Visibility != Visibility.Visible, 120);
                    Click(plugin.LiveButton);
                    await Until(() => plugin.StatusText.Text.StartsWith("Live", StringComparison.Ordinal), 30);

                    // Taking reloads the tree: find the cycle time again and select it.
                    mps = plugin.AddressTree.Items.OfType<TreeViewItem>().First(i => i.Tag is UaBrowseItem { DisplayName: "MPS500" });
                    station = await Child(mps, "ST30_Processing");
                    var cycleTime = await Child(await Child(station, "ParameterSet"), "CycleTime");
                    cycleTime.IsSelected = true;
                    await Until(() => ValueShown(plugin) is { } v && v != "…", 10);
                    shown.Add(ValueShown(plugin));
                    await Until(() => ValueShown(plugin) != shown[0], 10);
                    shown.Add(ValueShown(plugin));
                }
                finally
                {
                    await plugin.StopPracticePlantAsync();
                }
            });
        }
        finally
        {
            try { Directory.Delete(pki, recursive: true); } catch (IOException) { }
        }

        Assert.NotNull(shown[0]);
        Assert.NotEqual(shown[0], shown[1]);
    }
}
