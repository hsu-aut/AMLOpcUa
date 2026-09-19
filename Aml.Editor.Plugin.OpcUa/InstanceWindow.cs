// Dialog for a new instance of a UA type: pick the type on the left; name the
// instance, choose the hierarchy, the Optional children and the children of
// placeholders on the right.

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
    private readonly StackPanel _placeholders = new();
    private readonly TextBlock _placeholderLabel = DialogKit.Label("Children for placeholders");
    private readonly TextBlock _info = DialogKit.Message();
    private readonly List<(PlaceholderInfo Placeholder, TextBox Names, ComboBox Type)> _placeholderRows = new();

    public SystemUnitFamilyType? SelectedType { get; private set; }
    public string InstanceName => _name.Text.Trim();
    public string HierarchyName => string.IsNullOrWhiteSpace(_hierarchy.Text) ? "OpcUaInstances" : _hierarchy.Text.Trim();
    public HashSet<string> ChosenOptional { get; } = new(StringComparer.Ordinal);

    /// <summary>The concrete children per placeholder path.</summary>
    public Dictionary<string, List<PlaceholderFill>> Fills { get; } = new(StringComparer.Ordinal);

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
            Fills.Clear();
            foreach (var (placeholder, names, typeBox) in _placeholderRows)
            {
                var type = (typeBox.SelectedItem as ComboBoxItem)?.Tag as SystemUnitFamilyType;
                var list = names.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(n => new PlaceholderFill(n, type == placeholder.Type ? null : type)).ToList();
                if (list.Select(f => f.Name).Distinct().Count() != list.Count) { _info.Text = $"Two children of {placeholder.Path} have the same name."; return; }
                if (list.Count > 0) Fills[placeholder.Path] = list;
            }
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
        DockPanel.SetDock(form, Dock.Top);
        right.Children.Add(form);
        var optional = new StackPanel();
        optional.Children.Add(_placeholderLabel);
        optional.Children.Add(_placeholders);
        optional.Children.Add(DialogKit.Label("Optional children to create"));
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
            "Every Mandatory child of the type is created, the Optional children you tick, and for a placeholder the children you name.",
            body, _info, ok, DialogKit.Action("Cancel", cancel: true));

        Filter();
        Loaded += (_, __) => _search.Focus();
    }

    /// <summary>
    /// A placeholder: the names of its children, comma separated, and their
    /// type, the placeholder's own or a concrete subtype of it.
    /// </summary>
    private void AddPlaceholderRow(PlaceholderInfo placeholder)
    {
        var names = new TextBox { Padding = new Thickness(3) };
        var typeBox = new ComboBox { Margin = new Thickness(0, 3, 0, 0) };
        var candidates = placeholder.Type == null ? new List<SystemUnitFamilyType>()
            : _types.Select(t => t.Type).Where(t => t == placeholder.Type || UaTypes.DerivesFrom(t, placeholder.Type))
                .Where(t => t == placeholder.Type || !UaTypes.IsAbstract(t))
                .OrderBy(t => t == placeholder.Type ? 0 : 1).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var type in candidates)
        {
            var label = type == placeholder.Type ? $"{type.Name} (as declared{(UaTypes.IsAbstract(type) ? ", abstract" : "")})" : type.Name;
            typeBox.Items.Add(new ComboBoxItem { Content = label, Tag = type });
        }
        // An abstract declared type cannot be instantiated; offer its first concrete subtype instead.
        typeBox.SelectedIndex = candidates.Count == 0 ? -1
            : placeholder.Type != null && UaTypes.IsAbstract(placeholder.Type) && candidates.Count > 1 ? 1 : 0;
        typeBox.IsEnabled = candidates.Count > 1;

        var head = new TextBlock { Text = placeholder.Path + (placeholder.Mandatory ? "  (at least one)" : ""), Margin = new Thickness(0, 6, 0, 2) };
        _placeholders.Children.Add(head);
        _placeholders.Children.Add(DialogKit.WithPlaceholder(names, "Names, comma separated", search: false));
        if (candidates.Count > 0) _placeholders.Children.Add(typeBox);
        _placeholderRows.Add((placeholder, names, typeBox));
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
        _placeholders.Children.Clear();
        _placeholderRows.Clear();
        _placeholderLabel.Visibility = Visibility.Collapsed;
        SelectedType = DialogKit.Selected<SystemUnitFamilyType>(_typeList);
        if (SelectedType == null) return;

        if (_name.Text.Length == 0 || _types.Any(t => t.Type.Name.Replace("Type", "") == _name.Text))
            _name.Text = SelectedType.Name.EndsWith("Type") ? SelectedType.Name[..^4] : SelectedType.Name;

        // A dry run with every Optional child asks about exactly the Optional
        // ones, inherited included; the Mandatory children below them are not choices.
        try
        {
            var asked = new List<string>();
            var all = TypeInstantiator.Instantiate(SelectedType, "probe", new InstantiationOptions { AllowAbstract = true, IncludeOptional = p => { asked.Add(p); return true; } });
            var mandatory = TypeInstantiator.Instantiate(SelectedType, "probe", new InstantiationOptions { AllowAbstract = true }).Included.ToHashSet();
            foreach (var path in asked)
            {
                // A choice below another Optional child only counts when that one is ticked.
                var depth = path.Count(c => c == '/');
                var box = new CheckBox { Content = depth == 0 ? path : path[(path.LastIndexOf('/') + 1)..], Tag = path, ToolTip = path, Margin = new Thickness(18 * depth, 2, 0, 2) };
                _optional.Children.Add(box);
            }
            _optionalHint.Text = _optional.Children.Count == 0 ? "The type has no Optional children." : "";
            _optionalHint.Visibility = _optional.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (var placeholder in all.Placeholders) AddPlaceholderRow(placeholder);
            _placeholderLabel.Visibility = _placeholderRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            _info.Text = $"{mandatory.Count} Mandatory child(ren) are always created."
                + (UaTypes.IsAbstract(SelectedType) ? " The type is abstract; OPC UA does not instantiate it." : "");
        }
        catch (Exception ex)
        {
            _info.Text = "Cannot analyse the type: " + ex.Message;
        }
    }
}
