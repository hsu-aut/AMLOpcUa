// Dialog after an Apply from the modeler whose model holds instances: take
// them into a hierarchy of their own, below the element selected in the
// editor, or not at all. Asked every time, since each Apply may mean another
// place.

using System.Windows;
using System.Windows.Controls;

namespace Aml.Editor.Plugin.OpcUa;

public enum InstancesChoice { OwnHierarchy, BelowSelection, None }

public sealed class InstancesWindow : Window
{
    private readonly RadioButton _own = new() { GroupName = "instances", IsChecked = true };
    private readonly RadioButton _below = new() { GroupName = "instances" };
    private readonly RadioButton _none = new() { GroupName = "instances" };

    public InstancesChoice Choice =>
        _own.IsChecked == true ? InstancesChoice.OwnHierarchy
        : _below.IsChecked == true ? InstancesChoice.BelowSelection
        : InstancesChoice.None;

    /// <param name="instances">The names of the model's topmost instances.</param>
    /// <param name="hierarchy">The name of the hierarchy of their own.</param>
    /// <param name="selection">The element selected in the editor, when instances can go below it.</param>
    public InstancesWindow(IReadOnlyList<string> instances, string hierarchy, string? selection)
    {
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;

        _own.Content = Option($"Into '{hierarchy}'",
            "An InstanceHierarchy of their own. Every Apply replaces it, the rest of the document stays as it is.");
        _below.Content = Option(selection != null ? $"Below '{selection}'" : "Below the selected element",
            selection != null
                ? "Added to what the selected element holds, with IDs of their own, on every Apply."
                : "Select an InstanceHierarchy or an element in the editor first.");
        _below.IsEnabled = selection != null;
        _none.Content = Option("Not at all", "Only the types are taken; the instances stay in the NodeSet.");

        var body = new StackPanel();
        body.Children.Add(_own);
        _below.Margin = new Thickness(0, 10, 0, 0);
        body.Children.Add(_below);
        _none.Margin = new Thickness(0, 10, 0, 0);
        body.Children.Add(_none);

        var shown = instances.Take(5).ToList();
        var names = string.Join(", ", shown) + (instances.Count > shown.Count ? $" and {instances.Count - shown.Count} more" : "");
        var ok = DialogKit.Action("Take", primary: true);
        ok.Click += (_, __) => DialogResult = true;
        DialogKit.Frame(this, "", DialogKit.Create, "Instances of the model",
            $"The model holds {instances.Count} instance(s): {names}. Where should they go?", body, null, ok,
            DialogKit.Action("Types only", cancel: true));
    }

    private static UIElement Option(string title, string text)
    {
        var panel = new StackPanel { Margin = new Thickness(4, 0, 0, 0) };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = text, Foreground = DialogKit.Muted, TextWrapping = TextWrapping.Wrap, MaxWidth = 480 });
        return panel;
    }
}
