using System.IO;
using System.Windows.Threading;
using Aml.Editor.Plugin.OpcUa.Bridge;

namespace PluginTests;

/// <summary>
/// The modeler that could not start: the reason was shown once, a later visit
/// to the tab never tried again, and "Edit" then said "Opened …" over it
/// although nothing was shown.
/// </summary>
public class ModelerStartTests
{
    private static void OnUiThread(Func<Task> body)
    {
        Exception? failure = null;
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
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void A_modeler_that_could_not_start_tries_again_and_is_not_available()
    {
        var empty = Directory.CreateTempSubdirectory("opcuaaml-no-modeler-").FullName;
        var errors = new List<string>();
        bool available = true;
        try
        {
            OnUiThread(async () =>
            {
                using var modeler = new ModelerWebView(new PageKeysWebView(), empty);
                modeler.Error += errors.Add;
                await modeler.InitAsync();
                await modeler.InitAsync();
                available = modeler.Available;
            });
        }
        finally { Directory.Delete(empty, recursive: true); }

        Assert.Equal(2, errors.Count);
        Assert.Contains(empty, errors[0]);
        Assert.False(available);
    }
}
