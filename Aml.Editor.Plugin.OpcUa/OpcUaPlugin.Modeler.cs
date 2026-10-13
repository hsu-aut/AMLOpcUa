// The Modeler tab: the graphical modeler (NodeSet.js) in a WebView2 control.
//
// It edits NodeSets, not the document: "Edit" opens the NodeSet a namespace
// of the document was imported from, "New model" starts an empty one, and the
// modeler's "Apply to document" sends the NodeSet back. The plugin keeps it in
// its models folder and imports it like any NodeSet, so the document gets the
// libraries of OPC 10000-83 Annex A and a newer publication replaces an older.

using System.IO;
using System.Windows;
using System.Windows.Controls;
using Aml.Editor.Plugin.OpcUa.Bridge;
using Aml.Editor.Plugin.OpcUa.Diagnostics;
using Aml.Editor.Plugin.OpcUa.Localization;
using Aml.Engine.CAEX;
using Microsoft.Win32;
using OpcUaAml.Import;
using OpcUaAml.NodeSets;

namespace Aml.Editor.Plugin.OpcUa;

public partial class OpcUaPlugin
{
    private ModelerWebView? _modeler;

    /// <summary>Where applied models are kept; searched like a NodeSet folder.</summary>
    internal static string ModelsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AMLOpcUa", "models");

    /// <summary>Models the modeler ships itself and need not be sent.</summary>
    private static readonly HashSet<string> BundledInModeler = new(StringComparer.Ordinal)
    {
        "http://opcfoundation.org/UA/", "http://opcfoundation.org/UA/DI/",
    };

    private void InitModelerTab()
    {
        // The WebView2 control starts only when the tab is first shown.
        Tabs.SelectionChanged += async (_, e) =>
        {
            if (e.Source != Tabs || Tabs.SelectedItem != ModelerTab) return;
            try { await EnsureModelerAsync(); }
            catch (Exception ex) { ModelerFailed(L.T("Starting the modeler"), ex); }
        };
    }

    private async Task EnsureModelerAsync()
    {
        if (_modeler != null) return;
        _modeler = new ModelerWebView(ModelerView);
        _modeler.SetTheme(ThemePalette.Current(this).Dark);
        _modeler.Applied += (xml, uri) => Dispatcher.InvokeAsync(() => ApplyModelAsync(xml, uri));
        _modeler.SaveRequested += (xml, name) => Dispatcher.Invoke(() => SaveModel(xml, name));
        _modeler.DirtyChanged += dirty => Dispatcher.Invoke(() => ModelerTab.Header = dirty ? L.T("Modeler *") : L.T("Modeler"));
        _modeler.Status += (text, warn) => Dispatcher.Invoke(() =>
        {
            ModelerStatus.Text = text;
            if (warn) PluginLog.Warn("Modeler: " + text);
        });
        _modeler.Error += message => Dispatcher.Invoke(() =>
        {
            ModelerStatus.Text = message;
            PluginLog.Error("Modeler: " + message);
        });
        await _modeler.InitAsync();
    }

    /// <summary>Tells what went wrong in the modeler's status line and the log.</summary>
    private void ModelerFailed(string what, Exception ex)
    {
        PluginLog.Error($"{what} failed", ex);
        ModelerStatus.Text = L.T("{0} failed: {1}", what, ex.Message);
    }

    /// <summary>
    /// True when the modeler holds nothing unsaved, or the user agrees to drop
    /// it: opening or starting another model replaces the one shown.
    /// </summary>
    private bool MayReplaceModel()
    {
        if (_modeler?.IsDirty != true) return true;
        return DialogKit.Confirm(Window.GetWindow(this), "\uE7BA", DialogKit.Verify, L.T("Drop the changes in the modeler?"),
            L.T("The model in the modeler has changes that are neither applied to the document nor saved. Opening another model drops them."),
            L.T("Drop the changes"), risky: true);
    }

    /// <summary>The modeler's "Save NodeSet": a file of the user's choice, not a browser download.</summary>
    private void SaveModel(string xml, string name)
    {
        var dialog = new SaveFileDialog
        {
            Title = L.T("Save the NodeSet"),
            Filter = L.T("OPC UA NodeSet (*.xml)|*.xml"),
            FileName = name,
            InitialDirectory = _settings.LastNodeSetFolder ?? "",
        };
        if (dialog.ShowDialog() != true)
        {
            _modeler?.Reply("saved", false, L.T("Not saved."));
            return;
        }
        try
        {
            File.WriteAllText(dialog.FileName, xml);
            PluginLog.Info($"Modeler: saved to {dialog.FileName}.");
            _modeler?.Reply("saved", true, L.T("Saved to {0}.", dialog.FileName));
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Saving {dialog.FileName} failed", ex);
            _modeler?.Reply("saved", false, L.T("Saving failed: {0}", ex.Message));
        }
    }

