// Dialog for a new instance of a UA type: pick the type, name the instance,
// choose the hierarchy and the Optional children. Built in code like the
// folder dialog; it is a form without styling needs of its own.

using System.Windows;
using System.Windows.Controls;
using Aml.Engine.CAEX;
using OpcUaAml.Types;

namespace Aml.Editor.Plugin.OpcUa;

public sealed class InstanceWindow : Window
{
    private readonly List<(string Path, SystemUnitFamilyType Type)> _types;
    private readonly TextBox _search = new() { Margin = new Thickness(0, 0, 0, 4) };
    private readonly ListBox _typeList = new() { Height = 180 };
    private readonly TextBox _name = new();
    private readonly ComboBox _hierarchy = new() { IsEditable = true };
    private readonly CheckBox _showAbstract = new() { Content = "Show abstract types", Margin = new Thickness(0, 4, 0, 0) };
    private readonly StackPanel _optional = new();
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Gray };

    public SystemUnitFamilyType? SelectedType { get; private set; }
    public string InstanceName => _name.Text.Trim();
    public string HierarchyName => string.IsNullOrWhiteSpace(_hierarchy.Text) ? "OpcUaInstances" : _hierarchy.Text.Trim();
    public HashSet<string> ChosenOptional { get; } = new(StringComparer.Ordinal);

    public InstanceWindow(CAEXDocument document)
    {
        Title = "New OPC UA instance";
        Width = 640;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
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

        var ok = new Button { Content = "Create", Width = 90, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        ok.Click += (_, __) =>
        {
            if (SelectedType == null) { _info.Text = "Choose a type."; return; }
            if (InstanceName.Length == 0) { _info.Text = "Give the instance a name."; return; }
            ChosenOptional.Clear();
            foreach (var cb in _optional.Children.OfType<CheckBox>())
                if (cb.IsChecked == true) ChosenOptional.Add((string)cb.Tag);
            DialogResult = true;
        };
        var cancel = new Button { Content = "Cancel", Width = 90, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var form = new StackPanel();
        form.Children.Add(Label("Type (search by name)"));
        form.Children.Add(_search);
        form.Children.Add(_typeList);
        form.Children.Add(_showAbstract);
        form.Children.Add(Label("Name"));
        form.Children.Add(_name);
        form.Children.Add(Label("Instance hierarchy (created if new)"));
        form.Children.Add(_hierarchy);
        form.Children.Add(Label("Optional children to create"));
        form.Children.Add(new ScrollViewer { Content = _optional, Height = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        form.Children.Add(_info);

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;

        Filter();
        Loaded += (_, __) => _search.Focus();
    }

    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2) };

    private void Filter()
    {
        var text = _search.Text.Trim();
        var showAbstract = _showAbstract.IsChecked == true;
        _typeList.ItemsSource = _types
            .Where(t => showAbstract || !UaTypes.IsAbstract(t.Type))
            .Where(t => text.Length == 0 || t.Type.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Select(t => new TypeItem(t.Path, t.Type))
            .ToList();
    }

    private void TypeChanged()
    {
        _optional.Children.Clear();
        SelectedType = (_typeList.SelectedItem as TypeItem)?.Type;
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
                _optional.Children.Add(new CheckBox { Content = path, Tag = path, Margin = new Thickness(0, 1, 0, 1) });

            _info.Text = $"{mandatory.Count} Mandatory child(ren) are always created."
                + (all.OmittedPlaceholders.Count > 0 ? $" Placeholders to fill by hand: {string.Join(", ", all.OmittedPlaceholders)}." : "")
                + (UaTypes.IsAbstract(SelectedType) ? " The type is abstract; OPC UA does not instantiate it." : "");
        }
        catch (Exception ex)
        {
            _info.Text = "Cannot analyse the type: " + ex.Message;
        }
    }

    private sealed record TypeItem(string Path, SystemUnitFamilyType Type)
    {
        public override string ToString() => $"{Type.Name}    ({NamespaceOf(Path)})";

        private static string NamespaceOf(string path)
        {
            var end = path.IndexOf(']');
            return path.StartsWith("[SUC_") && end > 5 ? path[5..end] : path;
        }
    }
}
