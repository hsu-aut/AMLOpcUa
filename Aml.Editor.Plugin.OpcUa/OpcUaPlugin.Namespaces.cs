// The namespaces of the document in detail: what a namespace brings (its
// types by kind), what it builds on and what builds on it, how many elements
// use its types, its types to draw, and removing it when nothing needs it.
// Also NodeSet files dropped on the plugin, and the dialog for models an
// import cannot find.

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Aml.Editor.Plugin.OpcUa.Diagnostics;
using Aml.Engine.CAEX;
using OpcUaAml.Import;
using OpcUaAml.NodeSets;
using OpcUaAml.Types;

namespace Aml.Editor.Plugin.OpcUa;

public partial class OpcUaPlugin
{
    // ── namespace details ───────────────────────────────────────────────────

    private void NamespaceList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowNamespaceDetails();

    /// <summary>"DI" for http://opcfoundation.org/UA/DI/, "Robotics" for …/Robotics/, the last part of a URN.</summary>
    private static string ShortName(string uri)
    {
        var parts = uri.Split(new[] { '/', ':' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? uri : parts[^1];
    }

    private void ShowNamespaceDetails()
    {
        var panel = NamespaceDetailsPanel;
        panel.Children.Clear();
        var document = _document;
        if (document == null || NamespaceList.SelectedItem is not NamespaceRow row)
        {
            NamespaceDetailsBorder.Visibility = Visibility.Collapsed;
            return;
        }
        NamespaceDetails details;
        try { details = NamespaceInspector.Of(document, row.NamespaceUri); }
        catch (Exception ex)
        {
            PluginLog.Error($"Reading {row.NamespaceUri} failed", ex);
            NamespaceDetailsBorder.Visibility = Visibility.Collapsed;
            return;
        }
        NamespaceDetailsBorder.Visibility = Visibility.Visible;
        var palette = ThemePalette.Current(this);

        // Title, URI, version
        panel.Children.Add(new TextBlock { Text = ShortName(details.NamespaceUri), FontSize = 18, FontWeight = FontWeights.SemiBold });
        var uriRow = new DockPanel { Margin = new Thickness(0, 2, 0, 8) };
        var copy = IconButton("", "Copy the namespace URI", () => CopyText(details.NamespaceUri));
        DockPanel.SetDock(copy, Dock.Right);
        uriRow.Children.Add(copy);
        uriRow.Children.Add(new TextBlock { Text = details.NamespaceUri, Foreground = palette.Muted, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(uriRow);
        var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        if (row.ModelVersion.Length > 0) chips.Children.Add(Chip($"Version {row.ModelVersion}", palette));
        if (row.Published.Length > 0) chips.Children.Add(Chip($"published {row.Published}", palette));
        chips.Children.Add(Chip($"{details.Libraries.Count} libraries", palette));
        panel.Children.Add(chips);

        // What it brings
        var tiles = new UniformGrid2(2);
        tiles.Add(Tile(details.ObjectTypes, "ObjectTypes", ClassShape("Object"), palette));
        tiles.Add(Tile(details.VariableTypes, "VariableTypes", ClassShape("Variable"), palette));
        tiles.Add(Tile(details.DataTypes, "DataTypes", Glyph("", DialogKit.Verify), palette));
        tiles.Add(Tile(details.ReferenceTypes, "ReferenceTypes", Glyph("", DialogKit.Relate), palette));
        panel.Children.Add(tiles.Panel);

        // Dependencies and use
        panel.Children.Add(Section("Builds on"));
        panel.Children.Add(details.BuildsOn.Count == 0 ? Note("No other namespace.", palette) : Links(details.BuildsOn));
        panel.Children.Add(Section("Used by"));
        panel.Children.Add(details.UsedBy.Count == 0 ? Note("No other namespace of the document builds on it.", palette) : Links(details.UsedBy));
        panel.Children.Add(Section("In this document"));
        panel.Children.Add(Note(details.Instances == 0 ? "No element of the instance hierarchies uses its types."
            : $"{details.Instances} element(s) of the instance hierarchies use its types.", palette));

        // Its types, to draw
        var types = UaTypes.AllTypes(document)
            .Where(t => details.Libraries.Any(l => t.Path.StartsWith($"[{l}]", StringComparison.Ordinal)))
            .OrderBy(t => t.Type.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (types.Count > 0)
        {
            panel.Children.Add(Section($"Types ({types.Count})"));
            var search = new TextBox();
            var list = new ListBox { Height = 170 };
            void Fill() => list.ItemsSource = types
                .Where(t => search.Text.Length == 0 || t.Type.Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase))
                .Select(t => DialogKit.Entry(t.Type.Name, UaTypes.IsAbstract(t.Type) ? "abstract" : "", t.Type)).ToList();
            search.TextChanged += (_, __) => Fill();
            void Draw()
            {
                if (DialogKit.Selected<SystemUnitFamilyType>(list) is not { } type) return;
                Tabs.SelectedItem = DiagramTab;
                DrawDiagramOf(type);
            }
            list.MouseDoubleClick += (_, __) => Draw();
            list.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; Draw(); } };
            Fill();
            var box = DialogKit.WithPlaceholder(search, "Search; double click or Enter draws a type");
            ((FrameworkElement)box).Margin = new Thickness(0, 0, 0, 4);
            panel.Children.Add(box);
            panel.Children.Add(list);
        }

        // Actions
        var actions = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        var edit = DialogKit.Action("Edit in the modeler");
        edit.Click += (s, e) => NamespaceEdit_Click(s, e);
        actions.Children.Add(edit);
        var blockers = NamespaceInspector.RemovalBlockers(document, details.NamespaceUri);
        var remove = DialogKit.Action("Remove…");
        remove.Margin = new Thickness(6, 0, 0, 0);
        remove.IsEnabled = blockers.Count == 0 && !_busy;
        remove.ToolTip = blockers.Count == 0 ? "Remove the namespace's libraries from the document." : "Still needed: " + string.Join(" ", blockers);
        ToolTipService.SetShowOnDisabled(remove, true);
        remove.Click += (_, __) => RemoveNamespace(details.NamespaceUri);
        actions.Children.Add(remove);
        panel.Children.Add(actions);
    }

    private void RemoveNamespace(string uri)
    {
        if (_document == null) return;
        if (!DialogKit.Confirm(Window.GetWindow(this), "\uE74D", DialogKit.Danger, "Remove the namespace?",
                $"The libraries of {uri} leave the document. No other namespace builds on it and no element uses its types. " +
                "Ctrl+Z in the editor does not bring them back; importing the NodeSet again does.",
                "Remove", risky: true))
            return;
        Guard($"Removing {uri}", () =>
        {
            var removed = NamespaceInspector.Remove(_document, uri);
            foreach (var lib in removed) PluginLog.Info("Removed library " + lib);
            UpdateState();
            SetStatus($"Removed {uri} ({removed.Count} libraries). Press Ctrl+S to save.");
        });
    }

    private FrameworkElement Links(IEnumerable<string> uris)
    {
        var panel = new StackPanel();
        foreach (var uri in uris)
        {
            var link = new Hyperlink(new Run(uri)) { ToolTip = "Show this namespace" };
            link.Click += (_, __) =>
            {
                var target = (NamespaceList.ItemsSource as IEnumerable<NamespaceRow>)?.FirstOrDefault(r => r.NamespaceUri == uri);
                if (target != null) NamespaceList.SelectedItem = target;
            };
            panel.Children.Add(new TextBlock(link) { Margin = new Thickness(0, 1, 0, 1), TextTrimming = TextTrimming.CharacterEllipsis });
        }
        return panel;
    }

    private static TextBlock Section(string text) =>
        new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 3) };

