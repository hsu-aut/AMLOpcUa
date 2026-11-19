// The server's events and alarms, below its address space beside the watched
// values: received from connecting on (the setting can leave that to More ▾),
// kept as a history across connections, the newest first, bounded however
// many a server sends. Events arrive on a stack thread and are taken into the
// list four times a second, so a flood of them does not stop the editor.

using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Aml.Editor.Plugin.OpcUa.Diagnostics;
using Aml.Editor.Plugin.OpcUa.Localization;
using Aml.Engine.CAEX;
using Microsoft.Win32;
using OpcUaAml.Import;
using OpcUaAml.Server;

namespace Aml.Editor.Plugin.OpcUa;

public partial class OpcUaPlugin : Aml.Editor.Plugin.Contracts.ISupportsAMLContainer
{
    private readonly EventHistory _events = new();

    /// <summary>
    /// A container (.amlx) is open. The editor gives a plugin no path for it,
    /// only the folder of its working copy and the root part, so an attachment
    /// cannot go beside it; the editor takes the file in when it saves the container.
    /// </summary>
    private bool _containerOpen;

    /// <summary>The root part of the open container ("/Plant.aml"), which names an attachment's file.</summary>
    private string? _containerPart;

    /// <summary>Where the attachments of a container wait until the editor takes them in (tests change it).</summary>
    internal static string AttachmentsFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AMLOpcUa", "attachments");

    /// <summary>
    /// The open document's path as the editor's API has it: for a container its
    /// .amlx file, which ChangeAMLFilePath does not give. Null outside the editor;
    /// tests set their own.
    /// </summary>
    internal Func<string?> EditorDocumentPath { get; set; } = ReadEditorDocumentPath;

