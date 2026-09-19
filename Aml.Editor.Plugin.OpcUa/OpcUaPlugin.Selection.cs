// What of the server goes into the document: nodes checked in the address
// tree (right click for how much below them), instances of a type, Views,
// filters over all of it, a count before and the question whether to update
// or copy when the part was mirrored before. The selection is kept in the
// document by the core (MirrorSelection) and can be loaded again.

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Aml.Editor.Plugin.Contracts;
using Aml.Editor.Plugin.OpcUa.Diagnostics;
using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using OpcUaAml.Addressing;
using OpcUaAml.Server;

namespace Aml.Editor.Plugin.OpcUa;

public partial class OpcUaPlugin
{
    private const string Pending = "…";
    private static readonly UaNodeAddress ObjectsFolder = new("http://opcfoundation.org/UA/", UaIdType.Numeric, "85");

    private readonly List<MirrorItem> _items = new();
    private readonly HashSet<UaNodeAddress> _excluded = new();
    private List<string> _namespaces = new();
    private readonly ObservableCollection<SelectionRow> _selectionRows = new();
    private readonly Dictionary<UaNodeAddress, string> _names = new();
    private readonly List<Action> _showChecks = new();
    private readonly HashSet<UaNodeAddress> _inDocument = new();

    private sealed record SelectionRow(string Text, MirrorItem? Item, UaNodeAddress? Excluded)
    {
        public override string ToString() => Text;
    }

    private void InitSelection()
    {
        SelectionList.ItemsSource = _selectionRows;
        SelectionList.SelectionChanged += (_, __) => UpdateServerState();
        _names[ObjectsFolder] = "Objects";
        RefreshSelection();
    }

    private static UaNodeAddress Key(UaNodeAddress a) => a with { ServerUri = null };

    private MirrorFilter CurrentFilter() => new()
    {
        SkipProperties = SkipPropertiesBox.IsChecked == true,
        ObjectsOnly = ObjectsOnlyBox.IsChecked == true,
        HideServer = ShowServerBox.IsChecked != true,
        Namespaces = _namespaces.ToList(),
    };

    private int CurrentDepth() => int.TryParse(MirrorDepthBox.Text, out var d) ? Math.Clamp(d, 0, 50) : 3;

    private int CurrentMaxNodes() => int.TryParse(MirrorMaxNodesBox.Text, out var n) && n > 0 ? n : MirrorOptions.DefaultMaxNodes;