    private static TextBlock Note(string text, ThemePalette palette) =>
        new() { Text = text, Foreground = palette.Muted, TextWrapping = TextWrapping.Wrap };

    private static Border Chip(string text, ThemePalette palette) => new()
    {
        Child = new TextBlock { Text = text, FontSize = 11 },
        BorderBrush = palette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9),
        Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 6, 4), Background = palette.Surface,
    };

    private static Border Tile(int count, string label, FrameworkElement icon, ThemePalette palette)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(icon);
        head.Children.Add(new TextBlock { Text = count.ToString(System.Globalization.CultureInfo.InvariantCulture), FontSize = 20, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var body = new StackPanel();
        body.Children.Add(head);
        body.Children.Add(new TextBlock { Text = label, Foreground = palette.Muted });
        return new Border
        {
            Child = body, Background = palette.Surface, BorderBrush = palette.Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 6, 6),
        };
    }

    private static TextBlock Glyph(string glyph, Brush brush) => new()
    {
        FontFamily = new FontFamily("Segoe MDL2 Assets"), Text = glyph, Foreground = brush, FontSize = 12,
        Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
    };

    private static Button IconButton(string glyph, string tip, Action action)
    {
        var b = new Button
        {
            Content = new TextBlock { FontFamily = new FontFamily("Segoe MDL2 Assets"), Text = glyph, FontSize = 11 },
            ToolTip = tip, Padding = new Thickness(3, 1, 3, 1), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top,
        };
        System.Windows.Automation.AutomationProperties.SetName(b, tip);
        b.Click += (_, __) => action();
        return b;
    }

    /// <summary>A grid that fills row by row with a fixed number of columns.</summary>
    private sealed class UniformGrid2
    {
        private readonly int _columns;
        public Grid Panel { get; } = new();

        public UniformGrid2(int columns)
        {
            _columns = columns;
            for (var i = 0; i < columns; i++) Panel.ColumnDefinitions.Add(new ColumnDefinition());
        }

        public void Add(UIElement element)
        {
            var index = Panel.Children.Count;
            if (index % _columns == 0) Panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(element, index / _columns);
            Grid.SetColumn(element, index % _columns);
            Panel.Children.Add(element);
        }
    }

    // ── NodeSet files dropped on the plugin ─────────────────────────────────

    private static string[] DroppedNodeSets(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.Where(f => f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && File.Exists(f)).ToArray()
            : Array.Empty<string>();

    private bool CanImport => _document != null && !_busy && _document.CAEXFile.SchemaVersion == LibraryMerger.RequiredSchemaVersion;

    private void Plugin_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (DroppedNodeSets(e).Length == 0) return;
        e.Effects = CanImport ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.Visibility = CanImport ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void Plugin_PreviewDragLeave(object sender, DragEventArgs e)
    {
        // Leaving a child raises this too; only leaving the plugin hides the hint.
        var p = e.GetPosition(this);
        if (p.X <= 0 || p.Y <= 0 || p.X >= ActualWidth || p.Y >= ActualHeight) DropOverlay.Visibility = Visibility.Collapsed;
    }

    private async void Plugin_PreviewDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        var files = DroppedNodeSets(e);
        if (files.Length == 0 || !CanImport || _document is not { } document) return;
        e.Handled = true;
        Tabs.SelectedIndex = 0;
        foreach (var file in files)
        {
            if (NodeSetInfo.TryRead(file) == null)
            {
                SetStatus($"{Path.GetFileName(file)} is not an OPC UA NodeSet.");
                continue;
            }
            await ImportFileAsync(document, file, Array.Empty<string>());
        }
    }

    // ── models an import cannot find ────────────────────────────────────────

    private static string CloudCache =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AMLOpcUa", "cloudlibrary");

    /// <summary>
    /// Asks what to do about models a NodeSet requires and no folder holds;
    /// true when they may be there now and the import should try again.
    /// </summary>
    private async Task<bool> ResolveMissingAsync(string file, IReadOnlyList<ModelRef> missing)
    {
        var window = new MissingModelsWindow(Path.GetFileName(file), missing) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true) return false;
        switch (window.Choice)
        {
            case MissingModelsWindow.Action.AddFolder:
                var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Folder with the NodeSets " + Path.GetFileName(file) + " requires" };
                if (dialog.ShowDialog() != true) return false;
                if (!_settings.NodeSetFolders.Contains(dialog.FolderName)) _settings.NodeSetFolders.Add(dialog.FolderName);
                _settings.Save();
                PluginLog.Info("NodeSet folder added: " + dialog.FolderName);
                return true;
            case MissingModelsWindow.Action.Cloud:
                return await DownloadFromCloudAsync(ShortName(missing[0].ModelUri)) != null;
            default:
                return false;
        }
    }

    /// <summary>
    /// Lets the user pick a model of the Cloud Library and downloads it with the
    /// models it requires; returns the files, the chosen model's first, or null.
    /// </summary>
    private async Task<IReadOnlyList<string>?> DownloadFromCloudAsync(string search)
    {
        var window = new CloudLibraryWindow(_settings.CloudLibraryUser, search) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true || window.Selected == null) return null;
        _settings.CloudLibraryUser = window.UserName;
        _settings.Save();
        SetBusy(true, $"Downloading {window.Selected.NamespaceUri} …");
        try
        {
            var catalog = NodeSetCatalog.Create(new[] { CloudCache }.Concat(_settings.NodeSetFolders));
            var files = await window.CreateClient().DownloadWithDependenciesAsync(window.Selected.Identifier, CloudCache, catalog);
            foreach (var f in files) PluginLog.Info("Downloaded " + f);
            return files;
        }
        catch (CloudLibraryException ex)
        {
            PluginLog.Error(ex.Message);
            SetStatus(ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            PluginLog.Error("Downloading from the Cloud Library failed", ex);
            SetStatus("Downloading from the Cloud Library failed: " + ex.Message);
            return null;
        }
        finally
        {
            SetBusy(false, null);
        }
    }
}