    private void RefreshModelerNamespaces()
    {
        var rows = NamespaceList.ItemsSource as IEnumerable<NamespaceRow>;
        var uris = rows?.Select(r => r.NamespaceUri).Where(u => u != "http://opcfoundation.org/UA/").ToList() ?? new List<string>();
        var previous = ModelerNamespaceBox.SelectedItem as string;
        ModelerNamespaceBox.ItemsSource = uris;
        ModelerNamespaceBox.SelectedItem = previous != null && uris.Contains(previous) ? previous : uris.FirstOrDefault();
        ModelerEditButton.IsEnabled = uris.Count > 0;
    }

    /// <summary>
    /// Where the modeler finds a namespace's NodeSet and the ones it requires:
    /// the same folders an import searches, so a model fetched from the
    /// OPC Foundation, the Cloud Library or a server opens as well.
    /// </summary>
    private NodeSetCatalog ModelerCatalog(params string[] extraFolders)
    {
        Directory.CreateDirectory(ModelsFolder);
        return NodeSetCatalog.Create(extraFolders.Concat(new[] { ModelsFolder }).Concat(_settings.NodeSetFolders)
            .Concat(OwnNodeSetFolders()).Distinct());
    }

    private async void ModelerEdit_Click(object sender, RoutedEventArgs e)
    {
        if (ModelerNamespaceBox.SelectedItem is not string uri || !MayReplaceModel()) return;
        try
        {
            await EnsureModelerAsync();
            var catalog = ModelerCatalog();
            var info = catalog.Find(uri);
            if (info == null)
            {
                ModelerStatus.Text = L.T("No NodeSet of {0} in the NodeSet folders or the models folder.", uri);
                SetStatus(L.T("The modeler needs the NodeSet file of {0}: add the folder that holds it under Settings › NodeSet folders.", uri));
                return;
            }
            OpenInModeler(info, catalog);
        }
        catch (Exception ex) { ModelerFailed(L.T("Opening {0}", uri), ex); }
    }