    private static string? ReadEditorDocumentPath()
    {
        try
        {
            var path = ActiveDocumentPath();
            return !string.IsNullOrEmpty(path) && Path.IsPathRooted(path) && File.Exists(path) ? path : null;
        }
        // Outside the editor its API's assembly is not there.
        catch (Exception) { return null; }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static string? ActiveDocumentPath() => Aml.Editor.API.AMLEditor.AMLApplication?.ActiveDocument?.FilePath;

    /// <summary>
    /// Where an attachment goes: the document's path, the editor's API first,
    /// and whether the document is a container, which the editor completes on saving.
    /// </summary>
    internal (string? Path, bool Container) AttachmentPlace()
    {
        var path = EditorDocumentPath() ?? _documentPath;
        return (path, _containerOpen || (path != null && AmlContainers.IsContainerPath(path)));
    }

    /// <summary>The editor opened a container; <paramref name="containerDirectory"/> is that of its working copy.</summary>
    public void PublishAutomationMLContainer(string containerDirectory, string filePath, CAEXBasicObject selectedObject)
    {
        _containerOpen = true;
        _containerPart = filePath;
    }
    private readonly ConcurrentQueue<(UaEvent Event, string Server)> _inbox = new();
    private IAsyncDisposable? _eventSubscription;
    private System.Windows.Threading.DispatcherTimer? _eventFlush;
    private bool _eventsStarting;
    private bool _droppedNoted;

    /// <summary>The server the events now arriving come from, by its application name.</summary>
    private string _eventServer = "";

    /// <summary>Severity from which a newly active alarm is announced in the status line.</summary>
    private const ushort AnnouncedSeverity = 500;

    /// <summary>How often a new alarm was read out (tests).</summary>
    internal int AlarmsReadOut { get; private set; }

    /// <summary>When the last alarm was read out: at most one in this time, so a flood is not read out without end.</summary>
    private DateTime _lastReadOut = DateTime.MinValue;
    private static readonly TimeSpan ReadOutEvery = TimeSpan.FromSeconds(3);

    /// <summary>Whether the events of the connected server are received.</summary>
    internal bool ReceivingEvents => _eventSubscription != null;

    /// <summary>Every event received, and a row for each connection.</summary>
    internal EventHistory Events => _events;

    /// <summary>A row of the event list.</summary>
    public sealed class EventRowView(EventRow row)
    {
        public EventRow Row { get; } = row;
        public string Time { get; } = row.Time.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        public string Source { get; } = row.Event?.SourceName ?? "";
        /// <summary>The source's NodeId: its path would take a call to the server for every event.</summary>
        public string? SourceTip { get; } = row.Event?.SourceNode?.ToString();
        public string Severity { get; } = row.Event == null ? "" : row.Event.Severity.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public string Message { get; } = row.Event?.Message ?? L.T("Connected to {0}", row.Server);
        public string? MessageTip { get; } = Tip(row.Event);
        public string State { get; } = StateText(row.Event);
        public bool Active { get; } = row.Event?.Active == true;
        public Visibility DotVisibility { get; } = row.IsCondition ? Visibility.Visible : Visibility.Hidden;
        public Brush Brush { get; } = SeverityBrush(row.Event?.Severity ?? 0);

        private static string? Tip(UaEvent? e) => e?.Comment == null ? null
            : e.ClientUserId == null ? e.Comment : L.T("{0} ({1})", e.Comment, e.ClientUserId);

        private static string StateText(UaEvent? e) =>
            e == null ? "" : e.ConditionId == null ? L.T("event")
            : L.T("{0}, {1}", e.Active == true ? L.T("active") : L.T("inactive"), e.Acked == true ? L.T("acknowledged") : L.T("unacknowledged"));

        private static Brush SeverityBrush(ushort severity) => severity >= 800 ? Fault : severity >= 500 ? Warning : Info;

        private static readonly Brush Fault = Frozen(0xC0, 0x30, 0x30);
        private static readonly Brush Warning = Frozen(0xD0, 0x8A, 0x00);
        private static readonly Brush Info = Frozen(0x5A, 0x7A, 0x9A);

        private static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        /// <summary>What a screen reader says of the row.</summary>
        public override string ToString() => Row.Event == null ? Message : $"{Time} {Source}: {Message}, {State}";
    }

    /// <summary>
    /// Takes events into the list as the subscription does: queued here, shown
    /// at the next tick. For tests, which bring their own.
    /// </summary>
    internal void DeliverEvents(IEnumerable<UaEvent> events)
    {
        foreach (var e in events) _inbox.Enqueue((e, _eventServer));
        EnsureEventFlush();
    }

    private void EnsureEventFlush()
    {
        if (_eventFlush != null) return;
        _eventFlush = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _eventFlush.Tick += (_, _) => FlushEvents();
        _eventFlush.Start();
    }

    /// <summary>Takes what arrived into the history and shows it; announces a new severe alarm.</summary>
    private void FlushEvents()
    {
        if (_inbox.IsEmpty) return;
        EventRow? announce = null;
        while (_inbox.TryDequeue(out var item))
        {
            if (_events.Add(item.Event, item.Server) is not { } row) continue;
            var e = item.Event;
            if (e.ConditionId != null && e.Active == true && e.Acked != true && e.Severity >= AnnouncedSeverity) announce = row;
        }
        ShowEvents();
        if (_events.Dropped > 0 && !_droppedNoted)
        {
            _droppedNoted = true;
            SetStatus(L.T("The {0} oldest event(s) were dropped to keep the list small.", _events.Dropped));
        }
        // A new severe alarm is told beside the list, which a screen reader reads out;
        // the status line keeps what the last command said.
        if (announce?.Event is { } alarm)
        {
            // The line always shows the newest; it is read out at most every few seconds.
            AlarmAnnouncement.Text = L.T("Alarm: {0} at {1}, severity {2}.", alarm.Message, alarm.SourceName, alarm.Severity);
            AlarmAnnouncement.Visibility = Visibility.Visible;
            if (DateTime.UtcNow - _lastReadOut >= ReadOutEvery)
            {
                _lastReadOut = DateTime.UtcNow;
                DialogKit.Announce(AlarmAnnouncement);
                AlarmsReadOut++;
            }
        }
    }

    /// <summary>The rows shown, the newest first; one view per row, kept while the row is.</summary>
    private System.Collections.ObjectModel.ObservableCollection<EventRowView> _eventViews = new();
    private readonly Dictionary<EventRow, EventRowView> _viewOf = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
    private (bool OnlyOpen, int MinSeverity) _shownFilter = (false, -1);

    /// <summary>At most this many new rows go on top one by one; more, and the list is built anew.</summary>
    private const int InsertedAtMost = 200;

    /// <summary>
    /// The list as the filter below it says. New rows go on top of the rows
    /// shown, so the list stays where the user scrolled it; a new filter, the
    /// open alarms (which change in the middle) or a flood build it anew.
    /// </summary>
    private void ShowEvents()
    {
        var selected = (EventList.SelectedItem as EventRowView)?.Row;
        var filter = (OpenOnlyBox.IsChecked == true, MinSeverity());
        var rows = _events.Filter(filter.Item1, filter.Item2);
        EventRowView View(EventRow r)
        {
            if (!_viewOf.TryGetValue(r, out var view)) _viewOf[r] = view = new EventRowView(r);
            return view;
        }
        var newer = _eventViews.Count == 0 ? -1 : IndexOf(rows, _eventViews[0].Row);
        if (filter == _shownFilter && !filter.Item1 && newer >= 0 && newer <= InsertedAtMost && ReferenceEquals(EventList.ItemsSource, _eventViews))
        {
            for (var i = newer - 1; i >= 0; i--) _eventViews.Insert(0, View(rows[i]));
            // The rows that went are the oldest, at the end.
            while (_eventViews.Count > rows.Count) _eventViews.RemoveAt(_eventViews.Count - 1);
        }
        else
        {
            _eventViews = new System.Collections.ObjectModel.ObservableCollection<EventRowView>(rows.Select(View));
            EventList.ItemsSource = _eventViews;
        }
        _shownFilter = filter;
        // Views of rows the history dropped go too.
        if (_viewOf.Count > _events.Count + 1000)
        {
            var kept = _events.Rows.ToHashSet(System.Collections.Generic.ReferenceEqualityComparer.Instance);
            foreach (var gone in _viewOf.Keys.Where(k => !kept.Contains(k)).ToList()) _viewOf.Remove(gone);
        }
        if (selected != null && (EventList.SelectedItem as EventRowView)?.Row != selected)
            EventList.SelectedItem = _eventViews.FirstOrDefault(v => ReferenceEquals(v.Row, selected));
        EventsTab.Header = _events.Count == 0 ? L.T("Events") : L.T("Events ({0})", _events.Count);
        UpdateLiveTabs();
        UpdateEventCommands(_client != null);
    }

    private static int IndexOf(IReadOnlyList<EventRow> rows, EventRow row)
    {
        for (var i = 0; i < rows.Count; i++) if (ReferenceEquals(rows[i], row)) return i;
        return -1;
    }

    private int MinSeverity() => SeverityBox.SelectedIndex switch { 1 => 200, 2 => 500, 3 => 800, _ => 0 };

    private void EventFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        ShowEvents();
    }

