using System.IO;
using System.Windows;
using System.Windows.Controls;
using Aml.Editor.Plugin.OpcUa;
using Aml.Engine.CAEX;
using OpcUaAml.Server;
using static PluginTests.LessonHarness;

namespace PluginTests;

/// <summary>
/// The lesson on the practice plant, walked the way a visitor at the booth
/// walks it: each step ticks itself off when the plugin's state shows it
/// done, so every step must be reachable and its check must see it.
/// </summary>
public class PlantLessonTests
{
    [Fact]
    public void The_practice_plant_lesson_can_be_walked_to_its_end()
    {
        ProvideConverter();
        var pki = Directory.CreateTempSubdirectory("opcuaaml-lesson-pki-").FullName;
        var done = new List<string>();
        string? notDone = null;
        try
        {
            OnUiThread(async () =>
            {
                var plugin = new OpcUaPlugin();
                var steps = plugin.TourLessons().Single(l => l.Id == "plant").Steps;
                bool Done(int i) => plugin.TourLessons().Single(l => l.Id == "plant").Steps[i].Done?.Invoke() == true;
                void Check(int i, string what)
                {
                    if (Done(i)) done.Add(what);
                    else notDone ??= $"step {i + 1} ({what}) did not tick off; status: {plugin.StatusText.Text}";
                }
                try
                {
                    Assert.False(Done(0));
                    var document = CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX3_0);
                    plugin.DocumentLoaded(document);
                    Check(0, "document");

                    Assert.False(Done(1));
                    await plugin.StartPracticePlantAsync(FreePort(), Path.Combine(pki, "server"));
                    Check(1, "plant");

                    Assert.False(Done(2));
                    var url = plugin.PracticePlant!.EndpointUrl;
                    var client = await UaClient.ConnectAsync(new UaConnectOptions { EndpointUrl = url, UseSecurity = false, PkiRoot = Path.Combine(pki, "client") });
                    await plugin.AttachClientAsync(client, url);
                    Check(2, "connected");

                    // Types of the server… is a dialog; behind it, the plant's NodeSet is imported.
                    Assert.False(Done(3));
                    await plugin.ImportFileAsync(document, OpcUaPlugin.PracticePlantFile, Array.Empty<string>());
                    Check(3, "types");

                    Assert.False(Done(4));
                    await plugin.LoadRootAsync();
                    var mps = plugin.AddressTree.Items.OfType<TreeViewItem>().First(i => i.Tag is UaBrowseItem { DisplayName: "MPS500" });
                    var box = Logical(mps).OfType<CheckBox>().First();
                    ((System.Windows.Automation.Provider.IToggleProvider)System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(box)).Toggle();
                    Check(4, "ticked");

                    Assert.False(Done(5));
                    Click(plugin.MirrorButton);
                    await Task.Delay(200);
                    await Until(() => plugin.Busy.Visibility != Visibility.Visible, 120);
                    Check(5, "taken");

                    Assert.False(Done(6));
                    Click(plugin.LiveButton);
                    await Until(() => Done(6), 30);
                    Check(6, "live");

                    Assert.False(Done(7));
                    // Taking reloads the tree; the item ticked before is gone.
                    mps = plugin.AddressTree.Items.OfType<TreeViewItem>().First(i => i.Tag is UaBrowseItem { DisplayName: "MPS500" });
                    var variable = await FirstVariable(mps, 4) ?? throw new InvalidOperationException("no variable below MPS500");
                    variable.IsSelected = true;
                    await Task.Delay(500);
                    Click(plugin.WatchButton);
                    await Until(() => Done(7), 30);
                    Check(7, "watched");

                    Assert.False(Done(8));
                    await plugin.StopPracticePlantAsync();
                    Check(8, "stopped");
                    Assert.Equal(9, steps.Count);
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

        Assert.True(notDone == null, notDone + "; done: " + string.Join(", ", done));
    }
}
