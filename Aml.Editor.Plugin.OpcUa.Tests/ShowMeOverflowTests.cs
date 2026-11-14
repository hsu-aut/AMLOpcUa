using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Aml.Editor.Plugin.OpcUa;
using Aml.Editor.Plugin.OpcUa.Localization;

namespace PluginTests;

/// <summary>
/// "Show me" in the tutorial frames the button a step means. With the plugin
/// narrow, the toolbar puts its last buttons into its overflow, and Show me
/// found nothing to frame: lesson 3, step 8 (Watch) seen in the editor.
/// </summary>
public class ShowMeOverflowTests
{
    [Fact]
    public void Show_me_opens_the_toolbar_overflow_that_holds_the_button()
    {
        bool overflowed = false, visible = false;
        Exception? failure = null;
        var german = L.German;
        L.German = false;
        try
        {
            var thread = new Thread(() =>
            {
                try
                {
                    var plugin = new OpcUaPlugin();
                    var window = new Window { Content = plugin, Width = 700, Height = 800, ShowInTaskbar = false, Left = -4000 };
                    window.Show();
                    plugin.ServerTab.IsSelected = true;
                    window.Dispatcher.Invoke(window.UpdateLayout, DispatcherPriority.Loaded);
                    window.Dispatcher.Invoke(window.UpdateLayout, DispatcherPriority.Loaded);
                    overflowed = ToolBar.GetIsOverflowItem(plugin.WatchButton);

                    plugin.ShowMe("WatchButton", "ServerTab");
                    window.Dispatcher.Invoke(window.UpdateLayout, DispatcherPriority.Loaded);
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    visible = plugin.WatchButton.IsVisible;
                    window.Close();
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
        }
        finally { L.German = german; }
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());

        Assert.True(overflowed, "at 700 px Watch is expected in the overflow; the test no longer shows the case");
        Assert.True(visible, "Show me left Watch hidden in the closed overflow");
    }
}