    private void EventList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateEventCommands(_client != null);

    /// <summary>The row of a connection, with the server's application name.</summary>
    private void AddConnectedRow(UaClient client)
    {
        _events.AddConnected(client.ServerName, DateTimeOffset.Now);
        ShowEvents();
    }

    /// <summary>Receives the connected server's events, after a ConditionRefresh for the alarms already active.</summary>
    internal async Task StartEventsAsync()
    {
        var client = _client;
        if (client == null || _eventSubscription != null || _eventsStarting) return;
        _eventsStarting = true;
        var server = client.ServerName;
        try
        {
            // Set before the first events, which come with the subscription.
            _eventServer = server;
            var subscription = await client.SubscribeEventsAsync(e => _inbox.Enqueue((e, server)));
            // Closed or disconnected while the server answered: no one would end it.
            if (_released || !ReferenceEquals(_client, client))
            {
                await subscription.DisposeAsync();
                return;
            }
            _eventSubscription = subscription;
            EnsureEventFlush();
            if (_watchRows.Count == 0) LiveTabs.SelectedItem = EventsTab;
            PluginLog.Info($"Receiving the events of {server}.");
        }
        catch (UaEventsUnavailableException ex)
        {
            PluginLog.Info($"{server} sends no events ({ex.Status}).");
            SetStatus(L.T("{0} sends no events.", server));
        }
        catch (Exception) when (_released)
        {
        }
        catch (Exception ex)
        {
            PluginLog.Error("Receiving events failed", ex);
            SetStatus(L.T("Receiving events failed: {0}", ex.Message));
        }
        finally
        {
            _eventsStarting = false;
            UpdateServerState();
        }
    }

