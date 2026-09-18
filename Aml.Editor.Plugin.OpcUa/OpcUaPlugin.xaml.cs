// OPC UA plugin for the AutomationML Editor.
//
// Imports OPC UA NodeSets into the open document as AML libraries according to
// OPC 10000-83 Annex A. All logic lives in OpcUaAml.Core; this class only
// connects it to the editor: which document is open, where NodeSets come from,
// and what the user is told.

using System.IO;
using System.Windows;
using Aml.Editor.Plugin.Contracts;
using Aml.Editor.Plugin.OpcUa.Bridge;
using Aml.Editor.Plugin.OpcUa.Diagnostics;
using Aml.Editor.Plugin.WPFBase;
using Aml.Engine.CAEX;
using Microsoft.Win32;
using OpcUaAml.Checks;
using OpcUaAml.Import;
using OpcUaAml.NodeSets;
using OpcUaAml.Types;

namespace Aml.Editor.Plugin.OpcUa;

public partial class OpcUaPlugin : PluginViewBase, INotifyAMLDocumentLoad
{
    private const int MaxLogLines = 1000;

    private CAEXDocument? _document;
    private bool _busy;
    private readonly PluginSettings _settings = PluginSettings.Load();

    /// <summary>A row of the namespace list.</summary>
    public sealed record NamespaceRow(string NamespaceUri, string ModelVersion, string Published, int LibraryCount);

    public OpcUaPlugin()
    {
        InitializeComponent();

        // No dots or slashes: the editor turns DisplayName into a WPF x:Name and
        // an XML element name in its config, and both reject them.
        DisplayName = "AMLOpcUa";
        IsReactive = true;

        PluginLog.Init();
        PluginLog.DebugEnabled = _settings.DebugLogging;
        PluginLog.OnLine += AppendLog;
        var version = typeof(OpcUaPlugin).Assembly.GetName().Version?.ToString(3) ?? "?";
        PluginLog.Info($"AMLOpcUa v{version}. Log file: {PluginLog.FilePath}");
        PluginLog.Info($"Built-in NodeSets: {NodeSetCatalog.BundledFolder}");

        ReplaceToggle.IsChecked = _settings.ReplaceExistingLibraries;
        SaveToggle.IsChecked = _settings.SaveAfterImport;
        DebugToggle.IsChecked = PluginLog.DebugEnabled;
        LogFilePathLabel.Text = PluginLog.FilePath;
        InitServerTab();

        Loaded += (_, __) =>
        {
            if (_document == null)
            {
                // A view created after the editor announced its document never
                // hears about it; look it up instead.
                var found = DocumentDiscoverer.TryFindCurrentDocument();
                if (found != null) Attach(found);
            }
            UpdateState();
        };
    }

    // ── plugin identity ─────────────────────────────────────────────────────

    public override string PackageName => "Aml.Editor.Plugin.OpcUa";

    public override DockPositionEnum InitialDockPosition => DockPositionEnum.DockContent;

    public override bool CanClose => true;

    // ── document lifecycle ──────────────────────────────────────────────────

    /// <summary>
    /// Required by the contract, never raised: the editor answers it by calling
    /// DocumentLoaded again, which would feed itself.
    /// </summary>
#pragma warning disable CS0067
    public event EventHandler<CAEXDocument>? IsDocumentLoaded;
#pragma warning restore CS0067

