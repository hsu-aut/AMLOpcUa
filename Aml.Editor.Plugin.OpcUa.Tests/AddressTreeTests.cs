using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using Aml.Editor.Plugin.OpcUa;
using Aml.Editor.Plugin.OpcUa.Localization;
using OpcUaAml.Addressing;
using OpcUaAml.Server;

namespace PluginTests;

/// <summary>
/// The check box of a node in the server's address space says what is taken.
/// Seen in the editor: ticked through UI Automation (a screen reader's or voice
/// control's toggle), the box showed a tick, and the selection stayed empty.
/// </summary>
public class AddressTreeTests
{
    private static readonly UaBrowseItem Station = new(
        new UaNodeAddress("http://hsu-hh.de/UA/MPS500/", UaIdType.Numeric, "1042"),
        "1:ST10_Distributing", "ST10_Distributing", "Object", null, "Organizes");

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }

    /// <summary>Runs <paramref name="body"/> with English texts on a thread of its own for WPF.</summary>
    private static void OnUiThread(Action body)
    {
        Exception? failure = null;
        var german = L.German;
        L.German = false;
        try
        {
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
        }
        finally { L.German = german; }
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void Ticking_a_node_through_UI_Automation_takes_it_and_unticking_drops_it()
    {
        string? ticked = null, unticked = null;
        OnUiThread(() =>
        {
            var plugin = new OpcUaPlugin();
            var item = plugin.NodeItem(Station, null);
            var toggle = (IToggleProvider)UIElementAutomationPeer.CreatePeerForElement(Descendants<CheckBox>(item).Single());

            toggle.Toggle();
            ticked = plugin.SelectionSummary.Text;
            toggle.Toggle();
            unticked = plugin.SelectionSummary.Text;
        });

        Assert.Equal("1 part(s) checked.", ticked);
        Assert.StartsWith("Nothing checked", unticked);
    }

    [Fact]
    public void A_node_taken_alone_from_its_menu_stays_taken_alone()
    {
        // The box shows the tick for any part taken; showing it must not turn
        // "the node only" into "with everything below".
        string? scope = null;
        OnUiThread(() =>
        {
            var plugin = new OpcUaPlugin();
            var item = plugin.NodeItem(Station, null);
            var only = item.ContextMenu!.Items.OfType<MenuItem>().First(m => (string)m.Header == "Take the node only");
            only.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            scope = Descendants<TextBlock>(item).Last().Text;
        });

        Assert.Equal("   node only", scope);
    }
}
