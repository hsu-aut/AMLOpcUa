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
using OpcUaAml.Export;
using OpcUaAml.Import;
using OpcUaAml.ModelDesign;
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
        var doc = DialogKit.Action("Documentation…");
        doc.Margin = new Thickness(6, 0, 0, 0);
        doc.ToolTip = "One HTML page of the model: its types with their declarations and diagrams, its DataTypes and ReferenceTypes.";
        doc.Click += (_, __) => DocumentNamespace(details.NamespaceUri);
        actions.Children.Add(doc);
        var design = DialogKit.Action("ModelDesign…");
        design.Margin = new Thickness(6, 0, 0, 0);
        design.ToolTip = "Write the model as a ModelDesign file with its identifier file, the form the OPC Foundation's ModelCompiler reads.";
        design.Click += (_, __) => WriteModelDesign(details.NamespaceUri);
        actions.Children.Add(design);
        var publish = DialogKit.Action("Publish…");
        publish.Margin = new Thickness(6, 0, 0, 0);
        publish.ToolTip = "Publish the model's NodeSet to the UA Cloud Library, for others to find and download.";
        publish.Click += async (_, __) => await PublishNamespaceAsync(details.NamespaceUri);
        actions.Children.Add(publish);
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

    private static readonly System.Net.Http.HttpClient CloudUploadHttp = CloudLibraryClient.CreateHttp();

    /// <summary>Publishes the NodeSet a namespace came from to the UA Cloud Library, after asking what the library needs.</summary>
    private async Task PublishNamespaceAsync(string uri)
    {
        if (_busy) return;
        var info = ModelerCatalog().Find(uri);
        if (info == null)
        {
            SetStatus($"No NodeSet file of {uri} in the modeler's models or the NodeSet folders; publishing sends that file.");
            return;
        }
        var model = info.Models.FirstOrDefault(m => m.Model.ModelUri == uri)?.Model;
        var window = new CloudUploadWindow(uri, model?.Version, _settings.CloudLibraryUser) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true || window.Metadata is not { } metadata) return;
        _settings.CloudLibraryUser = window.UserName;
        _settings.Save();
        SetBusy(true, $"Publishing {uri} to the UA Cloud Library …");
        try
        {
            var answer = await window.CreateClient(CloudUploadHttp).UploadAsync(await File.ReadAllTextAsync(info.FilePath), metadata, window.Overwrite);
            PluginLog.Info($"Published {uri} ({info.FilePath}) to the UA Cloud Library: {answer}");
            SetStatus($"Published {uri}; the OPC Foundation reviews it before it is listed.");
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Publishing {uri} failed", ex);
            SetStatus($"Publishing {uri} failed: {ex.Message}");
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    /// <summary>Writes the model's documentation as HTML and opens it.</summary>
    private void DocumentNamespace(string uri)
    {
        if (_document is not { } document) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = $"Documentation of {uri}", Filter = "HTML (*.html)|*.html", FileName = ShortName(uri) + ".html",
        };
        if (dialog.ShowDialog() != true) return;
        Guard("Writing the documentation", () =>
        {
            File.WriteAllText(dialog.FileName, OpcUaAml.Documentation.ModelDocumentation.Html(document, uri));
            PluginLog.Info($"Documentation of {uri} written to {dialog.FileName}.");
            SetStatus($"Documentation written to {Path.GetFileName(dialog.FileName)}.");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
        });
    }

    /// <summary>
    /// Writes the model as a ModelDesign, with the identifier file beside it.
    /// The model comes out of the document itself (the inverse of Annex A), so
    /// the design holds what the document holds now.
    /// </summary>
    private void WriteModelDesign(string uri)
    {
        if (_document is not { } document) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = $"ModelDesign of {uri}", Filter = "ModelDesign (*.xml)|*.xml", FileName = ShortName(uri) + ".xml",
        };
        if (dialog.ShowDialog() != true) return;
        Guard("Writing the ModelDesign", () =>
        {
            var nodeSet = NodeSetExporter.Export(document, new NodeSetExportOptions
            {
                Mode = ExportMode.AnnexAInverse,
                NamespaceUri = uri,
            });
            var identifiers = ModelDesignWriter.From(nodeSet).Save(dialog.FileName);
            PluginLog.Info($"ModelDesign of {uri} written to {dialog.FileName}, identifiers to {identifiers ?? "(none)"}.");
            SetStatus($"ModelDesign written to {Path.GetFileName(dialog.FileName)}"
                      + (identifiers != null ? $", with {Path.GetFileName(identifiers)} beside it" : "")
                      + ". The ModelCompiler turns it into a NodeSet and code.");
        });
    }

    /// <summary>Whether the file is a ModelDesign rather than a NodeSet.</summary>
    private static bool IsModelDesign(string file)
    {
        if (!Path.GetExtension(file).Equals(".xml", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var reader = System.Xml.XmlReader.Create(file, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit });
            while (reader.Read())
            {
                if (reader.NodeType != System.Xml.XmlNodeType.Element) continue;
                return reader.NamespaceURI == ModelDesignWriter.DesignNamespace;
            }
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException
                                   or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            // Not readable, not XML, or a path the reader will not take: the
            // import says so in its own words.
        }
        return false;
    }

    /// <summary>
    /// Runs the ModelCompiler over a design and returns the NodeSet it wrote,
    /// or null with a message when the compiler is missing or the design does
    /// not compile. The folder is the caller's to delete.
    /// </summary>
    /// <summary>The folder of the last compiled design, so the caller can delete it.</summary>
    private string? _designFolder;

    private async Task<string?> CompileDesignAsync(string design)
    {
        if (ModelCompilerTool.Locate(_settings.ModelCompilerPath) is null)
        {
            SetStatus($"{Path.GetFileName(design)} is a ModelDesign, not a NodeSet. "
                      + ModelCompilerTool.InstallHint.Replace(Environment.NewLine, " ").Replace("\n", " "));
            PluginLog.Warn($"{design} is a ModelDesign and the ModelCompiler is not installed.");
            return null;
        }
        var folder = Directory.CreateTempSubdirectory("amlopcua-design-").FullName;
        _designFolder = folder;
        SetBusy(true, $"Compiling {Path.GetFileName(design)} with the ModelCompiler …");
        try
        {
            var result = await ModelCompilerTool.CompileAsync(design, folder,
                new CompileOptions { Executable = _settings.ModelCompilerPath });
            PluginLog.Info($"ModelCompiler turned {design} into {result.NodeSetPath}.");
            return result.NodeSetPath;
        }
        catch (ModelCompilerException ex)
        {
            PluginLog.Error($"Compiling {design} failed", ex);
            SetStatus($"The ModelCompiler could not compile {Path.GetFileName(design)}: {FirstLine(ex.Message)}");
            DeleteFolder(folder);
            return null;
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    /// <summary>The first line of a message, which carries what went wrong.</summary>
    private static string FirstLine(string message) =>
        new StringReader(message).ReadLine() ?? message;

    private static void DeleteFolder(string folder)
    {
        try { Directory.Delete(folder, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The files are in the document now; a folder left in the temp directory is no error.
        }
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
            if (!IsModelDesign(file) && NodeSetInfo.TryRead(file) == null)
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

    /// <summary>The index and the files of the OPC Foundation's NodeSets from GitHub; the same folder uaaml uses.</summary>
    private static string OpcfFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AMLOpcUa", "opcfoundation");

    /// <summary>The folders holding downloaded files: they keep the repository's folders, and the catalog reads one level.</summary>
    private static IEnumerable<string> OpcfFolders()
    {
        var files = Path.Combine(OpcfFolder, "files");
        return Directory.Exists(files) ? Directory.GetDirectories(files, "*", SearchOption.AllDirectories).Prepend(files) : Array.Empty<string>();
    }

    private static readonly System.Net.Http.HttpClient OpcfHttp = new() { Timeout = TimeSpan.FromSeconds(60) };

    private static OpcFoundationNodeSets OpcfSource() => new(OpcfHttp, OpcfFolder);

    private async void OpcfButton_Click(object sender, RoutedEventArgs e)
    {
        var document = _document;
        if (document == null || _busy) return;
        if (document.CAEXFile.SchemaVersion != LibraryMerger.RequiredSchemaVersion)
        {
            SetStatus($"This document uses CAEX {document.CAEXFile.SchemaVersion}; OPC UA libraries need CAEX 3.0.");
            return;
        }
        var files = await DownloadFromOpcFoundationAsync("");
        if (files == null || files.Count == 0) return;
        await ImportFileAsync(document, files[0], Array.Empty<string>());
    }

    /// <summary>
    /// Lets the user pick one of the OPC Foundation's NodeSets and downloads it
    /// with the models it requires; the files, the chosen model's first, or
    /// null. The dialog leads to the Cloud Library when the user asks for it.
    /// </summary>
    private async Task<IReadOnlyList<string>?> DownloadFromOpcFoundationAsync(string search)
    {
        var window = new OpcFoundationWindow(OpcfSource(), search) { Owner = Window.GetWindow(this) };
        var chosen = window.ShowDialog() == true ? window.Selected : null;
        if (window.CloudLibraryWanted) return await DownloadFromCloudAsync(search);
        if (chosen == null) return null;
        SetBusy(true, $"Downloading {chosen.ModelUri} …");
        try
        {
            var catalog = NodeSetCatalog.Create(OpcfFolders().Concat(_settings.NodeSetFolders));
            var missing = new List<string>();
            var files = await OpcfSource().DownloadWithDependenciesAsync(chosen, catalog, missing);
            foreach (var f in files) PluginLog.Info("Downloaded " + f);
            foreach (var m in missing) PluginLog.Warn($"The OPC Foundation's repository has no model {m}; the import will ask for it.");
            return files;
        }
        catch (Exception ex)
        {
            PluginLog.Error("Downloading from the OPC Foundation failed", ex);
            SetStatus("Downloading from the OPC Foundation failed: " + ex.Message);
            return null;
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    /// <summary>The missing models the OPC Foundation's repository has, downloaded; true when any arrived.</summary>
    private async Task<bool> FetchMissingFromOpcFoundationAsync(IReadOnlyList<ModelRef> missing)
    {
        SetBusy(true, "Fetching the missing models from the OPC Foundation …");
        try
        {
            var catalog = NodeSetCatalog.Create(OpcfFolders().Concat(_settings.NodeSetFolders));
            var notThere = new List<string>();
            var files = await OpcfSource().DownloadModelsAsync(missing.Select(m => m.ModelUri), catalog, notThere);
            foreach (var f in files) PluginLog.Info("Downloaded " + f);
            if (notThere.Count > 0)
                PluginLog.Warn("Not published by the OPC Foundation: " + string.Join(", ", notThere) + ". A NodeSet folder or the UA Cloud Library may have them.");
            return files.Count > 0;
        }
        catch (Exception ex)
        {
            PluginLog.Error("Fetching from the OPC Foundation failed", ex);
            SetStatus("Fetching from the OPC Foundation failed: " + ex.Message);
            return false;
        }
        finally
        {
            SetBusy(false, null);
        }
    }

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
            case MissingModelsWindow.Action.OpcFoundation:
                return await FetchMissingFromOpcFoundationAsync(missing);
            case MissingModelsWindow.Action.AddFolder:
                var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Folder with the NodeSets " + Path.GetFileName(file) + " requires" };
                if (dialog.ShowDialog() != true) return false;
                if (!_settings.NodeSetFolders.Contains(dialog.FolderName)) _settings.NodeSetFolders.Add(dialog.FolderName);
                _settings.Save();
                PluginLog.Info("NodeSet folder added: " + dialog.FolderName);
                return true;
            case MissingModelsWindow.Action.Cloud:
                // One search per missing model; a download brings what it requires,
                // so a later one may be there already. Cancelling stops the round.
                var any = false;
                foreach (var model in missing)
                {
                    var have = NodeSetCatalog.Create(new[] { CloudCache }.Where(Directory.Exists).Concat(_settings.NodeSetFolders));
                    if (have.Find(model.ModelUri) != null) continue;
                    if (await DownloadFromCloudAsync(ShortName(model.ModelUri)) == null) break;
                    any = true;
                }
                return any;
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
    public enum Action { OpcFoundation, AddFolder, Cloud }

    public Action Choice { get; private set; }

    public MissingModelsWindow(string file, IReadOnlyList<ModelRef> missing)
    {
        Width = 760;
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
            $"{file} requires models that neither its folder nor the NodeSet folders hold. Fetch them from the OPC Foundation's published NodeSets "
            + "(no account needed), add a folder that holds them, or search the UA Cloud Library; the import then tries again.",
            list, null,
            Choose("From the OPC Foundation", Action.OpcFoundation, true),
            Choose("Add NodeSet folder…", Action.AddFolder, false),
            Choose("Search the Cloud Library…", Action.Cloud, false),
            DialogKit.Action("Cancel", cancel: true));
    }
}