    private void MirrorMaxNodesBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _settings.MirrorMaxNodes = CurrentMaxNodes();
        MirrorMaxNodesBox.Text = _settings.MirrorMaxNodes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _settings.Save();
    }

    /// <summary>The checked parts, or with nothing checked the selected node with what is below it.</summary>
    private MirrorSelection? CurrentSelection()
    {
        var items = _items.ToList();
        if (items.Count == 0)
        {
            if (SelectedNode is not { NodeClass: not "Method" } node) return null;
            items.Add(new MirrorItem(node.Address, MirrorScope.Subtree));
        }
        return new MirrorSelection { Items = items, Excluded = _excluded.ToHashSet(), Filter = CurrentFilter(), Depth = CurrentDepth() };
    }

    private string Label(UaNodeAddress a) => _names.TryGetValue(Key(a), out var n) ? n : a.ToString();

    private string Describe(MirrorItem i) => i.Scope switch
    {
        MirrorScope.Node => $"{Label(i.Node)}  (node)",
        MirrorScope.Children => $"{Label(i.Node)}  (with children)",
        MirrorScope.Subtree => $"{Label(i.Node)}  (with everything below)",
        _ => $"every {Label(i.Type!)} below {Label(i.Node)}",
    } + (i.View != null && Key(i.View) != Key(i.Node) ? $"  in view {Label(i.View)}" : "");

    private void RefreshSelection()
    {
        _selectionRows.Clear();
        foreach (var i in _items) _selectionRows.Add(new SelectionRow(Describe(i), i, null));
        foreach (var x in _excluded) _selectionRows.Add(new SelectionRow($"leave out {Label(x)}", null, x));
        SelectionSummary.Text = _items.Count == 0
            ? "Nothing checked: 'Take into document' takes the selected node with what is below it."
            : $"{_items.Count} part(s) checked.";
        foreach (var show in _showChecks) show();
        UpdateServerState();
    }

    private MirrorItem? ItemFor(UaNodeAddress node) =>
        _items.FirstOrDefault(i => i.Scope != MirrorScope.InstancesOf && Key(i.Node) == Key(node));

    private void SetScope(UaNodeAddress node, MirrorScope? scope, UaNodeAddress? view)
    {
        _items.RemoveAll(i => i.Scope != MirrorScope.InstancesOf && Key(i.Node) == Key(node));
        if (scope is { } s)
        {
            _excluded.Remove(Key(node));
            _items.Add(new MirrorItem(node, s, View: view));
        }
        RefreshSelection();
    }

    // ── address space tree ──────────────────────────────────────────────────

    /// <summary>The nodes the hierarchy named under 'into' already holds a mirror of, for the marks in the tree.</summary>
    private void RefreshInDocument()
    {
        _inDocument.Clear();
        if (_document != null && _client != null && _document.CAEXFile.InstanceHierarchy[HierarchyName()] is { } ih
            && AddressSpaceMirror.MirroredServer(ih, _client) is { } server)
        {
            foreach (var e in server.Descendants<InternalElementType>())
            {
                try { if (AnnexANodeId.Of(e) is { } a) _inDocument.Add(Key(a)); }
                catch (AddressingException) { /* not a node of the server */ }
            }
        }
        foreach (var show in _showChecks) show();
    }

    private void MirrorHierarchyBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshInDocument();

    /// <summary>The shape of a NodeClass as OPC 10000-3 draws it, small: rectangle, rounded, ellipse.</summary>
    private static FrameworkElement ClassShape(string nodeClass)
    {
        Color stroke, fill;
        switch (nodeClass)
        {
            case "Object": stroke = Color.FromRgb(0x20, 0x70, 0xC0); fill = Color.FromRgb(0xE4, 0xEE, 0xF9); break;
            case "Variable": stroke = Color.FromRgb(0x20, 0xA0, 0x40); fill = Color.FromRgb(0xE3, 0xF4, 0xE8); break;
            case "Method": stroke = Color.FromRgb(0xE0, 0x80, 0x20); fill = Color.FromRgb(0xFC, 0xEF, 0xE0); break;
            case "View": stroke = Color.FromRgb(0x80, 0x40, 0xA0); fill = Color.FromRgb(0xF0, 0xE7, 0xF6); break;
            default: return new FrameworkElement { Width = 14 };
        }
        FrameworkElement shape = nodeClass == "Method"
            ? new System.Windows.Shapes.Ellipse { Stroke = new SolidColorBrush(stroke), Fill = new SolidColorBrush(fill), StrokeThickness = 1.2 }
            : new Border
            {
                BorderBrush = new SolidColorBrush(stroke),
                Background = new SolidColorBrush(fill),
                BorderThickness = new Thickness(nodeClass == "View" ? 1.6 : 1.2),
                CornerRadius = new CornerRadius(nodeClass == "Variable" ? 4 : nodeClass == "View" ? 1 : 0),
            };
        shape.Width = 14;
        shape.Height = 10;
        shape.Margin = new Thickness(0, 0, 5, 0);
        shape.VerticalAlignment = VerticalAlignment.Center;
        shape.ToolTip = nodeClass;
        return shape;
    }

    /// <summary>Fills the tree from the Objects folder; false, with the reason in the status, when the server did not answer.</summary>
    private async Task<bool> LoadRootAsync()
    {
        AddressTree.Items.Clear();
        _showChecks.Clear();
        if (_client == null) return false;
        RefreshInDocument();
        var filter = CurrentFilter();
        try
        {
            foreach (var item in await _client.BrowseAsync())
                if (filter.Admits(item)) AddressTree.Items.Add(NodeItem(item, null));
        }
        catch (Exception ex)
        {
            PluginLog.Error("Browsing the Objects folder failed", ex);
            SetStatus($"The server did not answer: {ex.Message} Disconnect and connect again if it restarted.");
            return false;
        }
        await LoadViewsAsync();
        return true;
    }

    private async Task LoadViewsAsync()
    {
        if (_client == null) return;
        try
        {
            var views = await _client.ViewsAsync();
            if (views.Count == 0) return;
            var folderHeader = new StackPanel { Orientation = Orientation.Horizontal };
            folderHeader.Children.Add(new TextBlock { FontFamily = new FontFamily("Segoe MDL2 Assets"), Text = "\uE890", Margin = new Thickness(18, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0x40, 0xA0)) });
            folderHeader.Children.Add(new TextBlock { Text = $"Views ({views.Count})", VerticalAlignment = VerticalAlignment.Center });
            var folder = new TreeViewItem { Header = folderHeader, ToolTip = "The Views the server defines: parts of its address space for a purpose." };
            foreach (var v in views) folder.Items.Add(NodeItem(v, v.Address));
            AddressTree.Items.Add(folder);
        }
        catch (Exception ex)
        {
            PluginLog.Debug($"The server's Views could not be read: {ex.Message}");
        }
    }

    private TreeViewItem NodeItem(UaBrowseItem node, UaNodeAddress? view)
    {
        _names[Key(node.Address)] = node.DisplayName;
        var check = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), IsEnabled = node.NodeClass != "Method" };
        var scope = new TextBlock { Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, FontStyle = FontStyles.Italic };
        var inDocument = new TextBlock
        {
            FontFamily = new FontFamily("Segoe MDL2 Assets"), Text = "\uE73E", FontSize = 10, Margin = new Thickness(6, 1, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x4F)), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Already in the document",
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
        header.Children.Add(check);
        header.Children.Add(ClassShape(node.NodeClass));
        header.Children.Add(new TextBlock { Text = node.DisplayName, VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(inDocument);
        header.Children.Add(scope);
        var item = new TreeViewItem { Header = header, Tag = node, ToolTip = $"{node.NodeClass}  {node.Address}" };

        void Show()
        {
            inDocument.Visibility = _inDocument.Contains(Key(node.Address)) ? Visibility.Visible : Visibility.Collapsed;
            var selected = ItemFor(node.Address);
            check.IsChecked = selected != null;
            scope.Text = selected?.Scope switch
            {
                MirrorScope.Node => "   node only",
                MirrorScope.Children => "   with children",
                MirrorScope.Subtree => "   with everything below",
                _ => _excluded.Contains(Key(node.Address)) ? "   left out" : "",
            };
        }
        _showChecks.Add(Show);
        Show();
        check.Click += (_, __) => SetScope(node.Address, check.IsChecked == true ? MirrorScope.Subtree : null, view);

        if (node.NodeClass != "Method")
        {
            var menu = new ContextMenu();
            void Add(string text, Action action)
            {
                var m = new MenuItem { Header = text };
                m.Click += (_, __) => action();
                menu.Items.Add(m);
            }
            Add("Take the node only", () => SetScope(node.Address, MirrorScope.Node, view));
            Add("Take it with its children", () => SetScope(node.Address, MirrorScope.Children, view));
            Add("Take it with everything below", () => SetScope(node.Address, MirrorScope.Subtree, view));
            Add("Do not take it", () => SetScope(node.Address, null, view));
            menu.Items.Add(new Separator());
            Add("Leave it out of checked parts", () =>
            {
                _items.RemoveAll(i => i.Scope != MirrorScope.InstancesOf && Key(i.Node) == Key(node.Address));
                _excluded.Add(Key(node.Address));
                RefreshSelection();
            });
            Add("Instances of a type below it…", () => _ = PickInstancesAsync(node.Address));
            item.ContextMenu = menu;
            item.Items.Add(Pending);
        }

        item.Expanded += async (s, e) =>
        {
            if (e.OriginalSource != item || _client == null) return;
            if (item.Items.Count != 1 || item.Items[0] as string != Pending) return;
            item.Items.Clear();
            try
            {
                // A View organizes its nodes by its own references; below them, browsing keeps to the View.
                var isView = node.NodeClass == "View";
                var filter = CurrentFilter();
                foreach (var child in await _client.BrowseAsync(node.Address, isView ? null : view))
                    if (filter.Admits(child)) item.Items.Add(NodeItem(child, view));
            }
            catch (Exception ex)
            {
                PluginLog.Error($"Browsing {node.Address} failed", ex);
            }
        };
        return item;
    }

    private async void Filter_Changed(object sender, RoutedEventArgs e) => await LoadRootAsync();

    // ── selection helpers ───────────────────────────────────────────────────

    private void NamespacesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_client == null) return;
        var rows = _client.NamespaceTable.Select(uri => new ChecklistWindow.Row(uri, uri, _namespaces.Contains(uri))).ToList();
        var window = new ChecklistWindow("Namespaces to take",
            "Only nodes of the checked namespaces are taken, with what they hold. Nothing checked takes all.", rows, "\uE71C")
        { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true) return;
        _namespaces = window.Checked.Cast<string>().ToList();
        NamespacesButton.Content = _namespaces.Count == 0 ? "Namespaces: all" : $"Namespaces: {_namespaces.Count}";
        _ = LoadRootAsync();
    }

    private async void InstancesOfButton_Click(object sender, RoutedEventArgs e) =>
        await PickInstancesAsync(SelectedNode?.Address ?? ObjectsFolder);

    private async Task PickInstancesAsync(UaNodeAddress start)
    {
        var client = _client;
        if (client == null || _busy) return;
        IReadOnlyList<UaBrowseItem> types;
        SetBusy(true, "Reading the server's types …");
        try { types = await client.TypesAsync(); }
        catch (Exception ex) { PluginLog.Error("Reading the types failed", ex); SetStatus("Reading the types failed: " + ex.Message); return; }
        finally { SetBusy(false, null); }

        var picker = new TypePickerWindow(types, $"Instances of which type below {Label(start)}?") { Owner = Window.GetWindow(this) };
        if (picker.ShowDialog() != true || picker.Selected is not { } type) return;
        _names[Key(type.Address)] = type.DisplayName;

        IReadOnlyList<UaBrowseItem> found;
        SetBusy(true, $"Searching instances of {type.DisplayName} …");
        try { found = await client.InstancesOfAsync(start, await client.SubtypesAsync(type.Address)); }
        catch (Exception ex) { PluginLog.Error("Searching instances failed", ex); SetStatus("The search failed: " + ex.Message); return; }
        finally { SetBusy(false, null); }
        if (found.Count == 0)
        {
            SetStatus($"No instance of {type.DisplayName} below {Label(start)}.");
            return;
        }

        foreach (var f in found) _names[Key(f.Address)] = f.DisplayName;
        var rows = found.Select(f => new ChecklistWindow.Row(f.DisplayName, Key(f.Address), !_excluded.Contains(Key(f.Address)), f.Address.ToString())).ToList();
        var check = new ChecklistWindow($"Instances of {type.DisplayName}",
            $"{found.Count} found below {Label(start)}. Each checked instance is taken with everything below it; unchecked ones are left out.", rows, "\uE721")
        { Owner = Window.GetWindow(this) };
        if (check.ShowDialog() != true) return;
        var taken = check.Checked.Cast<UaNodeAddress>().ToHashSet();
        foreach (var f in found)
        {
            if (taken.Contains(Key(f.Address))) _excluded.Remove(Key(f.Address));
            else _excluded.Add(Key(f.Address));
        }
        _items.RemoveAll(i => i.Scope == MirrorScope.InstancesOf && Key(i.Node) == Key(start) && i.Type != null && Key(i.Type) == Key(type.Address));
        _items.Add(new MirrorItem(start, MirrorScope.InstancesOf, type.Address));
        RefreshSelection();
        SetStatus($"{taken.Count} of {found.Count} instance(s) of {type.DisplayName} below {Label(start)} checked.");
    }

    private void RemoveSelection_Click(object sender, RoutedEventArgs e)
    {
        if (SelectionList.SelectedItem is not SelectionRow row) return;
        if (row.Item != null) _items.Remove(row.Item);
        if (row.Excluded != null) _excluded.Remove(row.Excluded);
        RefreshSelection();
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        _items.Clear();
        _excluded.Clear();
        RefreshSelection();
    }

    private void LoadSelectionButton_Click(object sender, RoutedEventArgs e) => Guard("Loading the kept selection", LoadSelection);

    private void LoadSelection()
    {
        if (_client == null || _document == null) return;
        var ihName = HierarchyName();
        var ih = _document.CAEXFile.InstanceHierarchy[ihName];
        var server = ih == null ? null : AddressSpaceMirror.MirroredServer(ih, _client);
        if (server == null || MirrorSelection.ReadFrom(server) is not { } kept)
        {
            SetStatus($"'{ihName}' keeps no selection for this server.");
            return;
        }
        _items.Clear();
        _items.AddRange(kept.Items);
        _excluded.Clear();
        _excluded.UnionWith(kept.Excluded);
        _namespaces = kept.Filter.Namespaces.ToList();
        NamespacesButton.Content = _namespaces.Count == 0 ? "Namespaces: all" : $"Namespaces: {_namespaces.Count}";
        SkipPropertiesBox.IsChecked = kept.Filter.SkipProperties;
        ObjectsOnlyBox.IsChecked = kept.Filter.ObjectsOnly;
        ShowServerBox.IsChecked = !kept.Filter.HideServer;
        MirrorDepthBox.Text = kept.Depth.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RefreshSelection();
        _ = LoadRootAsync();
        SetStatus($"Loaded the selection kept in '{ihName}': {kept.Items.Count} part(s). 'Take into document' mirrors it again.");
    }

    private async void PreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_client == null || CurrentSelection() is not { } selection) return;
        SetBusy(true, "Counting …");
        try
        {
            var (nodes, truncated) = await AddressSpaceMirror.PreviewAsync(_client, selection, CurrentMaxNodes());
            SelectionSummary.Text = $"{nodes} element(s){(truncated ? ", stopped at the node limit" : "")}.";
        }
        catch (Exception ex)
        {
            PluginLog.Error("Counting failed", ex);
            SetStatus("Counting failed: " + ex.Message);
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private string HierarchyName() => string.IsNullOrWhiteSpace(MirrorHierarchyBox.Text) ? "OpcUaServer" : MirrorHierarchyBox.Text.Trim();

    // ── into the document ───────────────────────────────────────────────────

    private async void MirrorButton_Click(object sender, RoutedEventArgs e)
    {
        var document = _document;
        var client = _client;
        if (document == null || client == null || CurrentSelection() is not { } selection) return;

        var ihName = HierarchyName();
        var ih = document.CAEXFile.InstanceHierarchy[ihName];
        var vanished = VanishedNodes.Report;
        if (ih != null && AddressSpaceMirror.MirroredServer(ih, client) != null)
        {
            var again = new MirrorAgainWindow(ihName, _settings.MirrorVanished) { Owner = Window.GetWindow(this) };
            if (again.ShowDialog() != true) return;
            vanished = again.Vanished;
            _settings.MirrorVanished = vanished;
            _settings.Save();
            if (!again.Update)
            {
                var n = 2;
                while (document.CAEXFile.InstanceHierarchy[$"{ihName}_{n}"] != null) n++;
                ihName = $"{ihName}_{n}";
                ih = null;
            }
        }
        ih ??= document.CAEXFile.InstanceHierarchy.Append(ihName);
        MirrorHierarchyBox.Text = ih.Name;

        SetBusy(true, "Reading the selection from the server …");
        try
        {
            var result = await AddressSpaceMirror.MirrorSelectionAsync(client, selection, ih,
                new MirrorOptions { MaxNodes = CurrentMaxNodes(), Vanished = vanished });
            var fate = vanished switch { VanishedNodes.Mark => "marked", VanishedNodes.Remove => "removed", _ => "kept" };
            var message = $"'{ih.Name}': {result.Nodes} node(s), {result.Created} added, {result.Updated} updated, {result.Typed} typed by an imported UA type"
                          + (result.Linked > 0 ? $", {result.Linked} linked to their planned element (refBaseObj)" : "")
                          + (result.Vanished.Count > 0 ? $", {result.Vanished.Count} no longer on the server ({fate}, see log)" : "")
                          + (result.Truncated ? ". Stopped at the node limit." : ".");
            PluginLog.Info(message);
            foreach (var gone in result.Vanished) PluginLog.Warn("Not on the server any more: " + gone);
            foreach (var note in result.Notes) PluginLog.Info("Mirror: " + note);
            SetStatus(message + " Press Ctrl+S to save.");
            RefreshInDocument();
            Selected?.Invoke(this, new SelectionEventArgs(result.Server));
        }
        catch (Exception ex)
        {
            PluginLog.Error("Mirroring failed", ex);
            SetStatus("Mirroring failed: " + ex.Message);
        }
        finally
        {
            SetBusy(false, null);
            UpdateServerState();
        }
    }
}

