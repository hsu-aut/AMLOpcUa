using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Aml.Editor.Plugin.OpcUa;
using static PluginTests.LessonHarness;

namespace PluginTests;

/// <summary>
/// A menu button the narrow tool bar put into its overflow: clicked there, the
/// tool bar closed the overflow, and the menu the button had just opened went
/// with it, so "UAFX Descriptor ▾" did nothing (editor round all7, 2026-11-17).
/// </summary>
public class OverflowMenuTests
{
    [Fact]
    public void The_menu_of_a_button_in_the_overflow_opens()
    {
        bool overflowed = false, open = false;
        OnUiThread(async () =>
        {
            var plugin = new OpcUaPlugin();
            var window = new Window { Content = plugin, Width = 420, Height = 800, ShowInTaskbar = false, Left = -4000 };
            window.Show();
            try
            {
                plugin.Tabs.SelectedItem = plugin.NamespacesTab;
                // In the overflow whatever the width, as a narrow pane puts it there.
                ToolBar.SetOverflowMode(plugin.DescriptorMenuButton, OverflowMode.Always);
                await Task.Delay(500);
                var toolBar = (ToolBar)ItemsControl.ItemsControlFromItemContainer(plugin.DescriptorMenuButton);
                overflowed = ToolBar.GetIsOverflowItem(plugin.DescriptorMenuButton);
                toolBar.IsOverflowOpen = true;
                await Task.Delay(500);

                plugin.DescriptorMenuButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Task.Delay(800);
                open = plugin.DescriptorMenu.IsOpen;
                plugin.DescriptorMenu.IsOpen = false;
            }
            finally { window.Close(); }
        });

        Assert.True(overflowed);
        Assert.True(open);
    }
}
