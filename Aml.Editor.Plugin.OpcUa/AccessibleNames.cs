// Names for buttons whose content is a glyph and a text. UI Automation takes a
// button's name from string content only; for a panel of glyph and text it has
// none, so a screen reader says "button" and a script cannot find it.

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;

namespace Aml.Editor.Plugin.OpcUa;

internal static class AccessibleNames
{
    /// <summary>Gives every button below <paramref name="root"/> that has no name the text it shows, else its tooltip.</summary>
    public static void Give(DependencyObject root)
    {
        foreach (var button in Descendants(root).OfType<ButtonBase>())
        {
            if (!string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)) || button.Content is string) continue;
            var name = ShownText(button) ?? button.ToolTip as string;
            if (!string.IsNullOrWhiteSpace(name)) AutomationProperties.SetName(button, name);
        }
    }

    /// <summary>The text a button shows, its glyphs (private use characters of the icon font) left out.</summary>
    private static string? ShownText(DependencyObject button)
    {
        var parts = Descendants(button)
            .Select(d => d switch
            {
                TextBlock t => t.Inlines.Count > 0 ? string.Concat(t.Inlines.OfType<Run>().Select(r => r.Text)) : t.Text,
                Run r => r.Text,
                _ => null,
            })
            .Select(text => new string((text ?? "").Where(c => c < '' || c > '').ToArray()).Trim())
            .Where(text => text.Length > 0)
            .Distinct()
            .ToList();
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }
}
