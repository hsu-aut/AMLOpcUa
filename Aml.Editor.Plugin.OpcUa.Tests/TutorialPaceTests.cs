using System.Windows.Controls;
using System.Windows.Threading;
using Aml.Editor.Plugin.OpcUa;
using Aml.Engine.CAEX;
using static PluginTests.LessonHarness;

namespace PluginTests;

/// <summary>
/// In the editor, Take into document with typed nodes kept the dispatcher busy
/// for half a minute and more; the tutorial checked its steps at Background
/// priority and ticked "Take into document" off a minute late.
/// </summary>
public class TutorialPaceTests
{
    [Fact]
    public void A_step_ticks_off_while_the_editor_keeps_the_dispatcher_busy()
    {
        string? shown = null;
        OnUiThread(async () =>
        {
            var plugin = new OpcUaPlugin();
            plugin.StartLesson("models");
            // Step 1 waits for a CAEX 3.0 document; it comes while the editor is busy.
            plugin.DocumentLoaded(CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX3_0));

            // The editor's work after a large change: one item after the other at Normal priority, for three seconds.
            var dispatcher = Dispatcher.CurrentDispatcher;
            var until = DateTime.UtcNow.AddSeconds(3);
            var done = new TaskCompletionSource();
            void Work()
            {
                Thread.Sleep(20);
                if (DateTime.UtcNow < until) dispatcher.BeginInvoke(DispatcherPriority.Normal, Work);
                else
                {
                    shown = plugin.TourBody.Children.OfType<TextBlock>().Select(t => t.Text).FirstOrDefault(t => t.StartsWith("Step "));
                    done.SetResult();
                }
            }
            dispatcher.BeginInvoke(DispatcherPriority.Normal, Work);
            await done.Task;
        });
        Assert.Equal("Step 2 of 5", shown);
    }
}
