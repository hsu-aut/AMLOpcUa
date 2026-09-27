// Dialog to link a VDI 3682 element to an OPC UA element: a TechnicalResource
// to the object representing it, a ProcessOperator to the method executing it.

using System.Windows;
using System.Windows.Controls;
using Aml.Editor.Plugin.OpcUa.Localization;
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
    private readonly TextBlock _info = DialogKit.Message();

    /// <summary>Set when a link was created, for the editor to select.</summary>
    public InternalElementType? Linked { get; private set; }

    public LinkWindow(CAEXDocument doc)
    {
        _doc = doc;
        Width = 860;
        Height = 600;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        _kind.Items.Add(L.T("TechnicalResource to UA object"));
        _kind.Items.Add(L.T("ProcessOperator to UA method"));
        _kind.SelectedIndex = 0;
        _kind.SelectionChanged += (_, __) => Fill();

        var link = DialogKit.Action(L.T("Link"), primary: true);
        link.Click += (_, __) => DoLink();

        var lists = new Grid();
        lists.ColumnDefinitions.Add(new ColumnDefinition());
        lists.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        lists.ColumnDefinitions.Add(new ColumnDefinition());
        lists.Children.Add(Labelled(L.T("VDI 3682 element"), _sources, 0));
        lists.Children.Add(Labelled(L.T("OPC UA element"), _targets, 2));

        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        top.Children.Add(new TextBlock { Text = L.T("Relation"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        top.Children.Add(_kind);

        var bottom = new StackPanel();
        bottom.Children.Add(DialogKit.Label(L.T("Links in this document")));
        bottom.Children.Add(_existing);

        var body = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        body.Children.Add(top);
        body.Children.Add(bottom);
        body.Children.Add(lists);

        DialogKit.Frame(this, "\uE71B", DialogKit.Relate, L.T("Link VDI 3682 and OPC UA"),
            L.T("A TechnicalResource refers to the OPC UA object that represents it, a ProcessOperator to the method that executes it."),
            body, _info, link, DialogKit.Action(L.T("Close"), cancel: true));
        Fill();
    }

    private FpdKind Kind => _kind.SelectedIndex == 1 ? FpdKind.ProcessOperator : FpdKind.TechnicalResource;

    private static DockPanel Labelled(string label, ListBox list, int column)
    {
        var panel = new DockPanel();
        var text = DialogKit.Label(label, 0);
        DockPanel.SetDock(text, Dock.Top);
        panel.Children.Add(text);
        panel.Children.Add(list);
        Grid.SetColumn(panel, column);
        return panel;
    }

    private void Fill()
    {
        _sources.ItemsSource = Vdi3682Links.FpdElements(_doc, Kind).Select(Entry).ToList();
        _targets.ItemsSource = Vdi3682Links.UaTargets(_doc, Kind).Select(Entry).ToList();
        _existing.ItemsSource = Vdi3682Links.Links(_doc)
            .Select(l => $"{l.Source.Name}   {(l.Kind == FpdKind.TechnicalResource ? "refOpcUaObject" : "refOpcUaMethod")}   {l.Target?.Name ?? L.T("(missing {0})", l.TargetId)}")
            .ToList();
        _info.Text = _sources.Items.Count == 0
            ? L.T("The document has no {0}. Process descriptions come from the FPB plugin (VDI 3682 library).", Kind)
            : L.T("The link is an attribute of the process element that names the OPC UA element.");
    }

    private void DoLink()
    {
        if (DialogKit.Selected<InternalElementType>(_sources) is not { } source || DialogKit.Selected<InternalElementType>(_targets) is not { } target)
        {
            DialogKit.ShowError(_info, L.T("Choose one element on each side."));
            return;
        }
        try
        {
            Vdi3682Links.Link(source, target, Kind);
            Linked = source;
            Fill();
            _info.Text = L.T("Linked '{0}' to '{1}'.", source.Name, target.Name);
        }
        catch (LinkException ex)
        {
            DialogKit.ShowError(_info, ex.Message);
        }
    }

    private static ListBoxItem Entry(InternalElementType element)
    {
        var type = element.RefBaseSystemUnitPath;
        var last = type == null ? "" : type[(type.LastIndexOf('/') + 1)..].Trim('[', ']');
        return DialogKit.Entry(element.Name, last, element);
    }
}
