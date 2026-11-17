using System.IO;
using System.Windows;
using System.Windows.Controls;
using Aml.Editor.Plugin.OpcUa;
using Aml.Engine.CAEX;
using OpcUaAml.Server;
using static PluginTests.LessonHarness;

namespace PluginTests;

/// <summary>
/// The plugin closed while the watch of a value was being set up: the
/// subscription that came back after the close had no one left to end it, or
/// its failure on the closed session was reported to a closed plugin.
/// </summary>
public class ClosedWhileWatchingTests
{
    [Fact]
    public void A_watch_that_comes_back_after_the_plugin_closed_is_ended_quietly()
    {
        ProvideConverter();
        var pki = Directory.CreateTempSubdirectory("opcuaaml-closed-watch-pki-").FullName;
        bool watching = true;
        string status = "";
        try
        {
            OnUiThread(async () =>
            {
                var plugin = new OpcUaPlugin();
                try
                {
                    plugin.DocumentLoaded(CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX3_0));
                    await plugin.StartPracticePlantAsync(FreePort(), Path.Combine(pki, "server"));
                    var url = plugin.PracticePlant!.EndpointUrl;
                    var client = await UaClient.ConnectAsync(new UaConnectOptions { EndpointUrl = url, UseSecurity = false, PkiRoot = Path.Combine(pki, "client") });
                    await plugin.AttachClientAsync(client, url);
                    await plugin.LoadRootAsync();
                    var mps = plugin.AddressTree.Items.OfType<TreeViewItem>().First(i => i.Tag is UaBrowseItem { DisplayName: "MPS500" });
                    var cycleTime = await Child(await Child(await Child(mps, "ST30_Processing"), "ParameterSet"), "CycleTime");
                    cycleTime.IsSelected = true;
                    await Until(() => plugin.WatchButton.IsEnabled, 10);
                    var before = plugin.StatusText.Text;

                    // Watch starts the subscription and waits for the server; the plugin closes meanwhile.
                    Click(plugin.WatchButton);
                    plugin.TerminatePlugin.Command.Execute(null);
                    await Until(() => plugin.WatchSettled, 30);

                    watching = plugin.Watching;
                    status = plugin.StatusText.Text == before ? "" : plugin.StatusText.Text;
                }
                finally
                {
                    plugin.ActivatePlugin.Command.Execute(null);
                    await plugin.StopPracticePlantAsync();
                }
            });
        }
        finally
        {
            try { Directory.Delete(pki, recursive: true); } catch (IOException) { }
        }

        Assert.False(watching);
        Assert.DoesNotContain("Watching failed", status);
    }
}