    private async void ModelerOpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (!MayReplaceModel()) return;
        try { await OpenFileInModelerAsync(); }
        catch (Exception ex) { ModelerFailed(L.T("Opening the NodeSet"), ex); }
    }

    private async Task OpenFileInModelerAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = L.T("Open an OPC UA NodeSet in the modeler"),
            Filter = L.T("OPC UA NodeSet (*.xml)|*.xml|All files (*.*)|*.*"),
            InitialDirectory = _settings.LastNodeSetFolder ?? "",
        };
        if (dialog.ShowDialog() != true) return;
        await EnsureModelerAsync();
        var info = NodeSetInfo.TryRead(dialog.FileName);
        if (info == null)
        {
            ModelerStatus.Text = L.T("{0} is not a NodeSet2 file.", Path.GetFileName(dialog.FileName));
            return;
        }
        OpenInModeler(info, ModelerCatalog(Path.GetDirectoryName(dialog.FileName)!));
    }

    private void OpenInModeler(NodeSetInfo info, NodeSetCatalog catalog)
    {
        var required = RequiredNodeSets(info, catalog, out var missing);
        if (missing.Count > 0) PluginLog.Warn($"Modeler: no NodeSet for {string.Join(", ", missing)}.");
        _modeler!.Open(Path.GetFileName(info.FilePath), File.ReadAllText(info.FilePath), required);
        ModelerStatus.Text = missing.Count > 0
            ? L.T("Opened {0}; missing: {1}.", Path.GetFileName(info.FilePath), string.Join(", ", missing))
            : L.T("Opened {0}.", Path.GetFileName(info.FilePath));
    }

    /// <summary>The NodeSets a model requires, transitively, except what the modeler ships.</summary>
    private static List<string> RequiredNodeSets(NodeSetInfo root, NodeSetCatalog catalog, out List<string> missing)
    {
        var result = new List<string>();
        missing = new List<string>();
        var seen = new HashSet<string>(root.Models.Select(m => m.Model.ModelUri), StringComparer.Ordinal);
        var queue = new Queue<NodeSetInfo>(new[] { root });
        while (queue.Count > 0)
        {
            foreach (var req in queue.Dequeue().Models.SelectMany(m => m.RequiredModels))
            {
                if (!seen.Add(req.ModelUri) || BundledInModeler.Contains(req.ModelUri)) continue;
                var provider = catalog.Find(req.ModelUri);
                if (provider == null) { missing.Add(req.ModelUri); continue; }
                queue.Enqueue(provider);
                result.Add(File.ReadAllText(provider.FilePath));
            }
        }
        return result;
    }

    private async void ModelerNew_Click(object sender, RoutedEventArgs e)
    {
        var uri = ModelerNewUriBox.Text.Trim();
        if (!Uri.TryCreate(uri, UriKind.Absolute, out _))
        {
            ModelerStatus.Text = L.T("'{0}' is not an absolute URI.", uri);
            return;
        }
        if (!MayReplaceModel()) return;
        try
        {
            await EnsureModelerAsync();
            _modeler!.NewModel(uri, Array.Empty<string>());
            _modelerNewUri = uri;
        }
        catch (Exception ex) { ModelerFailed(L.T("Starting a new model"), ex); }
    }

    private void ModelerReload_Click(object sender, RoutedEventArgs e)
    {
        if (MayReplaceModel()) _modeler?.Reload();
    }

    /// <summary>Keeps the applied NodeSet in the models folder and imports it into the document.</summary>
    private async Task ApplyModelAsync(string xml, string modelUri)
    {
        var name = SafeFileName(modelUri) + ".NodeSet2.xml";
        var file = Path.Combine(ModelsFolder, name);
        try
        {
            Directory.CreateDirectory(ModelsFolder);
            await File.WriteAllTextAsync(file, xml);
        }
        catch (Exception ex)
        {
            ModelerFailed(L.T("Saving {0}", modelUri), ex);
            _modeler?.Reply("applied", false, ModelerStatus.Text);
            return;
        }
        PluginLog.Info($"Modeler: {modelUri} saved to {file}.");

        var document = _document;
        if (document == null || document.CAEXFile.SchemaVersion != LibraryMerger.RequiredSchemaVersion)
        {
            // Kept, but not in a document: the model still counts as changed.
            ModelerStatus.Text = L.T("Saved to {0}. Open a CAEX 3.0 document to import it.", file);
            _modeler?.Reply("applied", false, ModelerStatus.Text);
            return;
        }
        var imported = await ImportFileAsync(document, file, new[] { ModelsFolder },
            conversion => TakeInstances(document, conversion, modelUri));
        ModelerStatus.Text = StatusText.Text;
        _modeler?.Reply("applied", imported, StatusText.Text);
    }

    /// <summary>
    /// Asks where the instances of an applied model go, and takes them there;
    /// null when the model holds none or they stay out.
    /// </summary>
    private string? TakeInstances(CAEXDocument document, ConversionResult conversion, string modelUri)
    {
        var tops = InstanceTransfer.TopInstances(conversion.Document, modelUri);
        if (tops.Count == 0) return null;
        var below = _editorSelection is IInternalElementContainer container and (InstanceHierarchyType or InternalElementType)
            && ReferenceEquals(_editorSelection.CAEXDocument, document)
            ? container : null;
        var hierarchy = InstanceTransfer.HierarchyName(modelUri);
        var window = new InstancesWindow(tops.Select(t => t.Name).ToList(), hierarchy, (below as CAEXObject)?.Name)
        {
            Owner = Window.GetWindow(this),
        };
        var choice = window.ShowDialog() == true ? window.Choice : InstancesChoice.None;
        if (choice == InstancesChoice.None)
        {
            PluginLog.Info($"Modeler: the {tops.Count} instance(s) of {modelUri} were not taken.");
            return null;
        }
        InstanceDestination destination = choice == InstancesChoice.BelowSelection && below != null
            ? new InstanceDestination.Below(below)
            : new InstanceDestination.OwnHierarchy(hierarchy);
        var options = new MergeOptions { DocumentFolder = _documentPath != null ? Path.GetDirectoryName(_documentPath) : null };
        var result = InstanceTransfer.Take(document, conversion.Document, modelUri, destination, options);
        PluginLog.Info("Modeler: " + result);
        return result + ".";
    }

    private static string SafeFileName(string uri)
    {
        var s = uri.Replace("https://", "").Replace("http://", "").Replace("urn:", "");
        foreach (var c in Path.GetInvalidFileNameChars().Concat(new[] { '/', ':' })) s = s.Replace(c, '.');
        return s.Trim('.');
    }
}