/// <summary>The models a NodeSet requires that no folder holds, and what to do about them.</summary>
public sealed class MissingModelsWindow : Window
{
    public enum Action { AddFolder, Cloud }

    public Action Choice { get; private set; }

    public MissingModelsWindow(string file, IReadOnlyList<ModelRef> missing)
    {
        Width = 620;
        Height = 400;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        var list = new ListBox
        {
            ItemsSource = missing.Select(m => DialogKit.Entry(m.ModelUri,
                string.Join("  ", new[] { m.Version, m.PublicationDate?.ToString("yyyy-MM-dd") }.Where(s => !string.IsNullOrEmpty(s))), m)).ToList(),
        };
        Button Choose(string text, Action action, bool primary)
        {
            var b = DialogKit.Action(text, primary);
            b.Click += (_, __) => { Choice = action; DialogResult = true; };
            return b;
        }
        DialogKit.Frame(this, "", DialogKit.Verify, "Models missing",
            $"{file} requires models that neither its folder nor the NodeSet folders hold. Add a folder that holds them, or fetch them from the UA Cloud Library; the import then tries again.",
            list, null,
            Choose("Add NodeSet folder…", Action.AddFolder, true),
            Choose("Search the Cloud Library…", Action.Cloud, false),
            DialogKit.Action("Cancel", cancel: true));
    }
}
