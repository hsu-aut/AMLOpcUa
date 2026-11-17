using System.IO;
using System.Windows.Controls;
using Aml.Editor.Plugin.OpcUa;
using Aml.Engine.CAEX;
using OpcUaAml.Server;
using static PluginTests.LessonHarness;

namespace PluginTests;

/// <summary>
/// Call method… is enabled for a method selected in the address space, but
/// the address space showed none: it was filtered with what a mirror takes,
/// and a mirror takes no methods (editor round all10, 2026-11-17).
/// </summary>
public class MethodsInTreeTests
{
    [Fact]
    public void A_method_shows_in_the_address_space_and_can_be_called()
    {
        ProvideConverter();
        var pki = Directory.CreateTempSubdirectory("opcuaaml-methods-pki-").FullName;
        List<string> children = new();
        bool callable = false;
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
                    plugin.ShowServerBox.IsChecked = true;
                    await plugin.LoadRootAsync();
                    var server = plugin.AddressTree.Items.OfType<TreeViewItem>().First(i => i.Tag is UaBrowseItem { DisplayName: "Server" });
                    var file = await Child(await Child(await Child(server, "Namespaces"), "http://hsu-hh.de/UA/MPS500/"), "NamespaceFile");
                    var open = await Child(file, "Open");
                    children = file.Items.OfType<TreeViewItem>().Select(i => ((UaBrowseItem)i.Tag).DisplayName).ToList();

                    open.IsSelected = true;
                    await Until(() => plugin.CallButton.IsEnabled, 10);
                    callable = plugin.CallButton.IsEnabled;
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

        Assert.Contains("Open", children);
        Assert.Contains("Read", children);
        Assert.True(callable);
    }
}
