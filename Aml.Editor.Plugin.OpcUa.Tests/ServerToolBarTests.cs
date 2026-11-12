using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Aml.Editor.Plugin.OpcUa;
using Aml.Editor.Plugin.OpcUa.Localization;

namespace PluginTests;

/// <summary>
/// The Server tab's tool bar beside the editor's trees (the default layout).
/// Seen in the editor: "Keep document values live", step 5 of the booth run,
/// sat in the tool bar's overflow, behind a small arrow nobody at a booth finds.
/// </summary>
public class ServerToolBarTests
{
    [Theory]
    [InlineData(600)]
    [InlineData(700)]
    [InlineData(885)]
    public void The_commands_of_the_booth_run_are_never_in_the_overflow(double width)
    {
        var overflowing = new List<string>();
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
                    var window = new Window { Content = plugin, Width = width, Height = 800, ShowInTaskbar = false, Left = -4000 };
                    window.Show();
                    plugin.ServerTab.IsSelected = true;
                    window.Dispatcher.Invoke(window.UpdateLayout, DispatcherPriority.Loaded);
                    window.Dispatcher.Invoke(window.UpdateLayout, DispatcherPriority.Loaded);
                    foreach (var button in new[] { plugin.ServerTypesButton, plugin.MirrorButton, plugin.LiveButton })
                        if (ToolBar.GetIsOverflowItem(button)) overflowing.Add(button.Name);
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

        Assert.Empty(overflowing);
    }
}
