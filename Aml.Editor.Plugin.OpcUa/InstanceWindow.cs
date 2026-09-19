// Dialog for a new instance of a UA type: pick the type on the left; name the
// instance, choose the hierarchy and the Optional children on the right.

using System.Windows;
using System.Windows.Controls;
using Aml.Engine.CAEX;
using OpcUaAml.Types;

namespace Aml.Editor.Plugin.OpcUa;

public sealed class InstanceWindow : Window
{
    private readonly List<(string Path, SystemUnitFamilyType Type)> _types;
    private readonly TextBox _search = new();
    private readonly ListBox _typeList = new();
    private readonly TextBox _name = new() { Padding = new Thickness(3) };
    private readonly ComboBox _hierarchy = new() { IsEditable = true };
    private readonly CheckBox _showAbstract = new() { Content = "Show abstract types", Margin = new Thickness(0, 6, 0, 0) };
    private readonly StackPanel _optional = new();
    private readonly TextBlock _optionalHint = new() { Foreground = DialogKit.Muted, TextWrapping = TextWrapping.Wrap, Text = "Choose a type first." };
    private readonly TextBlock _info = DialogKit.Message();

    public SystemUnitFamilyType? SelectedType { get; private set; }
    public string InstanceName => _name.Text.Trim();
    public string HierarchyName => string.IsNullOrWhiteSpace(_hierarchy.Text) ? "OpcUaInstances" : _hierarchy.Text.Trim();
    public HashSet<string> ChosenOptional { get; } = new(StringComparer.Ordinal);

    public InstanceWindow(CAEXDocument document)
    {
        Width = 820;
        Height = 600;
        ResizeMode = ResizeMode.CanResizeWithGrip;

        _types = UaTypes.AllTypes(document)
            .Where(t => !t.Path.StartsWith("SUC_OpcAmlMetaModel", StringComparison.Ordinal))
            .OrderBy(t => t.Type.Name, StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var ih in document.CAEXFile.InstanceHierarchy) _hierarchy.Items.Add(ih.Name);
        _hierarchy.Text = document.CAEXFile.InstanceHierarchy.FirstOrDefault()?.Name ?? "OpcUaInstances";

        _search.TextChanged += (_, __) => Filter();
        _showAbstract.Checked += (_, __) => Filter();
        _showAbstract.Unchecked += (_, __) => Filter();
        _typeList.SelectionChanged += (_, __) => TypeChanged();
        _typeList.MouseDoubleClick += (_, __) => _name.Focus();

        var ok = DialogKit.Action("Create", primary: true);
        ok.Click += (_, __) =>
        {
            if (SelectedType == null) { _info.Text = "Choose a type."; return; }
            if (InstanceName.Length == 0) { _info.Text = "Give the instance a name."; return; }
            ChosenOptional.Clear();
            foreach (var cb in _optional.Children.OfType<CheckBox>())
                if (cb.IsChecked == true) ChosenOptional.Add((string)cb.Tag);
            DialogResult = true;
        };

        var left = new DockPanel();
        var typeLabel = DialogKit.Label("Type", 0);
        var search = DialogKit.WithPlaceholder(_search, "Search types by name");
        DockPanel.SetDock(typeLabel, Dock.Top);
        DockPanel.SetDock(search, Dock.Top);
        DockPanel.SetDock(_showAbstract, Dock.Bottom);
        ((FrameworkElement)search).Margin = new Thickness(0, 0, 0, 4);
        left.Children.Add(typeLabel);
        left.Children.Add(search);
        left.Children.Add(_showAbstract);
        left.Children.Add(_typeList);

        var right = new DockPanel();
        var form = new StackPanel();
        form.Children.Add(DialogKit.Label("Name", 0));
        form.Children.Add(_name);
        form.Children.Add(DialogKit.Label("Instance hierarchy (created if new)"));
        form.Children.Add(_hierarchy);
        form.Children.Add(DialogKit.Label("Optional children to create"));
        DockPanel.SetDock(form, Dock.Top);
        right.Children.Add(form);
        var optional = new StackPanel();
        optional.Children.Add(_optionalHint);
        optional.Children.Add(_optional);
        right.Children.Add(new ScrollViewer { Content = optional, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(right, 2);
        body.Children.Add(left);
        body.Children.Add(right);

        DialogKit.Frame(this, "", DialogKit.Create, "New OPC UA instance",
            "Every Mandatory child of the type is created, and the Optional children you tick. Placeholders are left to fill by hand.",
            body, _info, ok, DialogKit.Action("Cancel", cancel: true));

        Filter();
        Loaded += (_, __) => _search.Focus();
    }

    private void Filter()
    {
        var text = _search.Text.Trim();
        var showAbstract = _showAbstract.IsChecked == true;
        _typeList.ItemsSource = _types
            .Where(t => showAbstract || !UaTypes.IsAbstract(t.Type))
            .Where(t => text.Length == 0 || t.Type.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Select(t => DialogKit.Entry(t.Type.Name, NamespaceOf(t.Path), t.Type))
            .ToList();
    }

    private static string NamespaceOf(string path)
    {
        var end = path.IndexOf(']');
        return path.StartsWith("[SUC_") && end > 5 ? path[5..end] : path;
    }

    private void TypeChanged()
    {
        _optional.Children.Clear();
        SelectedType = DialogKit.Selected<SystemUnitFamilyType>(_typeList);
        if (SelectedType == null) return;

        if (_name.Text.Length == 0 || _types.Any(t => t.Type.Name.Replace("Type", "") == _name.Text))
            _name.Text = SelectedType.Name.EndsWith("Type") ? SelectedType.Name[..^4] : SelectedType.Name;

        // Two dry runs tell the Optional children apart from the Mandatory ones,
        // including those inherited from supertypes.
        try
        {
            var options = new InstantiationOptions { AllowAbstract = true };
            var all = TypeInstantiator.Instantiate(SelectedType, "probe", new InstantiationOptions { AllowAbstract = true, IncludeOptional = _ => true });
            var mandatory = TypeInstantiator.Instantiate(SelectedType, "probe", options).Included.ToHashSet();
            foreach (var path in all.Included.Where(p => !mandatory.Contains(p)))
                _optional.Children.Add(new CheckBox { Content = path, Tag = path, Margin = new Thickness(0, 2, 0, 2) });
            _optionalHint.Text = _optional.Children.Count == 0 ? "The type has no Optional children." : "";
            _optionalHint.Visibility = _optional.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            _info.Text = $"{mandatory.Count} Mandatory child(ren) are always created."
                + (all.OmittedPlaceholders.Count > 0 ? $" Placeholders to fill by hand: {string.Join(", ", all.OmittedPlaceholders)}." : "")
                + (UaTypes.IsAbstract(SelectedType) ? " The type is abstract; OPC UA does not instantiate it." : "");
        }
        catch (Exception ex)
        {
            _info.Text = "Cannot analyse the type: " + ex.Message;
        }
    }
}
