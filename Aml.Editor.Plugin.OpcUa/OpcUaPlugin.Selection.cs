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

    private async Task LoadRootAsync()
    {
        AddressTree.Items.Clear();
        _showChecks.Clear();
        if (_client == null) return;
        var filter = CurrentFilter();
        foreach (var item in await _client.BrowseAsync())
            if (filter.Admits(item)) AddressTree.Items.Add(NodeItem(item, null));
        try
        {
            var views = await _client.ViewsAsync();
            if (views.Count == 0) return;
            var folder = new TreeViewItem { Header = $"Views ({views.Count})", ToolTip = "The Views the server defines: parts of its address space for a purpose." };
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
        var scope = new TextBlock { Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(check);
        header.Children.Add(new TextBlock { Text = $"{node.DisplayName}   [{node.NodeClass}]", VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(scope);
        var item = new TreeViewItem { Header = header, Tag = node, ToolTip = node.Address.ToString() };

        void Show()
        {
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
            "Only nodes of the checked namespaces are taken, with what they hold. Nothing checked takes all.", rows)
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
        catch (Exception ex) { SetStatus("Reading the types failed: " + ex.Message); return; }
        finally { SetBusy(false, null); }

        var picker = new TypePickerWindow(types, $"Instances of which type below {Label(start)}?") { Owner = Window.GetWindow(this) };
        if (picker.ShowDialog() != true || picker.Selected is not { } type) return;
        _names[Key(type.Address)] = type.DisplayName;

        IReadOnlyList<UaBrowseItem> found;
        SetBusy(true, $"Searching instances of {type.DisplayName} …");
        try { found = await client.InstancesOfAsync(start, await client.SubtypesAsync(type.Address)); }
        catch (Exception ex) { SetStatus("The search failed: " + ex.Message); return; }
        finally { SetBusy(false, null); }
        if (found.Count == 0)
        {
            SetStatus($"No instance of {type.DisplayName} below {Label(start)}.");
            return;
        }

        foreach (var f in found) _names[Key(f.Address)] = f.DisplayName;
        var rows = found.Select(f => new ChecklistWindow.Row($"{f.DisplayName}   {f.Address}", Key(f.Address), !_excluded.Contains(Key(f.Address)))).ToList();
        var check = new ChecklistWindow($"Instances of {type.DisplayName}",
            "Each checked instance is taken with everything below it; unchecked ones are left out.", rows)
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

    private void LoadSelectionButton_Click(object sender, RoutedEventArgs e)
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
            var (nodes, truncated) = await AddressSpaceMirror.PreviewAsync(_client, selection);
            SelectionSummary.Text = $"{nodes} element(s){(truncated ? ", stopped at the node limit" : "")}.";
        }
        catch (Exception ex)
        {
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
        if (ih != null && AddressSpaceMirror.MirroredServer(ih, client) != null)
        {
            var answer = MessageBox.Show(Window.GetWindow(this),
                $"'{ihName}' already holds a mirror of this server.\n\n"
                + "Yes: update it. Values and types are read again, new nodes are added, nodes the server no longer has are reported.\n"
                + "No: mirror into a new InstanceHierarchy, keeping the earlier state.",
                "Mirror again", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.No)
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
            var result = await AddressSpaceMirror.MirrorSelectionAsync(client, selection, ih);
            var message = $"'{ih.Name}': {result.Nodes} node(s), {result.Created} added, {result.Updated} updated, {result.Typed} typed by an imported UA type"
                          + (result.Linked > 0 ? $", {result.Linked} linked to their planned element (refBaseObj)" : "")
                          + (result.Vanished.Count > 0 ? $", {result.Vanished.Count} no longer on the server (see log)" : "")
                          + (result.Truncated ? ". Stopped at the node limit." : ".");
            PluginLog.Info(message);
            foreach (var gone in result.Vanished) PluginLog.Warn("Not on the server any more: " + gone);
            foreach (var note in result.Notes) PluginLog.Info("Mirror: " + note);
            SetStatus(message + " Press Ctrl+S to save.");
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
    public sealed record Row(string Label, object Tag, bool IsChecked);

    private readonly List<CheckBox> _boxes;

    public IReadOnlyList<object> Checked => _boxes.Where(b => b.IsChecked == true).Select(b => b.Tag).ToList();

    public ChecklistWindow(string title, string hint, IReadOnlyList<Row> rows)
    {
        Title = title;
        Width = 620;
        Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _boxes = rows.Select(r => new CheckBox { Content = r.Label, Tag = r.Tag, IsChecked = r.IsChecked, Margin = new Thickness(0, 1, 0, 1) }).ToList();

        var list = new StackPanel();
        foreach (var b in _boxes) list.Children.Add(b);
        var ok = new Button { Content = "OK", Width = 90, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        ok.Click += (_, __) => DialogResult = true;
        var all = new Button { Content = "All", Width = 70, Margin = new Thickness(0, 0, 6, 0) };
        all.Click += (_, __) => _boxes.ForEach(b => b.IsChecked = true);
        var none = new Button { Content = "None", Width = 70, Margin = new Thickness(0, 0, 18, 0) };
        none.Click += (_, __) => _boxes.ForEach(b => b.IsChecked = false);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(all);
        buttons.Children.Add(none);
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Cancel", Width = 90, IsCancel = true });

        var text = new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 6) };
        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(text, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(text);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }
}

/// <summary>Picks an ObjectType or VariableType of the server by name.</summary>
public sealed class TypePickerWindow : Window
{
    private readonly ListBox _list = new();
    private readonly TextBox _search = new() { Margin = new Thickness(0, 0, 0, 4) };
    private readonly IReadOnlyList<UaBrowseItem> _types;

    public UaBrowseItem? Selected { get; private set; }

    public TypePickerWindow(IReadOnlyList<UaBrowseItem> types, string title)
    {
        Title = title;
        Width = 560;
        Height = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _types = types.OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        _search.TextChanged += (_, __) => Filter();
        _list.MouseDoubleClick += (_, __) => Accept();

        var ok = new Button { Content = "Search", Width = 90, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        ok.Click += (_, __) => Accept();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Cancel", Width = 90, IsCancel = true });

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(_search, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(_search);
        root.Children.Add(buttons);
        root.Children.Add(_list);
        Content = root;
        Filter();
        Loaded += (_, __) => _search.Focus();
    }

    private void Accept()
    {
        Selected = (_list.SelectedItem as Row)?.Type;
        if (Selected != null) DialogResult = true;
    }

    private void Filter()
    {
        var text = _search.Text.Trim();
        _list.ItemsSource = _types.Where(t => text.Length == 0 || t.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Select(t => new Row(t)).ToList();
    }

    private sealed record Row(UaBrowseItem Type)
    {
        public override string ToString() => $"{Type.DisplayName}   ({Type.NodeClass}, {Type.Address.NamespaceUri})";
    }
}
