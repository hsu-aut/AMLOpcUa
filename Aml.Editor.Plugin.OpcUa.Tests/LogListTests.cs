using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Aml.Editor.Plugin.OpcUa;
using Aml.Editor.Plugin.OpcUa.Localization;

namespace PluginTests;

/// <summary>
/// A line of the log, selected. Seen in the editor: the time in a fixed grey
/// and the level in its fixed blue stood on the blue of the selection and could
/// hardly be read.
/// </summary>
public class LogListTests
{
    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in Visuals<T>(child)) yield return d;
        }
    }

    [Theory]
    [InlineData("INFO")]
    [InlineData("WARN")]
    [InlineData("DEBUG")]
    public void A_selected_line_shows_time_and_level_in_the_colour_of_the_selection(string level)
    {
        var (selection, time, badge) = Show(level, selected: true);

        Assert.Equal(selection, time);
        Assert.Equal(selection, badge);
    }

    [Fact]
    public void A_warning_not_selected_keeps_its_orange()
    {
        var (_, _, badge) = Show("WARN", selected: false);

        Assert.Equal("#FFD08020", badge);
    }

    /// <summary>The colours of the line, of its time and of its level.</summary>
    private static (string? Line, string? Time, string? Level) Show(string level, bool selected)
    {
        string? time = null, badge = null, selection = null;
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
                    var window = new Window { Content = plugin, Width = 900, Height = 700, ShowInTaskbar = false, Left = -4000 };
                    window.Show();
                    var line = new OpcUaPlugin.LogLine("12:00:01", level, "Connected.", "12:00:01 " + level + " Connected.");
                    plugin.LogList.ItemsSource = new[] { line };
                    plugin.LogList.UpdateLayout();
                    var item = (ListBoxItem)plugin.LogList.ItemContainerGenerator.ContainerFromIndex(0);
                    if (selected)
                    {
                        item.IsSelected = true;
                        item.Foreground = Brushes.White;
                    }
                    window.UpdateLayout();
                    var texts = Visuals<TextBlock>(item).ToList();
                    TextBlock Named(string text) => texts.First(t => t.Text == text);
                    selection = Colour(item.Foreground);
                    time = Colour(Named("12:00:01").Foreground);
                    badge = Colour(Named(level).Foreground);
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
        return (selection, time, badge);
    }

    private static string Colour(Brush brush) => brush is SolidColorBrush s ? s.Color.ToString() : brush.ToString();
}