/// <summary>A list of check boxes; returns the tags of the checked rows.</summary>
public sealed class ChecklistWindow : Window
{
    public sealed record Row(string Label, object Tag, bool IsChecked, string Detail = "");

    private readonly List<CheckBox> _boxes;

    public IReadOnlyList<object> Checked => _boxes.Where(b => b.IsChecked == true).Select(b => b.Tag).ToList();

    public ChecklistWindow(string title, string hint, IReadOnlyList<Row> rows, string glyph = "\uE762")
    {
        Width = 640;
        Height = 480;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        _boxes = rows.Select(r =>
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = r.Label });
            if (r.Detail.Length > 0) content.Children.Add(new TextBlock { Text = r.Detail, Foreground = DialogKit.Muted, Margin = new Thickness(10, 0, 0, 0) });
            return new CheckBox { Content = content, Tag = r.Tag, IsChecked = r.IsChecked, Margin = new Thickness(2, 2, 0, 2) };
        }).ToList();

        var list = new StackPanel();
        foreach (var b in _boxes) list.Children.Add(b);
        var all = DialogKit.Action("All");
        all.Click += (_, __) => _boxes.ForEach(b => b.IsChecked = true);
        var none = DialogKit.Action("None");
        none.Margin = new Thickness(6, 0, 0, 0);
        none.Click += (_, __) => _boxes.ForEach(b => b.IsChecked = false);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(all);
        left.Children.Add(none);
        var ok = DialogKit.Action("OK", primary: true);
        ok.Click += (_, __) => DialogResult = true;

        var frame = new Border
        {
            BorderBrush = ThemePalette.Current().Line, BorderThickness = new Thickness(1), Padding = new Thickness(6, 4, 6, 4),
            Child = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };
        DialogKit.Frame(this, glyph, DialogKit.Exchange, title, hint, frame, left, ok, DialogKit.Action("Cancel", cancel: true));
    }
}

