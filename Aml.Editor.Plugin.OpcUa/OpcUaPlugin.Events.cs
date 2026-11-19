// The server's events and alarms, below its address space beside the watched
// values: received from connecting on (the setting can leave that to More ▾),
// kept as a history across connections, the newest first, bounded however
// many a server sends. Events arrive on a stack thread and are taken into the
// list four times a second, so a flood of them does not stop the editor.

using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Aml.Editor.Plugin.OpcUa.Diagnostics;
using Aml.Editor.Plugin.OpcUa.Localization;
using OpcUaAml.Server;

namespace Aml.Editor.Plugin.OpcUa;

public partial class OpcUaPlugin
{
    private readonly EventHistory _events = new();
    private readonly ConcurrentQueue<(UaEvent Event, string Server)> _inbox = new();
    private IAsyncDisposable? _eventSubscription;
    private System.Windows.Threading.DispatcherTimer? _eventFlush;
    private bool _eventsStarting;
    private bool _droppedNoted;

    /// <summary>The server the events now arriving come from, by its application name.</summary>
    private string _eventServer = "";

    /// <summary>Severity from which a newly active alarm is announced in the status line.</summary>
    private const ushort AnnouncedSeverity = 500;

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
        else if (announce?.Event is { } alarm)
        {
            SetStatus(L.T("Alarm: {0} at {1}, severity {2}.", alarm.Message, alarm.SourceName, alarm.Severity));
        }
    }

    /// <summary>The list as the filter below it says.</summary>
    private void ShowEvents()
    {
        var selected = (EventList.SelectedItem as EventRowView)?.Row;
        var rows = _events.Filter(OpenOnlyBox.IsChecked == true, MinSeverity()).Select(r => new EventRowView(r)).ToList();
        EventList.ItemsSource = rows;
        if (selected != null) EventList.SelectedItem = rows.FirstOrDefault(r => ReferenceEquals(r.Row, selected));
        EventsTab.Header = _events.Count == 0 ? L.T("Events") : L.T("Events ({0})", _events.Count);
        UpdateLiveTabs();
        UpdateEventCommands(_client != null);
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

    /// <summary>Acknowledge… (task 6).</summary>
    private void AcknowledgeButton_Click(object sender, RoutedEventArgs e)
    {
    }

    /// <summary>Save events… (task 9).</summary>
    private void SaveEventsButton_Click(object sender, RoutedEventArgs e)
    {
    }

    /// <summary>The commands below the event list and Receive events behind More.</summary>
    private void UpdateEventCommands(bool connected)
    {
        ReceiveEventsItem.Visibility = _settings.ReceiveEvents ? Visibility.Collapsed : Visibility.Visible;
        ReceiveEventsItem.IsEnabled = connected && !ReceivingEvents && !_eventsStarting && !_busy;
    }

    /// <summary>The lists below the address space show while either has something or events are received.</summary>
    private void UpdateLiveTabs()
    {
        var any = _watchRows.Count > 0 || ReceivingEvents || _events.Count > 0;
        LiveTabs.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        WatchTab.Header = _watchRows.Count == 0 ? L.T("Watched") : L.T("Watched ({0})", _watchRows.Count);
    }
}
