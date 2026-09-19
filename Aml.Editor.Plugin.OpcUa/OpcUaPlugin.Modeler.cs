// The Modeler tab: the graphical modeler (InfoModel.js) in a WebView2 control.
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
            if (e.Source == Tabs && Tabs.SelectedItem == ModelerTab) await EnsureModelerAsync();
        };
    }

    private async Task EnsureModelerAsync()
    {
        if (_modeler != null) return;
        _modeler = new ModelerWebView(ModelerView);
        _modeler.SetTheme(ThemePalette.Current(this).Dark);
        _modeler.Applied += (xml, uri) => Dispatcher.InvokeAsync(() => ApplyModelAsync(xml, uri));
        _modeler.DirtyChanged += dirty => Dispatcher.Invoke(() => ModelerTab.Header = dirty ? "Modeler *" : "Modeler");
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

    private void RefreshModelerNamespaces()
    {
        var rows = NamespaceList.ItemsSource as IEnumerable<NamespaceRow>;
        var uris = rows?.Select(r => r.NamespaceUri).Where(u => u != "http://opcfoundation.org/UA/").ToList() ?? new List<string>();
        var previous = ModelerNamespaceBox.SelectedItem as string;
        ModelerNamespaceBox.ItemsSource = uris;
        ModelerNamespaceBox.SelectedItem = previous != null && uris.Contains(previous) ? previous : uris.FirstOrDefault();
        ModelerEditButton.IsEnabled = uris.Count > 0;
    }

    private NodeSetCatalog ModelerCatalog(params string[] extraFolders)
    {
        Directory.CreateDirectory(ModelsFolder);
        return NodeSetCatalog.Create(extraFolders.Concat(new[] { ModelsFolder }).Concat(_settings.NodeSetFolders).Distinct());
    }

    private async void ModelerEdit_Click(object sender, RoutedEventArgs e)
    {
        if (ModelerNamespaceBox.SelectedItem is not string uri) return;
        await EnsureModelerAsync();
        var catalog = ModelerCatalog();
        var info = catalog.Find(uri);
        if (info == null)
        {
            ModelerStatus.Text = $"No NodeSet of {uri} in the NodeSet folders or the models folder. Add its folder under 'NodeSet folders'.";
            return;
        }
        OpenInModeler(info, catalog);
    }

    private async void ModelerOpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open an OPC UA NodeSet in the modeler",
            Filter = "OPC UA NodeSet (*.xml)|*.xml|All files (*.*)|*.*",
            InitialDirectory = _settings.LastNodeSetFolder ?? "",
        };
        if (dialog.ShowDialog() != true) return;
        await EnsureModelerAsync();
        var info = NodeSetInfo.TryRead(dialog.FileName);
        if (info == null)
        {
            ModelerStatus.Text = $"{Path.GetFileName(dialog.FileName)} is not a NodeSet2 file.";
            return;
        }
        OpenInModeler(info, ModelerCatalog(Path.GetDirectoryName(dialog.FileName)!));
    }

    private void OpenInModeler(NodeSetInfo info, NodeSetCatalog catalog)
    {
        var required = RequiredNodeSets(info, catalog, out var missing);
        if (missing.Count > 0) PluginLog.Warn($"Modeler: no NodeSet for {string.Join(", ", missing)}.");
        _modeler!.Open(Path.GetFileName(info.FilePath), File.ReadAllText(info.FilePath), required);
        ModelerStatus.Text = $"Opened {Path.GetFileName(info.FilePath)}" + (missing.Count > 0 ? $"; missing: {string.Join(", ", missing)}." : ".");
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
            ModelerStatus.Text = $"'{uri}' is not an absolute URI.";
            return;
        }
        await EnsureModelerAsync();
        _modeler!.NewModel(uri, Array.Empty<string>());
    }

    private void ModelerReload_Click(object sender, RoutedEventArgs e) => _modeler?.Reload();

    /// <summary>Keeps the applied NodeSet in the models folder and imports it into the document.</summary>
    private async Task ApplyModelAsync(string xml, string modelUri)
    {
        Directory.CreateDirectory(ModelsFolder);
        var name = SafeFileName(modelUri) + ".NodeSet2.xml";
        var file = Path.Combine(ModelsFolder, name);
        await File.WriteAllTextAsync(file, xml);
        PluginLog.Info($"Modeler: {modelUri} saved to {file}.");

        var document = _document;
        if (document == null || document.CAEXFile.SchemaVersion != LibraryMerger.RequiredSchemaVersion)
        {
            ModelerStatus.Text = $"Saved to {file}. Open a CAEX 3.0 document to import it.";
            return;
        }
        await ImportFileAsync(document, file, new[] { ModelsFolder });
        ModelerStatus.Text = StatusText.Text;
    }

    private static string SafeFileName(string uri)
    {
        var s = uri.Replace("https://", "").Replace("http://", "").Replace("urn:", "");
        foreach (var c in Path.GetInvalidFileNameChars().Concat(new[] { '/', ':' })) s = s.Replace(c, '.');
        return s.Trim('.');
    }
}
