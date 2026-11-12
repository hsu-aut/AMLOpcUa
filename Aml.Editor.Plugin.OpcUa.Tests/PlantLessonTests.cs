using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Aml.Editor.Plugin.OpcUa;
using Aml.Editor.Plugin.OpcUa.Localization;
using Aml.Engine.CAEX;
using OpcUaAml.Server;

namespace PluginTests;

/// <summary>
/// The lesson on the practice plant, walked the way a visitor at the booth
/// walks it: each step ticks itself off when the plugin's state shows it
/// done, so every step must be reachable and its check must see it.
/// </summary>
public class PlantLessonTests
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
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
        }
        finally { L.German = german; }
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    /// <summary>
    /// The plugin runs the converter beside its assembly, which here is the test
    /// folder; it comes from the plugin's build output, once.
    /// </summary>
    private static void ProvideConverter()
    {
        var target = Path.Combine(AppContext.BaseDirectory, "converter");
        if (File.Exists(Path.Combine(target, "uaaml.exe"))) return;
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository != null && !File.Exists(Path.Combine(repository.FullName, "AMLOpcUa.sln"))) repository = repository.Parent;
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        var source = Path.Combine(repository!.FullName, "build", "Plugins", "Aml.Editor.Plugin.OpcUa", configuration, "net8.0-windows7.0", "converter");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to, overwrite: true);
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static IEnumerable<DependencyObject> Logical(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var d in Logical(child)) yield return d;
        }
    }

    private static void Click(System.Windows.Controls.Primitives.ButtonBase button) =>
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    private static async Task Until(Func<bool> condition, int seconds = 60)
    {
        for (var i = 0; i < seconds * 10 && !condition(); i++) await Task.Delay(100);
    }

    /// <summary>The first variable below <paramref name="parent"/>, expanding the tree breadth first.</summary>
    private static async Task<TreeViewItem?> FirstVariable(TreeViewItem parent, int depth)
    {
        // Expanding empties the item at once and fills it when the server answers.
        parent.IsExpanded = true;
        await Until(() => parent.Items.Count > 0 && parent.Items[0] is not string, 10);
        var items = parent.Items.OfType<TreeViewItem>().ToList();
        if (items.FirstOrDefault(i => i.Tag is UaBrowseItem { NodeClass: "Variable" }) is { } variable) return variable;
        if (depth == 0) return null;
        foreach (var item in items)
            if (await FirstVariable(item, depth - 1) is { } found) return found;
        return null;
    }

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
