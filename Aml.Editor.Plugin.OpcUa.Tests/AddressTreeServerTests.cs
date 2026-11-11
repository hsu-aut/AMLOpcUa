using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows.Controls;
using System.Windows.Threading;
using Aml.Editor.Plugin.OpcUa;
using Aml.Editor.Plugin.OpcUa.Localization;
using OpcUaAml.Server;

namespace PluginTests;

/// <summary>
/// The address tree against a running server: the MPS 500 the plugin brings,
/// on a free port, with certificates in a folder of the test's own.
/// </summary>
public class AddressTreeServerTests
{
    private static void OnUiThread(Func<Task> body)
    {
        Exception? failure = null;
        var german = L.German;
        L.German = false;
        try
        {
            var thread = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var frame = new DispatcherFrame();
                var task = body();
                task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
                Dispatcher.PushFrame(frame);
                try { task.GetAwaiter().GetResult(); }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
        }
        finally { L.German = german; }
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>The plant served and a plugin connected to it, unsecured.</summary>
    private static async Task<(NodeSetServerHost Plant, OpcUaPlugin Plugin)> ConnectedAsync(string pki)
    {
        var plant = await NodeSetServerHost.StartAsync(new[] { OpcUaPlugin.PracticePlantFile },
            new NodeSetServerOptions { Port = FreePort(), PkiRoot = Path.Combine(pki, "server") });
        var client = await UaClient.ConnectAsync(new UaConnectOptions
        {
            EndpointUrl = plant.EndpointUrl, UseSecurity = false, PkiRoot = Path.Combine(pki, "client"),
        });
        var plugin = new OpcUaPlugin();
        await plugin.AttachClientAsync(client, plant.EndpointUrl);
        return (plant, plugin);
    }

    private static List<string> Roots(OpcUaPlugin plugin) =>
        plugin.AddressTree.Items.OfType<TreeViewItem>().Select(i => System.Windows.Automation.AutomationProperties.GetName(i)).ToList();

    [Fact]
    public void Loading_the_tree_twice_at_once_shows_each_root_once()
    {
        // Two filter boxes clicked quickly, or F5 twice: each load cleared the
        // tree, waited for the server, then added its roots.
        var pki = Directory.CreateTempSubdirectory("opcuaaml-tree-pki-").FullName;
        List<string>? roots = null;
        try
        {
            OnUiThread(async () =>
            {
                var (plant, plugin) = await ConnectedAsync(pki);
                try
                {
                    await Task.WhenAll(plugin.LoadRootAsync(), plugin.LoadRootAsync());
                    roots = Roots(plugin);
                }
                finally
                {
                    await plugin.DisconnectAsync();
                    await plant.DisposeAsync();
                }
            });
        }
        finally { Directory.Delete(pki, recursive: true); }

        Assert.NotEmpty(roots!);
        Assert.Equal(roots!.Distinct().Count(), roots!.Count);
    }

    [Fact]
    public void A_node_that_could_not_be_expanded_can_be_expanded_again()
    {
        // The node was emptied before browsing, and a failed browse left it
        // empty for good, without a word.
        var pki = Directory.CreateTempSubdirectory("opcuaaml-tree-pki-").FullName;
        bool expandable = false, collapsed = false;
        string? status = null;
        try
        {
            OnUiThread(async () =>
            {
                var (plant, plugin) = await ConnectedAsync(pki);
                try
                {
                    await plugin.LoadRootAsync();
                    var item = plugin.AddressTree.Items.OfType<TreeViewItem>().First(i => i.Items.Count == 1 && i.Items[0] is string);
                    await plant.DisposeAsync();

                    item.IsExpanded = true;
                    for (var i = 0; i < 300 && item.IsExpanded; i++) await Task.Delay(100);

                    expandable = item.Items.Count == 1 && item.Items[0] is string;
                    collapsed = !item.IsExpanded;
                    status = plugin.StatusText.Text;
                }
                finally
                {
                    await plugin.DisconnectAsync();
                }
            });
        }
        finally { Directory.Delete(pki, recursive: true); }

        Assert.True(expandable, "the node has no placeholder for its children any more");
        Assert.True(collapsed);
        Assert.StartsWith("The server did not answer", status);
    }

    [Fact]
    public void Taking_nodes_that_fails_leaves_no_empty_hierarchy_behind()
    {
        // The hierarchy was made before the server was read; when reading
        // failed (the server had stopped), an empty "OpcUaServer" stayed.
        var pki = Directory.CreateTempSubdirectory("opcuaaml-tree-pki-").FullName;
        List<string>? hierarchies = null;
        string? status = null;
        try
        {
            OnUiThread(async () =>
            {
                var (plant, plugin) = await ConnectedAsync(pki);
                try
                {
                    var document = Aml.Engine.CAEX.CAEXDocument.New_CAEXDocument(Aml.Engine.CAEX.CAEXDocument.CAEXSchema.CAEX3_0);
                    plugin.DocumentLoaded(document);
                    var item = plugin.AddressTree.Items.OfType<TreeViewItem>().First();
                    var box = LogicalTreeHelper(item).OfType<CheckBox>().First();
                    ((System.Windows.Automation.Provider.IToggleProvider)System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(box)).Toggle();
                    await plant.DisposeAsync();

                    plugin.MirrorButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    await Task.Delay(200);
                    for (var i = 0; i < 300 && plugin.Busy.Visibility == System.Windows.Visibility.Visible; i++) await Task.Delay(100);

                    hierarchies = document.CAEXFile.InstanceHierarchy.Select(h => h.Name).ToList();
                    status = plugin.StatusText.Text;
                }
                finally
                {
                    await plugin.DisconnectAsync();
                }
            });
        }
        finally { Directory.Delete(pki, recursive: true); }

        Assert.StartsWith("Taking into the document failed", status);
        Assert.Empty(hierarchies!);
    }

    private static IEnumerable<System.Windows.DependencyObject> LogicalTreeHelper(System.Windows.DependencyObject root)
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<System.Windows.DependencyObject>())
        {
            yield return child;
            foreach (var d in LogicalTreeHelper(child)) yield return d;
        }
    }
}
