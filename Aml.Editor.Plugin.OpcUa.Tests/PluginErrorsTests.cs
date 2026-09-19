using System.Windows.Threading;
using Aml.Editor.Plugin.OpcUa.Diagnostics;
using OpcUaAml.Addressing;

namespace PluginTests;

public class PluginErrorsTests
{
    private static Exception Caught(Action action)
    {
        try { action(); }
        catch (Exception ex) { return ex; }
        throw new InvalidOperationException("nothing thrown");
    }

    [Fact]
    public void Tells_the_plugins_exceptions_from_others()
    {
        var ours = Caught(() => UaNodeAddress.Parse("no address"));
        var theirs = Caught(() => throw new InvalidOperationException("not the plugin"));

        Assert.True(PluginErrors.IsOurs(ours));
        Assert.True(PluginErrors.IsOurs(new System.Reflection.TargetInvocationException(ours)));
        Assert.False(PluginErrors.IsOurs(theirs));
    }

    [Fact]
    public void An_async_handler_that_throws_is_reported_and_the_dispatcher_goes_on()
    {
        Exception? reported = null;
        bool? othersHandled = null;
        var ranAfter = false;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            PluginErrors.Install(dispatcher, ex => reported = ex);
            // Registered after the plugin's: sees whether it took the exception, and takes the others so the test lives.
            dispatcher.UnhandledException += (_, e) => { othersHandled ??= e.Handled; e.Handled = true; };

            async void Handler()
            {
                await Task.Yield();
                UaNodeAddress.Parse("no address");
            }

            dispatcher.BeginInvoke(Handler);
            dispatcher.BeginInvoke(() => ranAfter = true, DispatcherPriority.Background);
            dispatcher.BeginInvoke(() => dispatcher.InvokeShutdown(), DispatcherPriority.SystemIdle);
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        Assert.NotNull(reported);
        Assert.True(othersHandled);
        Assert.True(ranAfter);
    }
}
