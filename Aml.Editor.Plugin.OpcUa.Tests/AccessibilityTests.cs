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
}
