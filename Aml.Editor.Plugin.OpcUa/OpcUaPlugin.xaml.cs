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
using Aml.Editor.Plugin.OpcUa.Guide;
using Aml.Editor.Plugin.OpcUa.Localization;
using Aml.Editor.Plugin.WPFBase;
using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
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
    private bool _importing;
    private readonly PluginSettings _settings;

    /// <summary>A row of the namespace list.</summary>
    public sealed record NamespaceRow(string NamespaceUri, string ModelVersion, string Published, int LibraryCount);

    public OpcUaPlugin()
    {
        // The log first, so that a problem reading the settings is written down.
        PluginLog.Init();
        _settings = PluginSettings.Load(out var settingsProblem);
        InitializeComponent();
        // The editor's theme is known once the view sits in its window, and may change while it is shown.
        Loaded += (_, __) => ApplyTheme();
        IsVisibleChanged += (_, __) => { if (IsVisible) ApplyTheme(); };
        FollowThemeChanges();

        // No dots or slashes: the editor turns DisplayName into a WPF x:Name and
        // an XML element name in its config, and both reject them.
        DisplayName = "AMLOpcUa";
        IsReactive = true;

        PluginLog.DebugEnabled = _settings.DebugLogging;
        PluginLog.OnLine += AppendLog;
        var version = typeof(OpcUaPlugin).Assembly.GetName().Version?.ToString(3) ?? "?";
        PluginLog.Info($"AMLOpcUa v{version}. Log file: {PluginLog.FilePath}");
        PluginLog.Info($"Built-in NodeSets: {NodeSetCatalog.BundledFolder}");
        if (settingsProblem != null) PluginLog.Warn(settingsProblem);
        PluginErrors.Install(Dispatcher, ex =>
        {
            PluginLog.Error("Unexpected error", ex);
            SetStatus(L.T("Something went wrong: {0} The log has the details.", ex.Message));
        });

        // Setting a toggle raises Setting_Changed, which saves every toggle:
        // with the first one set and the second not yet, "Save after import"
        // was saved as off on every start.
        _applyingSettings = true;
        ReplaceToggle.IsChecked = _settings.ReplaceExistingLibraries;
        SaveToggle.IsChecked = _settings.SaveAfterImport;
        _applyingSettings = false;
        DebugToggle.IsChecked = PluginLog.DebugEnabled;
        LogFilePathLabel.Text = PluginLog.FilePath;
        InitServerTab();
        InitModelerTab();
        InitNumberFields();
        InitTutorial();
        AccessibleNames.Give(this);
        // Closed by the user, the view is gone; what it holds must not stay behind (the port above all).
        PluginTerminated += (_, _) => ReleaseResources();
        Tabs.SelectionChanged += (_, e) =>
        {
            if (e.Source != Tabs || Tabs.SelectedIndex != 0) return;
            _unseenProblems = 0;
            ShowLogLink();
        };

        Loaded += (_, __) =>
        {
            ProbeEditorBridge();
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

    private void ApplyTheme()
    {
        var palette = ThemePalette.Current(this);
        palette.ApplyTo(this);
        _modeler?.SetTheme(palette.Dark);
        UpdateServerState();
    }

    private EventHandler<ControlzEx.Theming.ThemeChangedEventArgs>? _themeChanged;

    /// <summary>
    /// The editor switches themes through MahApps' theme manager (ControlzEx);
    /// its event says when. Without ControlzEx (a host without MahApps) the
    /// theme is read again whenever the plugin becomes visible.
    /// </summary>
    private void FollowThemeChanges()
    {
        try { Subscribe(); }
        catch (Exception ex) when (ex is System.IO.FileNotFoundException or System.IO.FileLoadException or TypeLoadException)
        {
            PluginLog.Debug("No theme manager; the theme is read when the plugin is shown.");
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        void Subscribe()
        {
            _themeChanged = (_, __) => Dispatcher.BeginInvoke(ApplyTheme);
            ControlzEx.Theming.ThemeManager.Current.ThemeChanged += _themeChanged;
        }
    }

    private bool _bridgeProbed;

    /// <summary>The user's choice, when this editor lets the plugin save.</summary>
    private bool SaveAfterImport => _settings.SaveAfterImport && SaveToggle.IsEnabled;

    /// <summary>
    /// Saving and finding the open document go through the editor's internals
    /// by reflection, which a new editor version may break. Checked once: an
    /// unusable "Save after import" is switched off with the reason, instead of
    /// promising a save that never happens.
    /// </summary>
    private void ProbeEditorBridge()
    {
        if (_bridgeProbed) return;
        _bridgeProbed = true;
        var save = EditorSaver.FindSaveCommand();
        if (save != null)
        {
            PluginLog.Debug($"Editor save command: {save}.");
            return;
        }
        // Outside the editor (the WPF probe, tests) there is no main view-model to ask.
        if (Application.Current?.MainWindow?.DataContext == null) return;
        PluginLog.Warn("This editor version has no save command the plugin knows; \"Save after import\" is off. Save with Ctrl+S.");
        // The setting itself stays as the user chose it, for an editor where saving works.
        SaveToggle.IsEnabled = false;
        SaveToggle.ToolTip = L.T("Not available with this editor version: its save command was not found. Save with Ctrl+S.");
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

    /// <summary>The file of the open document, as the editor reports it.</summary>
    private string? _documentPath;

    /// <summary>What is selected in the editor's trees, for commands that put something below it.</summary>
    private CAEXBasicObject? _editorSelection;

    public override void ChangeSelectedObject(CAEXBasicObject selectedObject)
    {
        base.ChangeSelectedObject(selectedObject);
        _editorSelection = selectedObject;
    }

    public override void ChangeAMLFilePath(string amlFilePath)
    {
        base.ChangeAMLFilePath(amlFilePath);
        _documentPath = string.IsNullOrEmpty(amlFilePath) ? null : amlFilePath;
    }

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
        Watch(null);
        _document = null;
        _ = StopLiveAsync();
        // A closed document is not served on.
        _ = StopServingAsync(L.T("the document was closed"));
        UpdateState();
        UpdateServerState();
        PluginLog.Info("Document closed.");
    }

    public void ApplicationClose()
    {
        ReleaseResources();
        try { PluginLog.Shutdown(); }
        catch { /* shutting down */ }
    }

    /// <summary>
    /// The XML whose changes the plugin follows. Kept here, since the editor
    /// unloads a document before it reports it closed, and an unloaded
    /// document no longer names its XML.
    /// </summary>
    private System.Xml.Linq.XDocument? _watched;

    /// <summary>Follows the document's changes, for the indexes the Server tab keeps of it; null stops.</summary>
    private void Watch(CAEXDocument? document)
    {
        if (_watched != null) _watched.Changed -= DocumentChanged;
        _watched = document?.CAEXFile?.Node?.Document;
        if (_watched != null) _watched.Changed += DocumentChanged;
        DocumentChanged(null, System.Xml.Linq.XObjectChangeEventArgs.Value);
    }

    private bool _released;

    /// <summary>What a step after the merge did: a sentence for the status, and whether it took the model's instances.</summary>
    internal sealed record AfterMerge(string Text, bool InstancesTaken = false);

    /// <summary>Cancelled when the plugin ends, which stops a conversion still running.</summary>
    private readonly CancellationTokenSource _closing = new();

    /// <summary>
    /// Ends what outlives the view otherwise: the session with its
    /// subscriptions, the document server and its port, the modeler, the log
    /// listener. When the editor ends, and when the plugin is closed.
    /// </summary>
    private void ReleaseResources()
    {
        if (_released) return;
        _released = true;
        try { _closing.Cancel(); }
        catch (ObjectDisposedException) { /* shutting down */ }
        PluginLog.OnLine -= AppendLog;
        HelpButton.Opened -= OnHelpOpened;
        HelpButton.LessonRequested -= OnLessonRequested;
        _tourTimer?.Stop();
        try { if (_themeChanged != null) ControlzEx.Theming.ThemeManager.Current.ThemeChanged -= _themeChanged; }
        catch { /* shutting down */ }
        var live = _live;
        _live = null;
        try { live?.DisposeAsync().AsTask().Wait(2000); }
        catch { /* shutting down */ }
        try { _watch?.DisposeAsync().AsTask().Wait(2000); }
        catch { /* shutting down */ }
        try { _client?.DisposeAsync().AsTask().Wait(3000); }
        catch { /* shutting down */ }
        _client = null;
        try { _hostFollow?.Dispose(); _host?.DisposeAsync().AsTask().Wait(3000); }
        catch { /* shutting down */ }
        _host = null;
        try { _plant?.DisposeAsync().AsTask().Wait(3000); }
        catch { /* shutting down */ }
        _plant = null;
        try { _modeler?.Dispose(); }
        catch { /* shutting down */ }
    }

    private void Attach(CAEXDocument document)
    {
        if (!ReferenceEquals(_document, document))
        {
            _ = StopLiveAsync();
            _ = StopServingAsync(L.T("another document was opened"));
            Watch(document);
        }
        _document = document;
        PluginLog.Debug($"Document attached: {document.CAEXFile?.FileName} (CAEX {document.CAEXFile?.SchemaVersion}).");
        UpdateState();
        // Serve, Take and the value commands depend on the document too.
        UpdateServerState();
    }

    // ── commands ────────────────────────────────────────────────────────────

    private async void ImportButton_Click(object sender, RoutedEventArgs e) => await ImportFromFolderAsync(_settings.LastNodeSetFolder);

    /// <summary>Asks for a NodeSet, starting in <paramref name="folder"/>, and imports it.</summary>
    private async Task ImportFromFolderAsync(string? folder)
    {
        var document = _document;
        if (document == null || _busy) return;
        if (document.CAEXFile.SchemaVersion != LibraryMerger.RequiredSchemaVersion)
        {
            SetStatus(L.T("This document uses CAEX {0}; OPC UA libraries need CAEX 3.0.", document.CAEXFile.SchemaVersion));
            PluginLog.Warn(StatusText.Text);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = L.T("Import OPC UA NodeSet or ModelDesign"),
            Filter = L.T("OPC UA NodeSet or ModelDesign (*.xml)|*.xml|All files (*.*)|*.*"),
            InitialDirectory = folder ?? "",
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
    /// <summary>True when the NodeSet arrived in the document.</summary>
    /// <param name="afterMerge">Runs on the UI thread after the libraries are in and before the document is saved; what it returns is added to the status.</param>
    private async Task<bool> ImportFileAsync(CAEXDocument document, string file, IEnumerable<string> extraFolders,
        Func<ConversionResult, AfterMerge?>? afterMerge = null)
    {
        // One import at a time: each merges into the document and clears the busy state when done.
        if (_importing)
        {
            SetStatus(L.T("An import is running; import {0} when it is done.", Path.GetFileName(file)));
            return false;
        }
        _importing = true;
        var compiled = (string?)null;
        try
        {
            // A ModelDesign is no NodeSet: the ModelCompiler makes one of it first.
            if (IsModelDesign(file))
            {
                compiled = await CompileDesignAsync(file);
                if (compiled == null) return false;
                file = compiled;
            }
            return await ImportOneAsync(document, file, extraFolders, afterMerge);
        }
        finally
        {
            _importing = false;
            // The whole folder the compiler wrote into, not just the one the NodeSet sits in.
            if (compiled != null && _designFolder != null) { DeleteFolder(_designFolder); _designFolder = null; }
        }
    }

    private async Task<bool> ImportOneAsync(CAEXDocument document, string file, IEnumerable<string> extraFolders,
        Func<ConversionResult, AfterMerge?>? afterMerge)
    {
        List<string> folders;
        // Missing models are asked about before the conversion, which takes seconds;
        // after the user fixed them, the check runs again.
        while (true)
        {
            try
            {
                // Besides the given folders, the models the plugin keeps itself: from the
                // modeler, the Cloud Library, the OPC Foundation and servers.
                folders = new[] { Path.GetDirectoryName(file)! }.Concat(extraFolders).Concat(_settings.NodeSetFolders)
                    .Concat(OwnNodeSetFolders()).Distinct().ToList();
                var info = NodeSetInfo.TryRead(file);
                if (info == null) break;
                var missing = await Task.Run(() => NodeSetCatalog.Create(folders).MissingDependencies(info));
                if (missing.Count == 0) break;
                PluginLog.Warn($"{Path.GetFileName(file)} requires models that are not available: {string.Join(", ", missing.Select(m => m.ModelUri))}.");
                if (await ResolveMissingAsync(file, missing)) continue;
                SetStatus(L.T("Import of {0} cancelled: required models are missing.", Path.GetFileName(file)));
                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PluginLog.Error($"Reading {file} failed", ex);
                SetStatus(L.T("{0} cannot be read: {1}", Path.GetFileName(file), ex.Message));
                return false;
            }
        }
        var options = new MergeOptions
        {
            ReplaceGeneratedLibraries = _settings.ReplaceExistingLibraries,
            // Where the document's ExternalReferences lie, so a base library it references is not added twice.
            DocumentFolder = _documentPath != null ? Path.GetDirectoryName(_documentPath) : null,
        };

        // Logged before the work starts, so an import that never ends still leaves a trace.
        PluginLog.Info($"Importing {file}.");
        SetBusy(true, L.T("Converting {0} … The first conversion of a NodeSet takes up to a minute; later ones come from the cache.", Path.GetFileName(file)));
        try
        {
            // The conversion runs in a process of its own (ConverterProcess):
            // inside the editor, Aml.Engine's process wide services made it
            // take minutes. Only the merge below touches the open document.
            var (conversion, notes) = await ConverterProcess.ConvertAsync(file, folders, token: _closing.Token);
            foreach (var note in notes) PluginLog.Debug(note);

            // The user may have closed the document, or opened another, meanwhile.
            if (!ReferenceEquals(_document, document))
            {
                PluginLog.Warn($"{Path.GetFileName(file)} was converted after its document was closed; nothing was imported.");
                SetStatus(L.T("The document changed while {0} was converted; import it again into the open one.", Path.GetFileName(file)));
                return false;
            }
            // Each phase is timed and logged: inside the editor an import has
            // taken minutes that the command line needs seconds for (PF-AML-19).
            var clock = System.Diagnostics.Stopwatch.StartNew();
            // The same report as the command line's: what Annex A leaves behind included.
            var result = OpcUaImport.MergeConverted(document, conversion, file, options);
            var merge = result.Merge;
            var merged = clock.Elapsed;
            // The editor updates its trees and lookups after the change; waiting
            // until it is idle measures how long that takes.
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var shown = clock.Elapsed - merged;

            foreach (var c in merge.Changes)
            {
                var line = $"{c.Action,-8} {c.Name}" + (c.Note != null ? $"  ({c.Note})" : "");
                if (c.Note != null && c.Action == LibraryAction.Kept) PluginLog.Warn(line);
                else PluginLog.Debug(line);
            }
            foreach (var w in result.Warnings) PluginLog.Warn(w);
            var more = afterMerge?.Invoke(conversion);
            // Instances taken after the merge (the modeler's Apply) are no longer "not taken".
            if (more?.InstancesTaken == true) result = result with { InstancesNotTaken = 0 };
            PluginLog.Info(result.Summary);

            var before = clock.Elapsed;
            var saved = SaveAfterImport && EditorSaver.TrySaveActiveDocument();
            PluginLog.Info($"Import took: conversion {conversion.Duration.TotalSeconds:F1} s{(conversion.FromCache ? " (cache)" : "")}, "
                + $"merge {merged.TotalSeconds:F1} s, editor updating {shown.TotalSeconds:F1} s"
                + (SaveAfterImport ? $", saving {(clock.Elapsed - before).TotalSeconds:F1} s" : ""));
            SetStatus(Summaries.Import(result) + (more != null ? " " + more.Text : "") + (saved ? L.T(" Saved.") : L.T(" Press Ctrl+S to save.")));
            return true;
        }
        catch (OperationCanceledException)
        {
            // The plugin is ending; the converter was stopped with it.
            return false;
        }
        catch (ImportException ex)
        {
            PluginLog.Error(ex.Message);
            SetStatus(L.T("Import failed: {0}", ex.Message));
            return false;
        }
        catch (Exception ex)
        {
            PluginLog.Error("Import failed", ex);
            SetStatus(L.T("Import failed: {0}", ex.Message));
            return false;
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
            SetStatus(L.T("This document uses CAEX {0}; OPC UA libraries need CAEX 3.0.", document.CAEXFile.SchemaVersion));
            return;
        }
        var files = await DownloadFromCloudAsync("");
        if (files == null || files.Count == 0) return;
        await ImportFileAsync(document, files[0], new[] { CloudCache });
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
                FillPlaceholder = p => window.Fills.TryGetValue(p, out var fills) ? fills : Array.Empty<PlaceholderFill>(),
                AllowAbstract = true,
            });
            var ih = document.CAEXFile.InstanceHierarchy[window.HierarchyName]
                     ?? document.CAEXFile.InstanceHierarchy.Append(window.HierarchyName);
            ih.InternalElement.Insert(result.Instance, asFirst: false);
            var created = ih.InternalElement.Last();

            var message = L.T("Created '{0}' ({1}) in '{2}' with {3} children.", result.Instance.Name, window.SelectedType.Name, ih.Name, result.Included.Count);
            PluginLog.Info(message);
            if (result.Filled.Count > 0)
                PluginLog.Info("Created for placeholders: " + string.Join(", ", result.Filled));
            if (result.OmittedPlaceholders.Count > 0)
                PluginLog.Info("Placeholders left empty: " + string.Join(", ", result.OmittedPlaceholders));
            if (UaTypes.IsAbstract(window.SelectedType))
                PluginLog.Warn($"'{window.SelectedType.Name}' is abstract; OPC UA only instantiates concrete subtypes.");

            var saved = SaveAfterImport && EditorSaver.TrySaveActiveDocument();
            SetStatus(message + (saved ? L.T(" Saved.") : L.T(" Press Ctrl+S to save.")));
            Selected?.Invoke(this, new SelectionEventArgs(created));
        }
        catch (InstantiationException ex)
        {
            PluginLog.Error(ex.Message);
            SetStatus(ex.Message);
        }
        catch (Exception ex)
        {
            PluginLog.Error("Creating the instance failed", ex);
            SetStatus(L.T("Creating the instance failed: {0}", ex.Message));
        }
    }

    private void CheckButton_Click(object sender, RoutedEventArgs e)
    {
        var document = _document;
        if (document == null) return;
        _checkRuns++;
        try
        {
            var findings = AnnexAChecker.Check(document);
            FindingList.ItemsSource = findings;
            var errors = findings.Count(f => f.Severity == Severity.Error);
            CheckTab.Header = findings.Count == 0 ? L.T("Check") : L.T("Check ({0})", findings.Count);
            CheckSummary.Text = findings.Count == 0
                ? L.T("No findings in {0} instance hierarchy(ies).", document.CAEXFile.InstanceHierarchy.Count)
                : L.T("{0} error(s), {1} warning(s). Rules: ", errors, findings.Count - errors)
                  + string.Join("; ", findings.Select(f => f.Rule).Distinct().OrderBy(r => r).Select(r => $"{r} {L.T(Rules.Descriptions[r])}"));
            Tabs.SelectedItem = CheckTab;
            PluginLog.Info($"Check: {errors} error(s), {findings.Count - errors} warning(s).");
            SetStatus(L.T("Check: {0} error(s), {1} warning(s).", errors, findings.Count - errors));
        }
        catch (Exception ex)
        {
            PluginLog.Error("Check failed", ex);
            SetStatus(L.T("Check failed: {0}", ex.Message));
        }
    }

    private void UpgradeButton_Click(object sender, RoutedEventArgs e) => Guard(L.T("Adding the Mandatory children"), () => Upgrade(sender, e));

    private void Upgrade(object sender, RoutedEventArgs e)
    {
        var document = _document;
        if (document == null) return;
        // Counted first; the user sees what is added before anything is.
        var planned = InstanceUpgrader.PreviewDocument(document);
        if (planned.Count > 0)
        {
            var instances = planned.Select(c => c.ElementPath).Distinct().ToList();
            var list = string.Join(Environment.NewLine, planned.Take(15).Select(c => $"{c.ElementPath}  +{c.Added}"))
                       + (planned.Count > 15 ? Environment.NewLine + L.T("… and {0} more", planned.Count - 15) : "");
            if (!DialogKit.Confirm(Window.GetWindow(this), "", DialogKit.Create, L.T("Add {0} Mandatory child(ren)?", planned.Count),
                    L.T("{0} instance(s) lack children their types now declare as Mandatory. They are added as a new instance would get them; nothing that exists changes. Ctrl+Z in the editor does not take them back.", instances.Count),
                    L.T("Add"), DialogKit.Facts((L.T("Adds"), list, false))))
                return;
        }
        var changes = InstanceUpgrader.UpgradeDocument(document);
        foreach (var c in changes) PluginLog.Info($"Added '{c.Added}' to {c.ElementPath}.");
        var message = changes.Count == 0
            ? L.T("All instances already have the Mandatory children of their types.")
            : L.T("Added {0} Mandatory child(ren) to {1} instance(s). Press Ctrl+S to save.", changes.Count, changes.Select(c => c.ElementPath).Distinct().Count());
        SetStatus(message);
        CheckButton_Click(sender, e);
        CheckSummary.Text = message + " " + CheckSummary.Text;
    }

    private void LinkButton_Click(object sender, RoutedEventArgs e) => Guard(L.T("Linking"), () =>
    {
        var document = _document;
        if (document == null) return;
        var window = new LinkWindow(document) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
        if (window.Linked != null)
        {
            PluginLog.Info($"Linked '{window.Linked.Name}' (VDI 3682) to OPC UA.");
            SetStatus(L.T("VDI 3682 link set. Press Ctrl+S to save."));
            Selected?.Invoke(this, new SelectionEventArgs(window.Linked));
        }
    });

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var document = _document;
        if (document == null || _busy) return;
        var choice = new ExportWindow(document.CAEXFile.Node) { Owner = Window.GetWindow(this) };
        if (choice.ShowDialog() != true) return;
        var name = choice.NamespaceUri is { } uri
            ? uri.TrimEnd('/').Split('/', ':').LastOrDefault(s => s.Length > 0)
            : Path.GetFileNameWithoutExtension(document.CAEXFile.FileName ?? "document");
        var dialog = new SaveFileDialog
        {
            Title = L.T("Export as OPC UA NodeSet"),
            Filter = L.T("OPC UA NodeSet (*.xml)|*.xml"),
            FileName = (string.IsNullOrEmpty(name) ? "document" : name) + ".NodeSet2.xml",
        };
        if (dialog.ShowDialog() != true) return;
        var options = new OpcUaAml.Export.NodeSetExportOptions { Mode = choice.Mode, NamespaceUri = choice.NamespaceUri };

        SetBusy(true, L.T("Exporting …"));
        try
        {
            // The exporter reads the document; a copy of its XML keeps the
            // editor's document out of the background thread.
            var xml = new System.Xml.Linq.XDocument(new System.Xml.Linq.XElement(document.CAEXFile.Node));
            var nodeSet = await Task.Run(() => OpcUaAml.Export.NodeSetExporter.Export(xml, options));
            nodeSet.Save(dialog.FileName);
            var nodes = nodeSet.Root?.Elements().Count(x => x.Name.LocalName.StartsWith("UA")) ?? 0;
            PluginLog.Info($"Exported {nodes} node(s) to {dialog.FileName}.");
            SetStatus(L.T("Exported {0} node(s) to {1}.", nodes, Path.GetFileName(dialog.FileName)));
        }
        catch (Exception ex)
        {
            PluginLog.Error("Export failed", ex);
            SetStatus(L.T("Export failed: {0}", ex.Message));
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    // ── first steps and the namespace list ─────────────────────────────────

    private void StartCloud_Click(object sender, RoutedEventArgs e) => OpcfButton_Click(sender, e);

    private void StartServer_Click(object sender, RoutedEventArgs e)
    {
        Tabs.SelectedItem = ServerTab;
        EndpointBox.Focus();
    }

    private void StartModeler_Click(object sender, RoutedEventArgs e) => Tabs.SelectedItem = ModelerTab;

    private void NamespaceEdit_Click(object sender, RoutedEventArgs e)
    {
        if (NamespaceList.SelectedItem is not NamespaceRow row) return;
        Tabs.SelectedItem = ModelerTab;
        ModelerNamespaceBox.SelectedItem = row.NamespaceUri;
        ModelerEdit_Click(sender, e);
    }

    private void NamespaceCopy_Click(object sender, RoutedEventArgs e)
    {
        if (NamespaceList.SelectedItem is NamespaceRow row) CopyText(row.NamespaceUri);
    }

    private void SettingsMenuButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsMenu.PlacementTarget = SettingsMenuButton;
        SettingsMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        SettingsMenu.IsOpen = true;
    }

    private void FindingList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        SelectFinding();
    }

    private void FindingList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => SelectFinding();

    /// <summary>Selects the element of a finding in the editor.</summary>
    private void SelectFinding()
    {
        if (FindingList.SelectedItem is not OpcUaAml.Checks.Finding { ElementId: { } id } || _document == null) return;
        if (_document.FindByID(id, true, null) is CAEXObject element) Selected?.Invoke(this, new SelectionEventArgs(element));
    }

    /// <summary>
    /// A CAEX 2.15 document cannot hold the Annex A libraries, and the editor
    /// offers plugins no way to replace its open document: the plugin writes a
    /// CAEX 3.0 copy for the user to open.
    /// </summary>
    private void Caex3Button_Click(object sender, RoutedEventArgs e)
    {
        var document = _document;
        if (document == null || !CaexUpgrade.IsNeeded(document)) return;
        var current = document.CAEXFile.FileName;
        var name = string.IsNullOrEmpty(current) ? "document" : Path.GetFileNameWithoutExtension(current);
        var dialog = new SaveFileDialog
        {
            Title = L.T("Save a CAEX 3.0 copy"),
            Filter = "AutomationML (*.aml)|*.aml",
            FileName = name + "_CAEX3.0.aml",
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var upgraded = CaexUpgrade.ToCaex3(document);
            upgraded.SaveToFile(dialog.FileName, true);
            PluginLog.Info($"Wrote a CAEX 3.0 copy of {name} to {dialog.FileName}.");
            SetStatus(L.T("CAEX 3.0 copy written to {0}. Open it in the editor to import OPC UA libraries.", Path.GetFileName(dialog.FileName)));
        }
        catch (Exception ex)
        {
            PluginLog.Error("Converting to CAEX 3.0 failed", ex);
            SetStatus(L.T("Converting to CAEX 3.0 failed: {0}", ex.Message));
        }
    }

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        OpcUaAml.Import.ConversionCache.Default.Clear();
        PluginLog.Info($"Emptied the conversion cache ({OpcUaAml.Import.ConversionCache.Default.Folder}).");
        SetStatus(L.T("Earlier conversions forgotten; the next import converts again."));
    }

    /// <summary>The folders of NodeSets fetched from servers and the Cloud Library grow with every fetch; this empties them.</summary>
    private void ForgetFetched_Click(object sender, RoutedEventArgs e) => Guard(L.T("Forgetting the fetched NodeSets"), () =>
    {
        var folders = new[] { ServerNodeSetsFolder, CloudCache, OpcfFolder }.Where(Directory.Exists).ToList();
        var files = folders.SelectMany(f => Directory.EnumerateFiles(f, "*", SearchOption.AllDirectories)).Select(f => new FileInfo(f)).ToList();
        if (files.Count == 0)
        {
            SetStatus(L.T("No fetched NodeSets are kept."));
            return;
        }
        var megabytes = files.Sum(f => f.Length) / 1048576.0;
        if (!DialogKit.Confirm(Window.GetWindow(this), "", DialogKit.Plain, L.T("Forget fetched NodeSets?"),
                L.T("{0} file(s), {1:0.0} MB, taken from servers, the OPC Foundation and the Cloud Library. They are fetched again when an import needs them. Models applied from the modeler stay; the document is not changed.", files.Count, megabytes),
                L.T("Forget")))
            return;
        foreach (var folder in folders) Directory.Delete(folder, recursive: true);
        PluginLog.Info($"Removed {files.Count} fetched NodeSet file(s) from {string.Join(" and ", folders)}.");
        SetStatus(L.T("Forgot {0} fetched NodeSet file(s).", files.Count));
    });

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

    /// <summary>True while the toggles are set from the saved settings.</summary>
    private bool _applyingSettings;

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _applyingSettings) return;
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

    /// <summary>Which commands the document and the busy state allow; the one place that decides it.</summary>
    private void UpdateCommands()
    {
        var doc = _document;
        var usable = doc != null && doc.CAEXFile.SchemaVersion == LibraryMerger.RequiredSchemaVersion;
        ImportButton.IsEnabled = usable && !_busy;
        CloudButton.IsEnabled = usable && !_busy;
        OpcfButton.IsEnabled = usable && !_busy;
        InstanceButton.IsEnabled = usable && !_busy;
        CheckButton.IsEnabled = doc != null && !_busy;
        CheckAgainButton.IsEnabled = doc != null && !_busy;
        UpgradeButton.IsEnabled = doc != null && !_busy;
        DiagramExportButton.IsEnabled = _diagram != null;
        LinkButton.IsEnabled = usable && !_busy;
        ExportButton.IsEnabled = doc != null && !_busy;
        FoldersButton.IsEnabled = !_busy;
    }

    private void UpdateState()
    {
        var doc = _document;
        var usable = doc != null && doc.CAEXFile.SchemaVersion == LibraryMerger.RequiredSchemaVersion;

        UpdateCommands();
        Placeholder.Visibility = usable ? Visibility.Collapsed : Visibility.Visible;
        Caex3Button.Visibility = doc != null && !usable ? Visibility.Visible : Visibility.Collapsed;
        Placeholder.Text = doc == null
            ? L.T("Open a CAEX 3.0 document to import OPC UA NodeSets.")
            : L.T("This document uses CAEX {0}. The OPC UA libraries of OPC 10000-83 Annex A need CAEX 3.0 (AutomationML 2.10).", doc.CAEXFile.SchemaVersion);

        RefreshDiagramSources();
        var rows = usable
            ? NamespaceOverview.Of(doc!).Select(n => new NamespaceRow(
                n.NamespaceUri, n.ModelVersion ?? "", n.PublicationDate?.ToString("yyyy-MM-dd") ?? "", n.Libraries.Count)).ToList()
            : null;
        NamespaceList.ItemsSource = rows;
        StartPanel.Visibility = usable && rows!.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshModelerNamespaces();
    }

    private void SetBusy(bool busy, string? status)
    {
        _busy = busy;
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateCommands();
        if (status != null) SetStatus(status);
        UpdateServerState();
    }

    /// <summary>The status line; the whole text is in its tooltip when the line is too short.</summary>
    private void SetStatus(string text)
    {
        StatusText.Text = text;
        StatusText.ToolTip = string.IsNullOrEmpty(text) ? null : new System.Windows.Controls.TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 600 };
    }

    /// <summary>Warnings and errors logged since the user last looked at the log.</summary>
    private int _unseenProblems;

    private void LogLink_Click(object sender, RoutedEventArgs e)
    {
        Tabs.SelectedIndex = 0;
        if (_log.Count > 0) LogList.ScrollIntoView(_log[^1]);
        LogList.Focus();
        _unseenProblems = 0;
        ShowLogLink();
    }

    private void ShowLogLink()
    {
        LogLinkText.Text = _unseenProblems == 0 ? L.T("Log") : L.T("Log ({0} new problem(s))", _unseenProblems);
        LogLinkGlyph.Visibility = _unseenProblems == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Runs a command; what it throws is logged and told, never passed on to the editor.</summary>
    private void Guard(string what, Action command)
    {
        try { command(); }
        catch (Exception ex)
        {
            PluginLog.Error($"{what} failed", ex);
            SetStatus(L.T("{0} failed: {1}", what, ex.Message));
        }
    }

    /// <summary>Another program may hold the clipboard; that is told, not thrown.</summary>
    private void CopyText(string text)
    {
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException ex) { SetStatus(L.T("The clipboard is held by another program: {0}", ex.Message)); }
    }

    /// <summary>A log line as the list shows it: time, level, message.</summary>
    public sealed record LogLine(string Time, string Level, string Message, string Text)
    {
        private static readonly System.Text.RegularExpressions.Regex Format = new(@"^\[(?<time>[^\]]*)\] \[(?<level>[^\]]*)\] (?<message>.*)$");

        public static LogLine Parse(string line)
        {
            var m = Format.Match(line);
            return m.Success
                ? new LogLine(m.Groups["time"].Value, m.Groups["level"].Value.Trim(), m.Groups["message"].Value, line)
                : new LogLine("", "", line, line);
        }

        public override string ToString() => Text;
    }

    private readonly System.Collections.ObjectModel.ObservableCollection<LogLine> _log = new();

    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _pendingLog = new();
    private int _logFlushScheduled;

    /// <summary>
    /// Lines arrive from any thread, in bursts when debugging; they are shown
    /// together, with one scroll, once the UI is idle.
    /// </summary>
    private void AppendLog(string line)
    {
        _pendingLog.Enqueue(line);
        if (Interlocked.Exchange(ref _logFlushScheduled, 1) == 0)
            Dispatcher.BeginInvoke(FlushLog, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void FlushLog()
    {
        Interlocked.Exchange(ref _logFlushScheduled, 0);
        if (LogList.ItemsSource == null) LogList.ItemsSource = _log;
        var added = false;
        while (_pendingLog.TryDequeue(out var line))
        {
            var parsed = LogLine.Parse(line);
            _log.Add(parsed);
            added = true;
            // Counted unless the log is in view; an error's own lines (type, stack trace)
            // are indented and belong to it, so they do not count again.
            if (parsed.Level is "WARN" or "ERROR" && !parsed.Message.StartsWith("  ", StringComparison.Ordinal)
                && !(Tabs.SelectedIndex == 0 && LogList.IsVisible)) _unseenProblems++;
        }
        if (!added) return;
        ShowLogLink();
        while (_log.Count > MaxLogLines) _log.RemoveAt(0);
        LogList.ScrollIntoView(_log[^1]);
    }

    private void LogCopy_Click(object sender, RoutedEventArgs e)
    {
        var lines = LogList.SelectedItems.Cast<LogLine>().Select(l => l.Text).ToList();
        if (lines.Count > 0) CopyText(string.Join(Environment.NewLine, lines));
    }

    private void LogCopyAll_Click(object sender, RoutedEventArgs e)
    {
        if (_log.Count > 0) CopyText(string.Join(Environment.NewLine, _log.Select(l => l.Text)));
    }

    private void LogClear_Click(object sender, RoutedEventArgs e) => _log.Clear();

    private void LogOpenFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(PluginLog.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus(L.T("The log file could not be opened: {0}", ex.Message));
        }
    }
}
