using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Aml.Editor.Plugin.OpcUa;
using Aml.Engine.CAEX;
using OpcUaAml.Server;
using static PluginTests.LessonHarness;

namespace PluginTests;

/// <summary>
/// Commands of the Server tab against the practice plant, as the review of
/// 2026-11-18 found them: work that went on with another document, a command
/// that cleared the busy state of another, commands enabled for a method.
/// </summary>
public class ServerCommandGuardTests
{
    /// <summary>The plugin connected to a practice plant of its own, in a window, the address space loaded.</summary>
    private static async Task<(OpcUaPlugin Plugin, UaClient Client)> Connected(Window window, CAEXDocument document, string pki)
    {
        var plugin = new OpcUaPlugin();
        window.Content = plugin;
        window.Show();
        plugin.DocumentLoaded(document);
        await plugin.StartPracticePlantAsync(FreePort(), Path.Combine(pki, "server"));
        var url = plugin.PracticePlant!.EndpointUrl;
        var client = await UaClient.ConnectAsync(new UaConnectOptions { EndpointUrl = url, UseSecurity = false, PkiRoot = Path.Combine(pki, "client") });
        await plugin.AttachClientAsync(client, url);
        await plugin.LoadRootAsync();
        return (plugin, client);
    }

    private static async Task<TreeViewItem> PartsProduced(OpcUaPlugin plugin)
    {
        var mps = plugin.AddressTree.Items.OfType<TreeViewItem>().First(i => i.Tag is UaBrowseItem { DisplayName: "MPS500" });
        return await Child(await Child(await Child(mps, "ST30_Processing"), "ParameterSet"), "PartsProduced");
    }

    private static CAEXDocument PlantDocument(out InternalElementType parts)
    {
        var document = CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX3_0);
        parts = document.CAEXFile.InstanceHierarchy.Append("Plant").InternalElement.Append("Station").InternalElement.Append("PartsProduced");
        return document;
    }

    /// <summary>The windows of this thread that are open now: a dialog shown with ShowDialog among them.</summary>
    private static List<Window> OpenWindows() =>
        PresentationSource.CurrentSources.OfType<HwndSource>().Select(s => s.RootVisual).OfType<Window>().Where(w => w.IsVisible).ToList();

    private static void Run(Func<Window, string, Task> body)
    {
        ProvideConverter();
        var pki = Directory.CreateTempSubdirectory("opcuaaml-guard-pki-").FullName;
        try
        {
            OnUiThread(async () =>
            {
                var window = new Window { Width = 900, Height = 800, ShowInTaskbar = false, Left = -4000 };
                try { await body(window, pki); }
                finally
                {
                    if (window.Content is OpcUaPlugin plugin) await plugin.StopPracticePlantAsync();
                    window.Close();
                }
            });
        }
        finally
        {
            try { Directory.Delete(pki, recursive: true); } catch (IOException) { }
        }
    }

    // Bind model… read the server's variables, then offered and bound the models of
    // the document open when it started, though another was open by then.
    [Fact]
    public void Bind_model_started_before_another_document_was_opened_offers_nothing()
    {
        var offered = new List<string>();
        Run(async (window, pki) =>
        {
            var (plugin, _) = await Connected(window, PlantDocument(out _), pki);
            Click(plugin.BindModelButton);
            plugin.DocumentLoaded(CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX3_0));
            await Until(() => plugin.Busy.Visibility != Visibility.Visible, 30);
            await Task.Delay(800);
            foreach (var w in OpenWindows().Where(w => w != window))
            {
                offered.Add(w.GetType().Name);
                w.Close();
            }
        });

        Assert.Empty(offered);
    }

    // Write value… read the variable without the busy state, so another command could
    // start meanwhile, and cleared that command's busy state when it ended.
    [Fact]
    public void Write_value_holds_the_busy_state_while_it_reads_the_variable()
    {
        bool busyWhileReading = false, freeAfter = false;
        string status = "";
        Run(async (window, pki) =>
        {
            var (plugin, _) = await Connected(window, PlantDocument(out _), pki);
            (await PartsProduced(plugin)).IsSelected = true;
            await Task.Delay(300);

            Click(plugin.WriteButton);
            busyWhileReading = plugin.Busy.Visibility == Visibility.Visible && !plugin.MirrorButton.IsEnabled;
            await Until(() => plugin.Busy.Visibility != Visibility.Visible, 30);
            freeAfter = plugin.MirrorButton.IsEnabled;
            status = plugin.StatusText.Text;
        });

        Assert.True(busyWhileReading);
        Assert.True(freeAfter, status);
        Assert.Contains("only be read", status);
    }

    // Check server put its findings beside the Trust signer… of a Descriptor checked before.
    [Fact]
    public void Check_server_takes_away_the_trust_offer_of_a_Descriptor_checked_before()
    {
        var trust = Visibility.Visible;
        Run(async (window, pki) =>
        {
            var (plugin, _) = await Connected(window, PlantDocument(out _), pki);
            plugin.TrustSignerButton.Visibility = Visibility.Visible;

            Click(plugin.CheckServerButton);
            await Until(() => plugin.Busy.Visibility != Visibility.Visible, 60);
            trust = plugin.TrustSignerButton.Visibility;
        });

        Assert.Equal(Visibility.Collapsed, trust);
    }

    // With the methods in the address space, Bind to element… bound an element to a method,
    // and Count nodes was enabled but did nothing.
    [Fact]
    public void A_method_selected_enables_no_command_meant_for_objects_and_variables()
    {
        bool bind = true, count = true, call = false;
        Run(async (window, pki) =>
        {
            var (plugin, _) = await Connected(window, PlantDocument(out _), pki);
            plugin.ShowServerBox.IsChecked = true;
            await plugin.LoadRootAsync();
            var server = plugin.AddressTree.Items.OfType<TreeViewItem>().First(i => i.Tag is UaBrowseItem { DisplayName: "Server" });
            var file = await Child(await Child(await Child(server, "Namespaces"), "http://hsu-hh.de/UA/MPS500/"), "NamespaceFile");
            (await Child(file, "Open")).IsSelected = true;
            await Until(() => plugin.CallButton.IsEnabled, 10);
            (bind, count, call) = (plugin.BindButton.IsEnabled, plugin.PreviewButton.IsEnabled, plugin.CallButton.IsEnabled);
        });

        Assert.True(call);
        Assert.False(bind);
        Assert.False(count);
    }

    // Another document opened while live values were being set up: the command stayed
    // disabled until something else refreshed the state.
    [Fact]
    public void Keep_live_is_offered_again_when_another_document_was_opened_while_it_started()
    {
        bool offered = false;
        Run(async (window, pki) =>
        {
            var (plugin, client) = await Connected(window, PlantDocument(out var parts), pki);
            var variable = await PartsProduced(plugin);
            OpcUaPlugin.Bind(parts, ((UaBrowseItem)variable.Tag).Address with { ServerUri = client.ServerUri }, "Variable");

            Click(plugin.LiveButton);
            plugin.DocumentLoaded(PlantDocument(out _));
            await Task.Delay(3000);
            offered = plugin.LiveButton.IsEnabled;
        });

        Assert.True(offered);
    }
}
