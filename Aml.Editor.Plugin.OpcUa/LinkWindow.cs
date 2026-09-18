// Dialog to link a VDI 3682 element to an OPC UA element: a TechnicalResource
// to the object representing it, a ProcessOperator to the method executing it.

using System.Windows;
using System.Windows.Controls;
using Aml.Engine.CAEX;
using OpcUaAml.Links;

namespace Aml.Editor.Plugin.OpcUa;

public sealed class LinkWindow : Window
{
    private readonly CAEXDocument _doc;
    private readonly ComboBox _kind = new() { Width = 220 };
    private readonly ListBox _sources = new();
    private readonly ListBox _targets = new();
    private readonly ListBox _existing = new() { Height = 110 };
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 4, 0, 0) };

    /// <summary>Set when a link was created, for the editor to select.</summary>
    public InternalElementType? Linked { get; private set; }

    public LinkWindow(CAEXDocument doc)
    {
        _doc = doc;
        Title = "Link VDI 3682 and OPC UA";
        Width = 820;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _kind.Items.Add("TechnicalResource to UA object");
        _kind.Items.Add("ProcessOperator to UA method");
        _kind.SelectedIndex = 0;
        _kind.SelectionChanged += (_, __) => Fill();

        var link = new Button { Content = "Link", Width = 90, Margin = new Thickness(0, 0, 6, 0) };
        link.Click += (_, __) => DoLink();
        var close = new Button { Content = "Close", Width = 90, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(link);
        buttons.Children.Add(close);

        var lists = new Grid();
        lists.ColumnDefinitions.Add(new ColumnDefinition());
        lists.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        lists.ColumnDefinitions.Add(new ColumnDefinition());
        lists.Children.Add(Labelled("VDI 3682 element", _sources, 0));
        lists.Children.Add(Labelled("OPC UA element", _targets, 2));

        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        top.Children.Add(new TextBlock { Text = "Relation ", VerticalAlignment = VerticalAlignment.Center });
        top.Children.Add(_kind);

        var bottom = new StackPanel();
        bottom.Children.Add(new TextBlock { Text = "Links in this document", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2) });
        bottom.Children.Add(_existing);
        bottom.Children.Add(_info);
        bottom.Children.Add(buttons);

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(lists);
        Content = root;
        Fill();
    }

    private FpdKind Kind => _kind.SelectedIndex == 1 ? FpdKind.ProcessOperator : FpdKind.TechnicalResource;

    private static DockPanel Labelled(string label, ListBox list, int column)
    {
        var panel = new DockPanel();
        var text = new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) };
        DockPanel.SetDock(text, Dock.Top);
        panel.Children.Add(text);
        panel.Children.Add(list);
        Grid.SetColumn(panel, column);
        return panel;
    }

    private void Fill()
    {
        _sources.ItemsSource = Vdi3682Links.FpdElements(_doc, Kind).Select(e => new Item(e)).ToList();
        _targets.ItemsSource = Vdi3682Links.UaTargets(_doc, Kind).Select(e => new Item(e)).ToList();
        _existing.ItemsSource = Vdi3682Links.Links(_doc)
            .Select(l => $"{l.Source.Name}   {(l.Kind == FpdKind.TechnicalResource ? "refOpcUaObject" : "refOpcUaMethod")}   {l.Target?.Name ?? "(missing " + l.TargetId + ")"}")
            .ToList();
        _info.Text = _sources.Items.Count == 0
            ? $"The document has no {Kind}. Process descriptions come from the FPB plugin (VDI 3682 library)."
            : "Links are IDREF attributes derived from refObj of the AutomationML object reference types.";
    }

    private void DoLink()
    {
        if (_sources.SelectedItem is not Item source || _targets.SelectedItem is not Item target)
        {
            _info.Text = "Choose one element on each side.";
            return;
        }
        try
        {
            Vdi3682Links.Link(source.Element, target.Element, Kind);
            Linked = source.Element;
            Fill();
            _info.Text = $"Linked '{source.Element.Name}' to '{target.Element.Name}'.";
        }
        catch (LinkException ex)
        {
            _info.Text = ex.Message;
        }
    }

    private sealed record Item(InternalElementType Element)
    {
        public override string ToString()
        {
            var type = Element.RefBaseSystemUnitPath;
            var last = type == null ? "" : type[(type.LastIndexOf('/') + 1)..].Trim('[', ']');
            return $"{Element.Name}    {last}";
        }
    }
}