    public void DocumentLoaded(CAEXDocument document)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => DocumentLoaded(document));
            return;
        }
        if (document == null)
        {
            DocumentUnLoaded();
            return;
        }
        Attach(document);
    }

    public void DocumentUnLoaded()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(DocumentUnLoaded);
            return;
        }
        _document = null;
        UpdateState();
        UpdateServerState();
        PluginLog.Info("Document closed.");
    }

    public void ApplicationClose()
    {
        try { _client?.DisposeAsync().AsTask().Wait(3000); }
        catch { /* shutting down */ }
        try { _host?.DisposeAsync().AsTask().Wait(3000); }
        catch { /* shutting down */ }
        try { PluginLog.Shutdown(); }
        catch { /* shutting down */ }
    }

    private void Attach(CAEXDocument document)
    {
        _document = document;
        PluginLog.Debug($"Document attached: {document.CAEXFile?.FileName} (CAEX {document.CAEXFile?.SchemaVersion}).");
        UpdateState();
    }

    // ── commands ────────────────────────────────────────────────────────────

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var document = _document;
        if (document == null || _busy) return;
        if (document.CAEXFile.SchemaVersion != LibraryMerger.RequiredSchemaVersion)
        {
            SetStatus($"This document uses CAEX {document.CAEXFile.SchemaVersion}; OPC UA libraries need CAEX 3.0.");
            PluginLog.Warn(StatusText.Text);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Import OPC UA NodeSet",
            Filter = "OPC UA NodeSet (*.xml)|*.xml|All files (*.*)|*.*",
            InitialDirectory = _settings.LastNodeSetFolder ?? "",
        };
        if (dialog.ShowDialog() != true) return;

        var file = dialog.FileName;
        _settings.LastNodeSetFolder = Path.GetDirectoryName(file);
        _settings.Save();
        await ImportFileAsync(document, file, Array.Empty<string>());
    }

    /// <summary>
    /// Converts a NodeSet off the UI thread and merges it into the document.
    /// The NodeSet's own folder is searched first: companion specs usually
    /// ship with the NodeSets they depend on.
    /// </summary>
    private async Task ImportFileAsync(CAEXDocument document, string file, IEnumerable<string> extraFolders)
    {
        var folders = new[] { Path.GetDirectoryName(file)! }.Concat(extraFolders).Concat(_settings.NodeSetFolders).Distinct().ToList();
        var options = new MergeOptions { ReplaceGeneratedLibraries = _settings.ReplaceExistingLibraries };

        SetBusy(true, $"Converting {Path.GetFileName(file)} …");
        try
        {
            // The conversion reads whole NodeSets and takes seconds, so it runs
            // off the UI thread. It does not touch the open document; only the
            // merge below does, back on the UI thread.
            var (conversion, catalog) = await Task.Run(() =>
            {
                var cat = NodeSetCatalog.Create(folders);
                return (NodeSetImporter.Convert(file, cat), cat);
            });
            foreach (var note in catalog.Warnings) PluginLog.Debug(note);

            var merge = LibraryMerger.Merge(document, conversion.Document, options);
            var result = new ImportResult(conversion, merge);

            foreach (var c in merge.Changes)
            {
                var line = $"{c.Action,-8} {c.Name}" + (c.Note != null ? $"  ({c.Note})" : "");
                if (c.Note != null && c.Action == LibraryAction.Kept) PluginLog.Warn(line);
                else PluginLog.Debug(line);
            }
            foreach (var w in result.Warnings) PluginLog.Warn(w);
            PluginLog.Info(result.Summary);

            var saved = _settings.SaveAfterImport && EditorSaver.TrySaveActiveDocument();
            SetStatus(result.Summary + (saved ? " Saved." : " Press Ctrl+S to save."));
        }
        catch (ImportException ex)
        {
            PluginLog.Error(ex.Message);
            SetStatus("Import failed: " + ex.Message);
        }
        catch (Exception ex)
        {
            PluginLog.Error("Import failed", ex);
            SetStatus("Import failed: " + ex.Message);
        }
        finally
        {
            SetBusy(false, null);
            UpdateState();
        }
    }

    private async void CloudButton_Click(object sender, RoutedEventArgs e)
    {
        var document = _document;
        if (document == null || _busy) return;
        if (document.CAEXFile.SchemaVersion != LibraryMerger.RequiredSchemaVersion)
        {
            SetStatus($"This document uses CAEX {document.CAEXFile.SchemaVersion}; OPC UA libraries need CAEX 3.0.");
            return;
        }
        var window = new CloudLibraryWindow(_settings.CloudLibraryUser) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true || window.Selected == null) return;
        _settings.CloudLibraryUser = window.UserName;
        _settings.Save();

        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AMLOpcUa", "cloudlibrary");
        SetBusy(true, $"Downloading {window.Selected.NamespaceUri} …");
        IReadOnlyList<string> files;
        try
        {
            var catalog = NodeSetCatalog.Create(new[] { cache }.Concat(_settings.NodeSetFolders));
            files = await window.CreateClient().DownloadWithDependenciesAsync(window.Selected.Identifier, cache, catalog);
            foreach (var f in files) PluginLog.Info("Downloaded " + f);
        }
        catch (CloudLibraryException ex)
        {
            PluginLog.Error(ex.Message);
            SetStatus(ex.Message);
            return;
        }
        finally
        {
            SetBusy(false, null);
        }
        await ImportFileAsync(document, files[0], new[] { cache });
    }

    private void InstanceButton_Click(object sender, RoutedEventArgs e)
    {
        var document = _document;
        if (document == null || _busy) return;

        var window = new InstanceWindow(document) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true || window.SelectedType == null) return;

        try
        {
            var chosen = window.ChosenOptional;
            var result = TypeInstantiator.Instantiate(window.SelectedType, window.InstanceName, new InstantiationOptions
            {
                IncludeOptional = chosen.Contains,
                AllowAbstract = true,
            });
            var ih = document.CAEXFile.InstanceHierarchy[window.HierarchyName]
                     ?? document.CAEXFile.InstanceHierarchy.Append(window.HierarchyName);
            ih.InternalElement.Insert(result.Instance, asFirst: false);

            var message = $"Created '{result.Instance.Name}' ({window.SelectedType.Name}) in '{ih.Name}' with {result.Included.Count} children.";
            PluginLog.Info(message);
            if (result.OmittedPlaceholders.Count > 0)
                PluginLog.Info("Placeholders to fill by hand: " + string.Join(", ", result.OmittedPlaceholders));
            if (UaTypes.IsAbstract(window.SelectedType))
                PluginLog.Warn($"'{window.SelectedType.Name}' is abstract; OPC UA only instantiates concrete subtypes.");

            var saved = _settings.SaveAfterImport && EditorSaver.TrySaveActiveDocument();
            SetStatus(message + (saved ? " Saved." : " Press Ctrl+S to save."));
        }
        catch (InstantiationException ex)
        {
            PluginLog.Error(ex.Message);
            SetStatus(ex.Message);
        }
        catch (Exception ex)
        {
            PluginLog.Error("Creating the instance failed", ex);
            SetStatus("Creating the instance failed: " + ex.Message);
        }
    }

    private void CheckButton_Click(object sender, RoutedEventArgs e)
    {
        var document = _document;
        if (document == null) return;
        try
        {
            var findings = AnnexAChecker.Check(document);
            FindingList.ItemsSource = findings;
            var errors = findings.Count(f => f.Severity == Severity.Error);
            CheckSummary.Text = findings.Count == 0
                ? $"No findings in {document.CAEXFile.InstanceHierarchy.Count} instance hierarch{(document.CAEXFile.InstanceHierarchy.Count == 1 ? "y" : "ies")}."
                : $"{errors} error(s), {findings.Count - errors} warning(s). Rules: "
                  + string.Join("; ", findings.Select(f => f.Rule).Distinct().OrderBy(r => r).Select(r => $"{r} {Rules.Descriptions[r]}"));
            Tabs.SelectedItem = CheckTab;
            PluginLog.Info($"Check: {errors} error(s), {findings.Count - errors} warning(s).");
            SetStatus($"Check: {errors} error(s), {findings.Count - errors} warning(s).");
        }
        catch (Exception ex)
        {
            PluginLog.Error("Check failed", ex);
            SetStatus("Check failed: " + ex.Message);
        }
    }

    private void UpgradeButton_Click(object sender, RoutedEventArgs e)
    {
        var document = _document;
        if (document == null) return;
        var changes = InstanceUpgrader.UpgradeDocument(document);
        foreach (var c in changes) PluginLog.Info($"Added '{c.Added}' to {c.ElementPath}.");
        var message = changes.Count == 0
            ? "All instances already have the Mandatory children of their types."
            : $"Added {changes.Count} Mandatory child(ren) to {changes.Select(c => c.ElementPath).Distinct().Count()} instance(s). Press Ctrl+S to save.";
        SetStatus(message);
        CheckButton_Click(sender, e);
        CheckSummary.Text = message + " " + CheckSummary.Text;
    }

    private void LinkButton_Click(object sender, RoutedEventArgs e)
    {
        var document = _document;
        if (document == null) return;
        var window = new LinkWindow(document) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
        if (window.Linked != null)
        {
            PluginLog.Info($"Linked '{window.Linked.Name}' (VDI 3682) to OPC UA.");
            SetStatus("VDI 3682 link set. Press Ctrl+S to save.");
            Selected?.Invoke(this, new SelectionEventArgs(window.Linked));
        }
    }

    private void FoldersButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new FolderListWindow(_settings.NodeSetFolders) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true) return;

        _settings.NodeSetFolders = window.Folders.ToList();
        _settings.Save();
        PluginLog.Info(_settings.NodeSetFolders.Count == 0
            ? "No NodeSet folders; only the built-in UA base model and DI are available."
            : "NodeSet folders: " + string.Join("; ", _settings.NodeSetFolders));
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        _settings.ReplaceExistingLibraries = ReplaceToggle.IsChecked == true;
        _settings.SaveAfterImport = SaveToggle.IsChecked == true;
        _settings.Save();
    }

    private void DebugToggle_Changed(object sender, RoutedEventArgs e)
    {
        PluginLog.DebugEnabled = DebugToggle.IsChecked == true;
        _settings.DebugLogging = PluginLog.DebugEnabled;
        _settings.Save();
    }

    // ── view state ──────────────────────────────────────────────────────────

    private void UpdateState()
    {
        var doc = _document;
        var usable = doc != null && doc.CAEXFile.SchemaVersion == LibraryMerger.RequiredSchemaVersion;

        ImportButton.IsEnabled = usable && !_busy;
        CloudButton.IsEnabled = usable && !_busy;
        InstanceButton.IsEnabled = usable && !_busy;
        CheckButton.IsEnabled = doc != null && !_busy;
        LinkButton.IsEnabled = usable && !_busy;
        Placeholder.Visibility = usable ? Visibility.Collapsed : Visibility.Visible;
        Placeholder.Text = doc == null
            ? "Open a CAEX 3.0 document to import OPC UA NodeSets."
            : $"This document uses CAEX {doc.CAEXFile.SchemaVersion}. The OPC UA libraries of OPC 10000-83 Annex A need CAEX 3.0 (AutomationML 2.10).";

        RefreshDiagramSources();
        NamespaceList.ItemsSource = usable
            ? NamespaceOverview.Of(doc!).Select(n => new NamespaceRow(
                n.NamespaceUri, n.ModelVersion ?? "", n.PublicationDate?.ToString("yyyy-MM-dd") ?? "", n.Libraries.Count)).ToList()
            : null;
    }

    private void SetBusy(bool busy, string? status)
    {
        _busy = busy;
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ImportButton.IsEnabled = !busy && _document != null;
        CloudButton.IsEnabled = !busy && _document != null;
        InstanceButton.IsEnabled = !busy && _document != null;
        CheckButton.IsEnabled = !busy && _document != null;
        FoldersButton.IsEnabled = !busy;
        if (status != null) SetStatus(status);
        UpdateServerState();
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private void AppendLog(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AppendLog(line));
            return;
        }
        LogBox.AppendText(line + Environment.NewLine);
        if (LogBox.LineCount > MaxLogLines)
        {
            var cut = LogBox.GetCharacterIndexFromLineIndex(LogBox.LineCount - MaxLogLines);
            LogBox.Text = LogBox.Text[cut..];
        }
        LogBox.ScrollToEnd();
    }
}
