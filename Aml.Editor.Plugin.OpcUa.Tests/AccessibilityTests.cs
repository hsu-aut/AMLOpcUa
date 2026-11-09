using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Aml.Editor.Plugin.OpcUa;

namespace PluginTests;

/// <summary>
/// A button whose content is a glyph and a text has no name for UI
/// Automation: a screen reader says "button", and a script driving the editor
/// cannot find it. Every button of the plugin has a name.
/// </summary>
public class AccessibilityTests
{
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }

    [Fact]
    public void Every_button_of_the_plugin_has_a_name()
    {
        Exception? failure = null;
        var unnamed = new List<string>();
        var thread = new Thread(() =>
        {
            try
            {
                var plugin = new OpcUaPlugin();
                var window = new Window { Content = plugin, Width = 1400, Height = 900, ShowInTaskbar = false, Left = -4000 };
                window.Show();
                foreach (var button in Descendants<ButtonBase>(plugin))
                {
                    var name = UIElementAutomationPeer.CreatePeerForElement(button)?.GetName();
                    if (string.IsNullOrWhiteSpace(name))
                        unnamed.Add(button.Name is { Length: > 0 } id ? id : button.ToString());
                }
                window.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());

        Assert.Empty(unnamed);
    }

    /// <summary>Runs <paramref name="body"/> on a thread of its own for WPF and rethrows what it threw.</summary>
    private static void OnUiThread(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void An_entry_of_a_dialog_list_is_named_by_what_it_shows()
    {
        // Seen in the editor: the companion specifications were each called
        // "System.Windows.Controls.ListBoxItem", two of them by the same title.
        var names = new List<string>();
        OnUiThread(() =>
        {
            var list = new ListBox();
            list.Items.Add(DialogKit.Entry("Machinery", "http://opcfoundation.org/UA/Machinery/   1.04.2", new object()));
            list.Items.Add(DialogKit.Entry("Machinery", "http://opcfoundation.org/UA/Machinery/Example/   1.0.0", new object()));
            var window = new Window { Content = list, Width = 600, Height = 300, ShowInTaskbar = false, Left = -4000 };
            window.Show();
            foreach (var item in new ListBoxAutomationPeer(list).GetChildren()) names.Add(item.GetName());
            window.Close();
        });

        Assert.Equal(new[]
        {
            "Machinery  http://opcfoundation.org/UA/Machinery/   1.04.2",
            "Machinery  http://opcfoundation.org/UA/Machinery/Example/   1.0.0",
        }, names);
    }

    [Fact]
    public void The_detail_of_an_entry_takes_the_colour_of_the_entry_when_it_is_selected()
    {
        // A fixed grey stood on the blue of the selection and was hard to read.
        bool? follows = null;
        OnUiThread(() =>
        {
            var list = new ListBox();
            var entry = DialogKit.Entry("Machinery", "http://opcfoundation.org/UA/Machinery/", new object());
            list.Items.Add(entry);
            var window = new Window { Content = list, Width = 600, Height = 300, ShowInTaskbar = false, Left = -4000 };
            window.Show();
            entry.Foreground = System.Windows.Media.Brushes.White;
            var detail = Descendants<TextBlock>(entry).Last();
            follows = ReferenceEquals(detail.Foreground, entry.Foreground);
            window.Close();
        });

        Assert.True(follows);
    }

    [Fact]
    public void A_node_of_the_address_space_and_its_check_box_are_named_by_the_node()
    {
        string? item = null, check = null;
        var german = Aml.Editor.Plugin.OpcUa.Localization.L.German;
        Aml.Editor.Plugin.OpcUa.Localization.L.German = false;
        try
        {
            OnUiThread(() =>
            {
                var plugin = new OpcUaPlugin();
                var node = new OpcUaAml.Server.UaBrowseItem(
                    new OpcUaAml.Addressing.UaNodeAddress("http://hsu-hh.de/UA/MPS500/", OpcUaAml.Addressing.UaIdType.Numeric, "5001"),
                    "1:MPS500", "MPS500", "Object", null, "Organizes");
                var tree = plugin.NodeItem(node, null);
                item = UIElementAutomationPeer.CreatePeerForElement(tree)?.GetName();
                var box = Descendants<CheckBox>(tree).Single();
                check = UIElementAutomationPeer.CreatePeerForElement(box)?.GetName();
            });
        }
        finally { Aml.Editor.Plugin.OpcUa.Localization.L.German = german; }

        Assert.Equal("MPS500", item);
        Assert.Equal("Take MPS500", check);
    }

    [Fact]
    public void The_band_of_the_document_server_keeps_every_command_in_view_in_a_narrow_pane()
    {
        // Seen in the editor with the plugin beside the trees: "Remove server
        // addresses…" stood beyond the right edge, only its first letter showing.
        var outside = new List<string>();
        var bandWidth = 0.0;
        OnUiThread(() =>
        {
            var plugin = new OpcUaPlugin();
            var window = new Window { Content = plugin, Width = 700, Height = 800, ShowInTaskbar = false, Left = -4000 };
            window.Show();
            plugin.ServerTab.IsSelected = true;
            window.Dispatcher.Invoke(window.UpdateLayout, System.Windows.Threading.DispatcherPriority.Loaded);
            // The panel of the commands grows past the band when they do not fit, and is
            // cut off there; the band around it is what shows.
            var commands = (Panel)plugin.StripAddressesButton.Parent;
            var band = (FrameworkElement)commands.Parent;
            bandWidth = band.IsVisible ? band.ActualWidth : 0;
            foreach (var child in commands.Children.OfType<FrameworkElement>())
            {
                var right = child.TranslatePoint(new Point(child.ActualWidth, 0), band).X;
                if (child.IsVisible && right > band.ActualWidth + 0.5)
                    outside.Add(child.Name is { Length: > 0 } id ? id : child.GetType().Name);
            }
            window.Close();
        });

        Assert.True(bandWidth > 0, "the band is laid out");
        Assert.Empty(outside);
    }
}
