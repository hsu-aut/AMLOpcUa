using System.IO;
using System.Windows.Controls;
using Aml.Editor.Plugin.OpcUa;
using Aml.Engine.CAEX;
using OpcUaAml.Server;
using static PluginTests.LessonHarness;

namespace PluginTests;

/// <summary>
/// Save values… stayed disabled after Watch: the button's state was worked out
/// again only when something else changed, so the values just watched could
/// not be saved until another node was selected (editor round all7, 2026-11-17).
/// </summary>
public class WatchSaveTests
{
    [Fact]
    public void Save_values_is_enabled_once_a_value_is_watched()
    {
        ProvideConverter();
        var pki = Directory.CreateTempSubdirectory("opcuaaml-watch-save-pki-").FullName;
        bool before = true, after = false;
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
                    before = plugin.SaveWatchButton.IsEnabled;

                    Click(plugin.WatchButton);
                    await Until(() => plugin.WatchSettled && plugin.Watching, 30);
                    after = plugin.SaveWatchButton.IsEnabled;
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

        Assert.False(before);
        Assert.True(after);
    }
}