/// <summary>Picks an ObjectType or VariableType of the server by name.</summary>
public sealed class TypePickerWindow : Window
{
    private readonly ListBox _list = new();
    private readonly TextBox _search = new();
    private readonly IReadOnlyList<UaBrowseItem> _types;

    public UaBrowseItem? Selected { get; private set; }

    public TypePickerWindow(IReadOnlyList<UaBrowseItem> types, string title)
    {
        Width = 620;
        Height = 520;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        _types = types.OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        _search.TextChanged += (_, __) => Filter();
        _list.MouseDoubleClick += (_, __) => Accept();

        var ok = DialogKit.Action("Search", primary: true);
        ok.Click += (_, __) => Accept();
        var search = DialogKit.WithPlaceholder(_search, "Type name");
        ((FrameworkElement)search).Margin = new Thickness(0, 0, 0, 6);
        var body = new DockPanel();
        DockPanel.SetDock(search, Dock.Top);
        body.Children.Add(search);
        body.Children.Add(_list);

        DialogKit.Frame(this, "\uE721", DialogKit.Exchange, title,
            "Instances of the type and of its subtypes are searched; below an instance found the search does not go on.",
            body, null, ok, DialogKit.Action("Cancel", cancel: true));
        Filter();
        Loaded += (_, __) => _search.Focus();
    }

    private void Accept()
    {
        Selected = DialogKit.Selected<UaBrowseItem>(_list);
        if (Selected != null) DialogResult = true;
    }

    private void Filter()
    {
        var text = _search.Text.Trim();
        _list.ItemsSource = _types.Where(t => text.Length == 0 || t.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Select(t => DialogKit.Entry(t.DisplayName, $"{t.NodeClass}   {t.Address.NamespaceUri}", t)).ToList();
    }
}
