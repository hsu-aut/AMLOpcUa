using System.Diagnostics;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Aml.Editor.Plugin.OpcUa;
using Aml.Editor.Plugin.OpcUa.Localization;

namespace PluginTests;

/// <summary>
/// Long operations (a conversion, a download, connecting, taking nodes) showed
/// only the busy bar: a server that does not answer, or a slow network at a
/// trade fair, meant waiting for the stack's timeout with no way out.
/// </summary>
public class CancelTests
{
    [Fact]
    public void Connecting_to_a_server_that_does_not_answer_can_be_cancelled()
    {
        string? status = null;
        bool cancelVisible = false, cancelHiddenAfter = false;
        TimeSpan took = TimeSpan.Zero;
        Exception? failure = null;
        var german = L.German;
        L.German = false;
        try
        {
            var thread = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var frame = new DispatcherFrame();
                async Task Run()
                {
                    var plugin = new OpcUaPlugin();
                    // An address no packet comes back from: connecting waits for the timeout.
                    plugin.EndpointBox.Text = "opc.tcp://10.255.255.1:4840";
                    plugin.SecurityToggle.IsChecked = false;
                    var clock = Stopwatch.StartNew();
                    plugin.ConnectButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Task.Delay(500);
                    cancelVisible = plugin.CancelLink.Visibility == Visibility.Visible;
                    plugin.CancelLink.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    for (var i = 0; i < 300 && plugin.Busy.Visibility == Visibility.Visible; i++) await Task.Delay(50);
                    took = clock.Elapsed;
                    status = plugin.StatusText.Text;
                    cancelHiddenAfter = plugin.CancelLink.Visibility != Visibility.Visible;
                }
                var task = Run();
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

        Assert.True(cancelVisible, "no Cancel while connecting");
        Assert.True(took < TimeSpan.FromSeconds(5), $"cancelling took {took.TotalSeconds:0.0} s");
        Assert.Equal("Connecting to opc.tcp://10.255.255.1:4840 cancelled.", status);
        Assert.True(cancelHiddenAfter);
    }
}
