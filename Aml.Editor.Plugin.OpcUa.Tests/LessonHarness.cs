using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Aml.Editor.Plugin.OpcUa.Localization;
using OpcUaAml.Server;

namespace PluginTests;

/// <summary>What the tests that walk a lesson of the tutorial share: a UI thread, the converter, the practice plant's port, the address tree.</summary>
internal static class LessonHarness
{
    /// <summary>Runs <paramref name="body"/> on an STA thread with a dispatcher, in English.</summary>
    public static void OnUiThread(Func<Task> body)
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
                // A body that throws before its first await must fail the test, not the test host.
                Task task;
                try { task = body(); }
                catch (Exception ex) { task = Task.FromException(ex); }
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
    public static void ProvideConverter()
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

    public static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public static IEnumerable<DependencyObject> Logical(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var d in Logical(child)) yield return d;
        }
    }

    public static void Click(System.Windows.Controls.Primitives.ButtonBase button) =>
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    public static async Task Until(Func<bool> condition, int seconds = 60)
    {
        for (var i = 0; i < seconds * 10 && !condition(); i++) await Task.Delay(100);
    }

    /// <summary>The child of <paramref name="parent"/> shown as <paramref name="name"/>, expanding it first.</summary>
    public static async Task<TreeViewItem> Child(TreeViewItem parent, string name)
    {
        parent.IsExpanded = true;
        await Until(() => parent.Items.Count > 0 && parent.Items[0] is not string, 10);
        return parent.Items.OfType<TreeViewItem>().FirstOrDefault(i => i.Tag is UaBrowseItem b && b.DisplayName == name)
               ?? throw new InvalidOperationException($"no {name} below {(parent.Tag as UaBrowseItem)?.DisplayName}");
    }

    /// <summary>The first variable below <paramref name="parent"/>, expanding the tree breadth first.</summary>
    public static async Task<TreeViewItem?> FirstVariable(TreeViewItem parent, int depth)
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
}
