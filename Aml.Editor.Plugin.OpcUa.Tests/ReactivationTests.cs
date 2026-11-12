using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Threading;
using Aml.Editor.Plugin.OpcUa;
using Aml.Editor.Plugin.OpcUa.Localization;

namespace PluginTests;

/// <summary>
/// PlugIns › AMLOpcUa › Terminate closes the plugin's pane; Activate shows the
/// same plugin again, not a new one. Terminate ended the plugin for good: the
/// practice plant no longer started, and the closed modeler view, touched again
/// when the pane came back, threw on every layout pass while the editor spun.
/// </summary>
public class ReactivationTests
{
    /// <summary>Runs <paramref name="body"/> on a WPF thread whose awaits come back to it, with English texts.</summary>
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
                // Windows and the modeler's browser end with their thread's dispatcher.
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
        }
        finally { L.German = german; }
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static int SomeFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static bool Listening(int port)
    {
        try
        {
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            return true;
        }
        catch (SocketException) { return false; }
    }

    [Fact]
    public void Activated_again_after_terminate_the_plugin_starts_the_practice_plant_again()
    {
        var pki = Directory.CreateTempSubdirectory("opcuaaml-reactivate-pki-").FullName;
        var port = SomeFreePort();
        bool first = false, afterTerminate = true, again = false;
        string? button = null;
        try
        {
            OnUiThread(async () =>
            {
                var plugin = new OpcUaPlugin();
                plugin.ActivatePlugin.Command.Execute(null);
                await plugin.StartPracticePlantAsync(port, pki);
                first = Listening(port);

                plugin.TerminatePlugin.Command.Execute(null);
                afterTerminate = Listening(port);

                plugin.ActivatePlugin.Command.Execute(null);
                await plugin.StartPracticePlantAsync(port, pki);
                again = Listening(port);
                button = plugin.PracticePlantText.Text;
                await plugin.StopPracticePlantAsync();
            });
        }
        finally
        {
            Directory.Delete(pki, recursive: true);
        }

        Assert.True(first);
        Assert.False(afterTerminate);
        Assert.True(again);
        Assert.Equal("Stop practice plant", button);
    }

    [Fact]
    public void Shown_again_after_terminate_the_plugin_has_a_modeler_view_that_can_start()
    {
        object? before = null, after = null;
        OnUiThread(async () =>
        {
            var plugin = new OpcUaPlugin();
            plugin.ActivatePlugin.Command.Execute(null);
            var window = new Window
            {
                Content = plugin, Width = 900, Height = 700, ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
            };
            window.Show();
            try
            {
                // The modeler starts when its tab is first shown.
                plugin.Tabs.SelectedItem = plugin.ModelerTab;
                await Task.Delay(300);
                before = plugin.ModelerView;

                plugin.TerminatePlugin.Command.Execute(null);
                window.Content = null;          // the editor closes the pane
                window.UpdateLayout();
                plugin.ActivatePlugin.Command.Execute(null);
                window.Content = plugin;        // and shows the same view again
                window.UpdateLayout();
                await Task.Delay(300);
                after = plugin.ModelerView;
            }
            finally { window.Close(); }
        });

        Assert.NotNull(before);
        Assert.NotSame(before, after);
    }
}
