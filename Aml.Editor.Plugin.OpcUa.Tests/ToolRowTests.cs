using System.Windows;
using System.Windows.Controls;
using Aml.Editor.Plugin.OpcUa;
using static PluginTests.LessonHarness;

namespace PluginTests;

/// <summary>
/// The tool bars put what did not fit into an overflow behind a small arrow.
/// In the editor's theme that arrow is not shown to UI Automation, so a screen
/// reader could not reach Bind model…, Write value…, Call method… and the
/// rest (editor rounds all6 to all11, 2026-11-17). The rows now wrap, and the
/// commands used now and then sit behind "More ▾", a menu a screen reader finds.
/// </summary>
public class ToolRowTests
{
    private static string[] Names(ItemsControl menu) =>
        menu.Items.OfType<FrameworkElement>().Where(i => i is not Separator).Select(i => i.Name).ToArray();

    [Fact]
    public void No_command_of_the_plugin_sits_in_a_tool_bar_overflow()
    {
        var toolBars = new List<string>();
        OnUiThread(() =>
        {
            var plugin = new OpcUaPlugin();
            toolBars.AddRange(Logical(plugin).OfType<ToolBar>().Select(t => t.Name));
            return Task.CompletedTask;
        });

        Assert.Empty(toolBars);
    }

    [Fact]
    public void More_holds_the_commands_used_now_and_then()
    {
        string[] server = [], namespaces = [];
        OnUiThread(() =>
        {
            var plugin = new OpcUaPlugin();
            server = Names(plugin.ServerMoreMenu);
            namespaces = Names(plugin.NamespacesMoreMenu);
            return Task.CompletedTask;
        });

        Assert.Equal(["PreviewButton", "CheckServerButton", "BindModelButton", "SnapshotButton", "ReceiveEventsItem", "SaveWatchButton", "WriteButton", "CallButton"], server);
        Assert.Equal(["CloudButton", "OpcfButton", "SaveDescriptorItem", "CheckDescriptorItem", "LinkButton"], namespaces);
    }

    [Theory]
    [InlineData(420)]
    [InlineData(600)]
    [InlineData(885)]
    public void The_commands_of_the_rows_are_in_view_at_any_width(double width)
    {
        var hidden = new List<string>();
        OnUiThread(async () =>
        {
            var plugin = new OpcUaPlugin();
            var window = new Window { Content = plugin, Width = width, Height = 800, ShowInTaskbar = false, Left = -4000 };
            window.Show();
            try
            {
                plugin.ServerTab.IsSelected = true;
                await Task.Delay(300);
                foreach (var b in new FrameworkElement[] { plugin.ServerTypesButton, plugin.MirrorButton, plugin.BindButton, plugin.LiveButton, plugin.WatchButton, plugin.UnwatchButton, plugin.ServerMoreButton })
                    if (!b.IsVisible || b.ActualWidth == 0 || b.TranslatePoint(new Point(b.ActualWidth, 0), plugin).X > plugin.ActualWidth + 0.5) hidden.Add(b.Name);
                plugin.NamespacesTab.IsSelected = true;
                await Task.Delay(300);
                foreach (var b in new FrameworkElement[] { plugin.ImportButton, plugin.ExportButton, plugin.InstanceButton, plugin.CheckButton, plugin.SettingsMenuButton, plugin.NamespacesMoreButton })
                    if (!b.IsVisible || b.ActualWidth == 0 || b.TranslatePoint(new Point(b.ActualWidth, 0), plugin).X > plugin.ActualWidth + 0.5) hidden.Add(b.Name);
            }
            finally { window.Close(); }
        });

        Assert.Empty(hidden);
    }

    [Fact]
    public void More_opens_its_menu()
    {
        bool server = false, namespaces = false, descriptorWithoutDocument = true;
        OnUiThread(async () =>
        {
            var plugin = new OpcUaPlugin();
            var window = new Window { Content = plugin, Width = 600, Height = 800, ShowInTaskbar = false, Left = -4000 };
            window.Show();
            try
            {
                plugin.ServerTab.IsSelected = true;
                await Task.Delay(300);
                Click(plugin.ServerMoreButton);
                await Task.Delay(500);
                server = plugin.ServerMoreMenu.IsOpen;
                plugin.ServerMoreMenu.IsOpen = false;

                plugin.NamespacesTab.IsSelected = true;
                await Task.Delay(300);
                Click(plugin.NamespacesMoreButton);
                await Task.Delay(500);
                namespaces = plugin.NamespacesMoreMenu.IsOpen;
                descriptorWithoutDocument = plugin.SaveDescriptorItem.IsEnabled;
                plugin.NamespacesMoreMenu.IsOpen = false;
            }
            finally { window.Close(); }
        });

        Assert.True(server);
        Assert.True(namespaces);
        Assert.False(descriptorWithoutDocument);
    }

    /// <summary>Lesson "bind", step 7 points at Read values into document, now in More.</summary>
    [Fact]
    public void Show_me_opens_More_for_a_command_in_it()
    {
        bool open = false;
        string? framed = null;
        OnUiThread(async () =>
        {
            var plugin = new OpcUaPlugin();
            var window = new Window { Content = plugin, Width = 600, Height = 800, ShowInTaskbar = false, Left = -4000 };
            window.Show();
            try
            {
                plugin.ShowMe("SnapshotButton", "ServerTab", openMenu: true);
                await Task.Delay(800);
                open = plugin.ServerMoreMenu.IsOpen;
                framed = plugin.Highlighted?.Name;
                plugin.ServerMoreMenu.IsOpen = false;
            }
            finally { window.Close(); }
        });

        Assert.True(open);
        Assert.Equal("ServerMoreButton", framed);
    }
}