    /// <summary>Ends the event subscription; the list stays.</summary>
    internal async Task StopEventsAsync()
    {
        var subscription = _eventSubscription;
        _eventSubscription = null;
        if (subscription != null)
        {
            try { await subscription.DisposeAsync(); }
            catch (Exception ex) { PluginLog.Debug($"Ending the event subscription: {ex.Message}"); }
        }
        UpdateLiveTabs();
    }

    private async void ReceiveEventsItem_Click(object sender, RoutedEventArgs e) => await StartEventsAsync();

    /// <summary>Acknowledge…: the selected alarm, after asking.</summary>
    private async void AcknowledgeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_client == null || EventList.SelectedItem is not EventRowView view || !_events.CanAcknowledge(view.Row)) return;
        var window = new AcknowledgeWindow(view.Row, _events.ActiveSince(view.Row)) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true) return;
        await AcknowledgeRowAsync(view.Row, window.Comment.Text);
    }

    /// <summary>
    /// Acknowledges the alarm of <paramref name="row"/> with the EventId of its
    /// newest state; the new state comes from the server as a row of its own.
    /// </summary>
    internal async Task AcknowledgeRowAsync(EventRow row, string comment)
    {
        if (row.Event?.ConditionId is not { } condition) return;
        // The state the dialog showed, and only while it is the newest: one that came
        // while the dialog was open was not seen, and is not acknowledged in its stead.
        if (!ReferenceEquals(_events.LatestOf(condition), row))
        {
            SetStatus(L.T("The alarm has changed; select it again."));
            return;
        }
        var client = _client;
        if (client == null) return;
        var shown = row.Event;
        try
        {
            var answer = await client.AcknowledgeAsync(condition, shown.EventId, comment);
            PluginLog.Info($"Acknowledge {shown.ConditionName} ({condition}): {answer.Status}.");
            SetStatus(AcknowledgeText(answer, shown));
        }
        catch (Exception) when (_released)
        {
        }
        catch (Exception ex)
        {
            PluginLog.Error("Acknowledging failed", ex);
            SetStatus(L.T("Acknowledging failed: {0}", ex.Message));
        }
    }

    /// <summary>What the status line says of the server's answer to Acknowledge.</summary>
    internal static string AcknowledgeText(UaWriteResult answer, UaEvent alarm) =>
        answer.Good ? L.T("Acknowledged '{0}'.", alarm.Message)
        : answer.Status == "BadEventIdUnknown" ? L.T("The alarm has changed; select it again.")
        : L.T("Acknowledging failed: {0}", answer.Status);

    /// <summary>Save events…: as a CSV file, or as an attachment of an element of the document.</summary>
    private void SaveEventsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_events.Count == 0) return;
        var document = _document;
        var owner = AttachmentOwner();
        var place = AttachmentPlace();
        var window = new SaveEventsWindow(document != null && owner != null ? place.Path : null,
            document != null && owner != null && place.Container, owner?.Name ?? "", () =>
        {
            if (document == null) return null;
            var picker = new ElementPickerWindow(document, L.T("Attach the events to")) { Owner = Window.GetWindow(this) };
            if (picker.ShowDialog() != true || picker.Selected == null) return null;
            owner = picker.Selected;
            return owner.Name;
        }) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true) return;
        if (window.AttachmentChoice.IsChecked == true && owner != null) SaveEventsAsAttachment(owner, DateTime.Now);
        else SaveEventsAsCsv();
    }

    /// <summary>The element an attachment goes to unless the user chooses another: the one selected in the editor, else the topmost one.</summary>
    private InternalElementType? AttachmentOwner() =>
        _editorSelection is InternalElementType selected && ReferenceEquals(selected.CAEXDocument, _document) ? selected
        : _document?.CAEXFile.InstanceHierarchy.FirstOrDefault()?.InternalElement.FirstOrDefault();

    /// <summary>The events as a CSV file beside the document, attached to <paramref name="owner"/>.</summary>
    internal void SaveEventsAsAttachment(InternalElementType owner, DateTime now)
    {
        var place = AttachmentPlace();
        if (place.Path == null && !place.Container) return;
        // Beside a document whose path is known; for a container without one, in the
        // plugin's folder, named by its full path until the editor takes it in.
        string file, uri;
        bool container;
        try
        {
            if (place.Path is { } documentPath)
            {
                file = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(documentPath)) ?? "", Attachments.FileName(documentPath, now));
                uri = Attachments.UriFor(documentPath, file);
                container = AmlContainers.IsContainerPath(documentPath);
            }
            else
            {
                var name = _containerPart is { Length: > 0 } part ? Path.GetFileName(part) : "Document.aml";
                Directory.CreateDirectory(AttachmentsFolder);
                file = Path.Combine(AttachmentsFolder, Attachments.FileName(name, now));
                uri = new Uri(file).AbsoluteUri;
                container = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            PluginLog.Error("Saving the events failed", ex);
            SetStatus(L.T("Saving the events failed: {0}", ex.Message));
            return;
        }
        try
        {
            File.WriteAllText(file, _events.ToCsv(), new System.Text.UTF8Encoding(true));
            Attachments.Attach(owner, uri, "text/csv",
                L.T("Events {0}", now.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)));
            var text = L.T("Saved {0} event(s) to {1} and attached it to '{2}'.", _events.Count, Path.GetFileName(file), owner.Name);
            text += container
                ? L.T(" When you save the container (Ctrl+S), the editor offers to take the file in; keep it ticked there.")
                : L.T(" Press Ctrl+S to save.");
            if (_events.Dropped > 0) text += L.T(" The {0} oldest were dropped to keep the list small.", _events.Dropped);
            PluginLog.Info($"Saved {_events.Count} event(s) to {file}, attached to {owner.Name}.");
            SetStatus(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PluginLog.Error($"Saving the events to {file} failed", ex);
            SetStatus(L.T("Saving the events failed: {0}", ex.Message));
        }
    }

    /// <summary>The events as a CSV file where the user says.</summary>
    private void SaveEventsAsCsv()
    {
        var dialog = new SaveFileDialog
        {
            Title = L.T("Save the events"),
            Filter = L.T("CSV, semicolons (*.csv)|*.csv"),
            FileName = $"events-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, _events.ToCsv(), new System.Text.UTF8Encoding(true));
            var text = L.T("Saved {0} event(s) to {1}.", _events.Count, dialog.FileName);
            if (_events.Dropped > 0) text += L.T(" The {0} oldest were dropped to keep the list small.", _events.Dropped);
            PluginLog.Info(text);
            SetStatus(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PluginLog.Error($"Saving the events to {dialog.FileName} failed", ex);
            SetStatus(L.T("Saving the events failed: {0}", ex.Message));
        }
    }

    /// <summary>The commands below the event list and Receive events behind More.</summary>
    private void UpdateEventCommands(bool connected)
    {
        ReceiveEventsItem.Visibility = _settings.ReceiveEvents ? Visibility.Collapsed : Visibility.Visible;
        ReceiveEventsItem.IsEnabled = connected && !ReceivingEvents && !_eventsStarting && !_busy;
        AcknowledgeButton.IsEnabled = connected && EventList.SelectedItem is EventRowView view && _events.CanAcknowledge(view.Row);
        SaveEventsButton.IsEnabled = _events.Count > 0;
    }

    /// <summary>The lists below the address space show while either has something or events are received.</summary>
    private void UpdateLiveTabs()
    {
        var any = _watchRows.Count > 0 || ReceivingEvents || _events.Count > 0;
        LiveTabs.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        WatchTab.Header = _watchRows.Count == 0 ? L.T("Watched") : L.T("Watched ({0})", _watchRows.Count);
    }
}
