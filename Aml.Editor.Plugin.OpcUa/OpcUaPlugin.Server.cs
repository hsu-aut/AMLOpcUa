// The "Server" tab: connect to a running OPC UA server, browse its address
// space, take its types and parts of its address space into the document, bind
// nodes to elements and read current values. All server work goes through
// OpcUaAml.Core.Server.

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Aml.Editor.Plugin.Contracts;
using Aml.Editor.Plugin.OpcUa.Diagnostics;
using Aml.Editor.Plugin.OpcUa.Localization;
using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using Microsoft.Win32;
using OpcUaAml.Addressing;
using OpcUaAml.Binding;
using OpcUaAml.Import;
using OpcUaAml.NodeSets;
using OpcUaAml.Server;

namespace Aml.Editor.Plugin.OpcUa;

public partial class OpcUaPlugin : ISupportsSelection
{
    private UaClient? _client;
    private IAsyncDisposable? _watch;
    private LiveValues? _live;
    /// <summary>While values are kept live: looks every two seconds whether the bindings changed.</summary>
    private System.Windows.Threading.DispatcherTimer? _liveCheck;
    private bool _liveChecking;
    private AmlServerHost? _host;
    private IDisposable? _hostFollow;
    private bool _structureNoted;
    private bool _liveStarting;

    /// <summary>
    /// The live session writes while this is its token: set before the
    /// subscription exists, so the first values, which arrive while it is set
    /// up, are written too; cleared when it stops.
    /// </summary>
    private object? _liveToken;
    private readonly SemaphoreSlim _watchGate = new(1, 1);

    /// <summary>For tests: whether a watch subscription is held, and whether none is being set up.</summary>
    internal bool Watching => _watch != null;
    internal bool WatchSettled => _watchGate.CurrentCount == 1;
    private int _detailsVersion;
    private readonly System.Collections.ObjectModel.ObservableCollection<WatchRow> _watchRows = new();

    /// <summary>Every value the watch received, with its time, until Stop watching; kept past a disconnect, to save.</summary>
    private readonly WatchRecording _recording = new();

    /// <summary>A row of the live list; updated from the subscription thread through the dispatcher.</summary>
    public sealed class WatchRow : System.ComponentModel.INotifyPropertyChanged
    {
        public required UaNodeAddress Address { get; init; }
        public required string Node { get; init; }
        public string Where => Address.ToString();
        public string Value { get; private set; } = "";
        public string Status { get; private set; } = L.T("waiting for the first value");
        public bool Good { get; private set; }
        public string Received { get; private set; } = "";

        /// <summary>The unit its server states (EngineeringUnits), empty for none.</summary>
        public string Unit { get; private set; } = "";

        public void SetUnit(string unit)
        {
            Unit = unit;
            PropertyChanged?.Invoke(this, new(nameof(Unit)));
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public void Update(UaReadResult r)
        {
            Value = r.ValueText ?? "";
            Status = r.Good ? "Good" : r.Status;
            Good = r.Good;
            Received = DateTime.Now.ToString("HH:mm:ss");
            PropertyChanged?.Invoke(this, new(null));
        }

        /// <summary>What a screen reader says of the row: UI Automation names a row by it.</summary>
        public override string ToString() => Received.Length == 0 ? $"{Node}: {Status}"
            : Unit.Length == 0 ? $"{Node}: {Value}, {Status}" : $"{Node}: {Value} {Unit}, {Status}";
    }

    /// <summary>Asks the editor to select an element in its tree (after mirroring or binding).</summary>
    public event EventHandler<SelectionEventArgs>? Selected;

    /// <summary>
    /// The endpoint offered before any was used: this computer by its address.
    /// "localhost" tries ::1 first, and a server on 127.0.0.1 only then answers
    /// after about two seconds, on every connection.
    /// </summary>
    internal const string DefaultEndpoint = "opc.tcp://127.0.0.1:4840";

    /// <summary>The user answered the trust dialog with Cancel.</summary>
    internal sealed class TrustDeclinedException : Exception;

    /// <summary>The server is trusted now, and still refused: it does not accept this computer's certificate.</summary>
    internal sealed class ClientRefusedException : Exception;

    /// <summary>What to tell after connecting failed, and whether it is an error or the user's own choice.</summary>
    internal static (string Text, bool IsError) DescribeConnectFailure(Exception ex, string url, string pkiRoot)
    {
        var own = Path.Combine(pkiRoot, "own", "certs");
        if (ex is TrustDeclinedException) return (L.T("Not connected: the certificate of {0} was not trusted.", url), false);
        var refused = ex is ClientRefusedException;
        for (var inner = ex.InnerException; inner != null && !refused; inner = inner.InnerException)
            refused = inner is Opc.Ua.ServiceResultException { StatusCode: var code } && code == Opc.Ua.StatusCodes.BadSecurityChecksFailed;
        if (refused)
            return (L.T("The server at {0} did not accept this computer's certificate. Its administrator has to trust it; it is in '{1}'.", url, own), true);
        return (ex is UaConnectionException ? ex.Message : L.T("Connecting to {0} failed: {1}", url, ex.Message), true);
    }

    private void InitServerTab()
    {
        EndpointBox.ItemsSource = _settings.RecentEndpoints;
        EndpointBox.Text = _settings.LastEndpointUrl ?? DefaultEndpoint;
        SecurityToggle.IsChecked = _settings.UseSecurity;
        MirrorMaxNodesBox.Text = _settings.MirrorMaxNodes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        InitSelection();
        UpdateServerState();
    }

    private async void AddressTree_KeyDown(object sender, KeyEventArgs e)
    {
        // Space checks or unchecks the selected node, as a click on its box does.
        if (e.Key == System.Windows.Input.Key.Space && AddressTree.SelectedItem is TreeViewItem { Header: StackPanel header }
            && header.Children.OfType<CheckBox>().FirstOrDefault() is { IsEnabled: true } check)
        {
            e.Handled = true;
            check.IsChecked = check.IsChecked != true;
            check.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            return;
        }
        if (e.Key != System.Windows.Input.Key.F5 || _client == null) return;
        e.Handled = true;
        if (await LoadRootAsync()) SetStatus(L.T("Address space reloaded."));
    }

    private void EndpointBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter || _client != null || _busy) return;
        e.Handled = true;
        ConnectButton_Click(sender, e);
    }

    /// <summary>Keeps an endpoint at the top of the recently used ones.</summary>
    private void RememberEndpoint(string url)
    {
        _settings.RecentEndpoints.RemoveAll(u => string.Equals(u, url, StringComparison.OrdinalIgnoreCase));
        _settings.RecentEndpoints.Insert(0, url);
        if (_settings.RecentEndpoints.Count > 8) _settings.RecentEndpoints.RemoveRange(8, _settings.RecentEndpoints.Count - 8);
        _settings.Save();
        EndpointBox.ItemsSource = null;
        EndpointBox.ItemsSource = _settings.RecentEndpoints;
        EndpointBox.Text = url;
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_client != null)
        {
            await DisconnectAsync();
            return;
        }

        var url = EndpointBox.Text.Trim();
        _settings.LastEndpointUrl = url;
        _settings.UseSecurity = SecurityToggle.IsChecked == true;
        _settings.Save();

        SetBusy(true, L.T("Connecting to {0} …", url));
        var token = BeginCancellable();
        try
        {
            var connected = await UntilCancelled(ConnectWithTrustPromptAsync(url, token), token, c => c.DisposeAsync().AsTask());
            // Closed while connecting: the session would stay open until the editor ends.
            if (_released)
            {
                await connected.DisposeAsync();
                return;
            }
            await AttachClientAsync(connected, url);
        }
        // The stack wraps a cancellation in its own exceptions; what counts is that the user cancelled.
        catch (Exception) when (token.IsCancellationRequested && !_released)
        {
            PluginLog.Info($"Connecting to {url} cancelled.");
            SetStatus(L.T("Connecting to {0} cancelled.", url));
        }
        catch (Exception ex)
        {
            var (text, isError) = DescribeConnectFailure(ex, url, new UaConnectOptions { EndpointUrl = url }.PkiRoot);
            if (!isError) PluginLog.Info(text);
            else if (ex is UaConnectionException or ClientRefusedException) PluginLog.Error(text);
            else PluginLog.Error($"Connecting to {url} failed", ex);
            SetStatus(text);
        }
        finally
        {
            EndCancellable();
            SetBusy(false, null);
            UpdateServerState();
        }
    }

    /// <summary>Takes a connected client: follows whether it answers, remembers the endpoint, shows the address space.</summary>
    internal async Task AttachClientAsync(UaClient client, string url)
    {
        _client = client;
        client.ReachableChanged += reachable => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_client, client)) return;
            if (reachable) PluginLog.Info($"{url} answers again.");
            else PluginLog.Warn($"{url} does not answer any more.");
            SetStatus(reachable ? L.T("{0} answers again.", url) : L.T("{0} does not answer any more. Disconnect, and connect again once it is back.", url));
            UpdateServerState();
        });
        PluginLog.Info($"Connected to {url} ({client.SecurityMode}); {client.NamespaceTable.Count} namespaces.");
        SetStatus(L.T("Connected to {0}.", url));
        RememberEndpoint(url);
        await LoadRootAsync();
    }

    /// <summary>The width the endpoint URL keeps at least before user, password and Secure leave its line.</summary>
    private const double EndpointRoom = 240;

    private void ConnectionBand_SizeChanged(object sender, SizeChangedEventArgs e) => FitConnectionBand();

    /// <summary>
    /// One line for the connection when the endpoint URL keeps room in it; else
    /// user, password and Secure go to a second line below it. The state beside
    /// the URL grows when connected, so this is asked again then.
    /// </summary>
    private void FitConnectionBand()
    {
        var width = ConnectionBand.ActualWidth;
        if (width <= 0) return;
        var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
        ConnectionPill.MaxWidth = double.PositiveInfinity;
        foreach (var part in new FrameworkElement[] { ConnectionPill, Credentials, ConnectCommands }) part.Measure(unbounded);
        var oneLine = ConnectionPill.DesiredSize.Width + EndpointRoom + Credentials.DesiredSize.Width + ConnectCommands.DesiredSize.Width;
        var twoLines = oneLine > width;
        // The state grows while serving ("serving at …"); beyond what leaves the URL its
        // room it is shortened, and the whole of it is the tooltip.
        var beside = ConnectCommands.DesiredSize.Width + (twoLines ? 0 : Credentials.DesiredSize.Width);
        ConnectionPill.MaxWidth = Math.Max(80, width - beside - EndpointRoom);
        Grid.SetRow(Credentials, twoLines ? 1 : 0);
        Grid.SetColumn(Credentials, twoLines ? 0 : 1);
        Grid.SetColumnSpan(Credentials, twoLines ? 3 : 1);
        Credentials.HorizontalAlignment = twoLines ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        Credentials.Margin = twoLines ? new Thickness(-8, 4, 0, 0) : new Thickness(0);
    }

    /// <summary>When a certificate is valid, for the dialog that asks to trust it.</summary>
    internal static string Validity(DateTime from, DateTime to, DateTime now)
    {
        // As every date of the plugin, whatever the regional settings say.
        var validFrom = from.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var validTo = to.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return to < now ? L.T("{0} to {1}  (expired)", validFrom, validTo) : L.T("{0} to {1}", validFrom, validTo);
    }

    /// <summary>
    /// Connects; if the server's certificate is unknown, shows it and asks,
    /// instead of trusting silently. Trusted, exactly that certificate is kept,
    /// so the next connection checks it again: a different one is asked about.
    /// </summary>
    private async Task<UaClient> ConnectWithTrustPromptAsync(string url, CancellationToken ct)
    {
        var options = new UaConnectOptions
        {
            EndpointUrl = url,
            UseSecurity = _settings.UseSecurity,
            UserName = string.IsNullOrWhiteSpace(UserBox.Text) ? null : UserBox.Text.Trim(),
            Password = PasswordBox.Password,
        };
        try
        {
            return await UaClient.ConnectAsync(options, ct);
        }
        catch (UaConnectionException ex) when (ex.UntrustedCertificate is { } certificate)
        {
            var trust = DialogKit.Confirm(Window.GetWindow(this), "\uE72E", DialogKit.Verify, L.T("Trust this server?"),
                L.T("The server at {0} presents a certificate this computer does not know. Trust it only if it is the server you mean: compare the fingerprint with the one its administrator gives you. Trusted, it is kept; a server that later shows another certificate is asked about again.", url),
                L.T("Trust this certificate"),
                DialogKit.Facts(
                    (L.T("Subject"), certificate.Subject, false),
                    (L.T("Issued by"), certificate.Issuer == certificate.Subject ? L.T("itself (self-signed)") : certificate.Issuer, false),
                    (L.T("Valid"), Validity(certificate.NotBefore, certificate.NotAfter, DateTime.Now), false),
                    (L.T("SHA-256"), certificate.Sha256, true)),
                risky: true);
            if (!trust) throw new TrustDeclinedException();
            UaClient.TrustServer(certificate, options.PkiRoot);
            PluginLog.Info($"Trusted the certificate of {url}: {certificate.Subject}, SHA-256 {certificate.Sha256}.");
            try
            {
                return await UaClient.ConnectAsync(options, ct);
            }
            catch (UaConnectionException again) when (again.UntrustedCertificate != null)
            {
                // Its certificate is trusted now; what is refused is ours.
                PluginLog.Error(again.Message);
                throw new ClientRefusedException();
            }
        }
    }

    /// <summary>Find servers…: an address from a discovery server into the endpoint box.</summary>
    private void FindServersButton_Click(object sender, RoutedEventArgs e)
    {
        // The host of what is in the box, to ask there first; this computer otherwise.
        var where = Uri.TryCreate(EndpointBox.Text.Trim(), UriKind.Absolute, out var uri) ? uri.Host : "";
        var window = new FindServersWindow(where) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true || window.Chosen == null) return;
        EndpointBox.Text = window.Chosen;
        if (window.Secure is bool secure) SecurityToggle.IsChecked = secure;
        SetStatus(L.T("Took {0} from the discovery server; Connect connects to it.", window.Chosen));
    }

    /// <summary>Check server: the instances below the selected node, or of the whole server, against their types.</summary>
    private async void CheckServerButton_Click(object sender, RoutedEventArgs e)
    {
        var client = _client;
        if (client == null) return;
        var start = SelectedNode is { NodeClass: "Object" or "Variable" } node ? node : null;
        SetBusy(true, start == null ? L.T("Checking the server against its types …") : L.T("Checking {0} against its types …", start.DisplayName));
        var token = BeginCancellable();
        try
        {
            var result = await OpcUaAml.Checks.ServerChecker.CheckAsync(client, start?.Address, ct: token);
            var findings = result.Findings;
            // The Check tab shows the server now: Trust signer… was for a Descriptor checked before.
            TrustSignerButton.Visibility = Visibility.Collapsed;
            var errors = findings.Count(f => f.Severity == OpcUaAml.Checks.Severity.Error);
            FindingList.ItemsSource = findings.Select(f => new FindingRow(f)).ToList();
            CheckTab.Header = findings.Count == 0 ? L.T("Check") : L.T("Check ({0})", findings.Count);
            var summary = L.T("Server {0}, {1} node(s) checked: {2} error(s), {3} warning(s).", client.EndpointUrl, result.Checked, errors, findings.Count - errors);
            if (findings.Count > 0)
                summary += " " + L.T("Rules: ") + string.Join("; ", findings.Select(f => f.Rule).Distinct().OrderBy(r => r)
                    .Select(r => $"{r} {L.T(OpcUaAml.Checks.Rules.Descriptions[r])}"));
            if (result.Truncated) summary += " " + L.T("The check stopped after {0} nodes; select a part of the server to check the rest.", result.Checked);
            CheckSummary.Text = summary;
            Tabs.SelectedItem = CheckTab;
            PluginLog.Info($"Server check of {client.EndpointUrl}{(start != null ? " below " + start.Address : "")}: {result.Checked} node(s), {errors} error(s), {findings.Count - errors} warning(s).");
            foreach (var f in findings) PluginLog.Info("  " + f);
            SetStatus(L.T("Server check: {0} error(s), {1} warning(s).", errors, findings.Count - errors));
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            SetStatus(L.T("Checking the server cancelled."));
        }
        catch (Exception ex)
        {
            PluginLog.Error("Checking the server failed", ex);
            SetStatus(L.T("Checking the server failed: {0}", ex.Message));
        }
        finally
        {
            EndCancellable();
            SetBusy(false, null);
        }
    }

    private async void WatchButton_Click(object sender, RoutedEventArgs e)
    {
        var node = SelectedNode;
        if (node == null || _client == null || node.NodeClass != "Variable") return;
        if (_watchRows.Any(r => r.Address == node.Address)) return;
        var row = new WatchRow { Address = node.Address, Node = node.DisplayName };
        // Its unit, where the server states one, before the first value arrives, so the
        // recording has it from the start; read once, a unit does not move.
        try
        {
            if (_client is { } client && await client.ReadEngineeringUnitsAsync(node.Address) is { } unit) row.SetUnit(unit.DisplayName);
        }
        catch (Exception ex) { PluginLog.Info($"No unit read for {node.Address}: {ex.Message}"); }
        if (_released || _watchRows.Any(r => r.Address == node.Address)) return;
        _watchRows.Add(row);
        await RestartWatchAsync();
        // Save values… and Unwatch follow the list; nothing else would tell them now.
        UpdateServerState();
    }

    private async void UnwatchButton_Click(object sender, RoutedEventArgs e)
    {
        _watchRows.Clear();
        _recording.Clear();
        await RestartWatchAsync();
        UpdateServerState();
    }

    /// <summary>
    /// A value the watch received. A row no longer in the list (Stop watching, while
    /// values were still on their way) takes it no more, nor does the recording.
    /// </summary>
    internal void RecordWatchValue(WatchRow row, UaReadResult read)
    {
        if (!_watchRows.Contains(row)) return;
        row.Update(read);
        _recording.Add(DateTimeOffset.Now, row.Node, row.Unit, read);
    }

    /// <summary>How many values the watch recorded, for Save values….</summary>
    internal int RecordedValues => _recording.Count;

    /// <summary>Save values…: what the watch received, as CSV.</summary>
    private void SaveWatchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recording.Count == 0)
        {
            SetStatus(L.T("The watch has received no value yet."));
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = L.T("Save the watched values"),
            Filter = L.T("CSV, semicolons (*.csv)|*.csv"),
            FileName = $"watch-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, _recording.ToCsv(), new System.Text.UTF8Encoding(true));
            var text = L.T("Saved {0} value(s) to {1}.", _recording.Count, dialog.FileName);
            if (_recording.Dropped > 0) text += L.T(" The {0} oldest were dropped to keep the list small.", _recording.Dropped);
            PluginLog.Info(text);
            SetStatus(text);
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Saving the watched values to {dialog.FileName} failed", ex);
            SetStatus(L.T("Saving the watched values failed: {0}", ex.Message));
        }
    }

    /// <summary>
    /// One subscription for the whole list, recreated when the list changes.
    /// Quick clicks wait for each other, so no subscription is left behind.
    /// </summary>
    private async Task RestartWatchAsync()
    {
        await _watchGate.WaitAsync();
        try
        {
            if (_watch != null) { await _watch.DisposeAsync(); _watch = null; }
            WatchList.ItemsSource = _watchRows;
            WatchList.Visibility = _watchRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            var client = _client;
            if (client == null || _watchRows.Count == 0) return;
            var rows = _watchRows.ToDictionary(r => r.Address);
            var watch = await client.WatchAsync(rows.Keys.ToList(), r =>
                Dispatcher.BeginInvoke(() =>
                {
                    if (rows.TryGetValue(r.Address, out var row)) RecordWatchValue(row, r);
                }));
            // Closed or disconnected while the server answered: no one would end this subscription.
            if (_released || !ReferenceEquals(_client, client))
            {
                await watch.DisposeAsync();
                return;
            }
            _watch = watch;
        }
        // The plugin closed its session meanwhile; there is no one to tell.
        catch (Exception) when (_released)
        {
        }
        catch (Exception ex)
        {
            PluginLog.Error("Watching failed", ex);
            SetStatus(L.T("Watching failed: {0}", ex.Message));
        }
        finally
        {
            _watchGate.Release();
        }
    }

    private async void ServeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_host != null)
        {
            await StopServingAsync(null);
            return;
        }
        var document = _document;
        if (document == null) return;
        if (!NumbersValid(ServePortBox)) return;
        var port = ValueOf(ServePortBox)!.Value;
        var network = ServeNetworkBox.IsChecked == true;
        var simulate = ServeSimulateBox.IsChecked == true;
        var withTypes = ServeTypesBox.IsChecked == true;
        SetBusy(true, L.T("Starting the document server …"));
        var token = BeginCancellable();
        try
        {
            // Cancelled while starting, the server ends and frees its port.
            var started = await AmlServerHost.StartAsync(document, new AmlServerOptions
            {
                Port = port, Network = network, Simulate = simulate, WithTypes = withTypes,
                Folders = KnownNodeSetFolders(_settings.NodeSetFolders, ModelsFolder, CloudCache, OpcfFolder, ServerNodeSetsFolder, ImportedFolder).ToList(),
            }, token);
            // Closed, or another document opened, while it started (the first start
            // makes a certificate and takes seconds): it would hold the port until the
            // editor ends, or serve a closed document.
            if (_released || !ReferenceEquals(_document, document))
            {
                await started.DisposeAsync();
                return;
            }
            _host = started;
            if (_host.CertificateReplaced)
                PluginLog.Info("The document server made itself a new certificate for its address; clients that trusted the old one ask again.");
            // Values edited in the document reach the served nodes; new or removed elements need a restart.
            _structureNoted = false;
            var host = _host;
            _hostFollow = host.FollowDocument(refresh => Dispatcher.BeginInvoke(() => { if (ReferenceEquals(_host, host)) refresh(); }), changed =>
            {
                if (changed > 0) PluginLog.Debug($"Served values updated: {changed}.");
                if (!host.StructureChanged || _structureNoted) return;
                _structureNoted = true;
                NoteServedStructureChanged();
            });
            var message = (network, simulate) switch
            {
                (true, true) => L.T("Serving {0} node(s) of this document at {1}, to other computers too, to trusted clients only (Clients…), with simulated values.", _host.Nodes, _host.EndpointUrl),
                (true, false) => L.T("Serving {0} node(s) of this document at {1}, to other computers too, to trusted clients only (Clients…).", _host.Nodes, _host.EndpointUrl),
                (false, true) => L.T("Serving {0} node(s) of this document at {1}, to this computer only, with simulated values.", _host.Nodes, _host.EndpointUrl),
                _ => L.T("Serving {0} node(s) of this document at {1}, to this computer only.", _host.Nodes, _host.EndpointUrl),
            };
            if (_host.ServedTypes.Count > 0) message += L.T(" With the types of {0}.", string.Join(", ", _host.ServedTypes));
            if (_host.MissingTypes.Count > 0) message += L.T(" No NodeSet found for {0}; instances of their types are served as plain objects.", string.Join(", ", _host.MissingTypes));
            PluginLog.Info(message);
            SetStatus(message);
            if (_client == null) EndpointBox.Text = _host.EndpointUrl;
        }
        catch (Exception) when (token.IsCancellationRequested && !_released)
        {
            SetStatus(L.T("Starting the document server cancelled."));
        }
        catch (Exception ex)
        {
            PluginLog.Error("Starting the document server failed", ex);
            SetStatus(L.T("Starting the document server failed: {0}", ex.Message));
        }
        finally
        {
            EndCancellable();
            SetBusy(false, null);
            UpdateServerState();
        }
    }

    /// <summary>
    /// What keeping values live follows: the bound values of this server, those
    /// bound to another server, and those this server does not have.
    /// </summary>
    internal static string LiveStartText(int count, int skipped, int notOnServer, string endpoint)
    {
        if (count == 0 && notOnServer > 0) return L.T("None of the {0} bound value(s) is on {1} (see log).", notOnServer, endpoint);
        if (count == 0) return Summaries.NothingBound;
        var text = L.T("Following {0} bound value(s) of {1}", count, endpoint);
        if (skipped > 0) text += L.T("; {0} bound to another server left out", skipped);
        if (notOnServer > 0) text += L.T("; {0} not on this server (see log)", notOnServer);
        return text + ".";
    }

    /// <summary>
    /// Says that the served document gained or lost elements, after what the
    /// command that changed it reported, not instead of it.
    /// </summary>
    internal void NoteServedStructureChanged()
    {
        var hint = L.T("Restart serving to show added or removed elements.");
        PluginLog.Info("Elements were added or removed; restart serving to show them.");
        if (StatusText.Text.Contains(hint, StringComparison.Ordinal)) return;
        SetStatus(StatusText.Text.Length > 0 ? StatusText.Text + "  " + hint : hint);
    }

    /// <summary>Stops the document server, if it runs; <paramref name="why"/> goes into the message.</summary>
    private async Task StopServingAsync(string? why)
    {
        var host = _host;
        if (host == null) return;
        _host = null;
        _hostFollow?.Dispose();
        _hostFollow = null;
        UpdateServerState();
        try { await host.DisposeAsync(); }
        catch (Exception ex) { PluginLog.Debug($"Stopping the document server: {ex.Message}"); }
        var message = why == null ? L.T("Document server stopped.") : L.T("Document server stopped: {0}.", why);
        PluginLog.Info(message);
        SetStatus(message);
    }

    /// <summary>
    /// The clients a server offered to the network refused, to trust, and those
    /// it trusts, to keep or drop: checked means admitted.
    /// </summary>
    private void ServeClients_Click(object sender, RoutedEventArgs e) => Guard(L.T("Changing the trusted clients"), () =>
    {
        var refused = AmlServerHost.RejectedClients();
        var trusted = AmlServerHost.TrustedClients();
        if (refused.Count + trusted.Count == 0)
        {
            SetStatus(L.T("No client has tried to connect to the document server over the network yet."));
            return;
        }
        var rows = refused.Select(c => new ChecklistWindow.Row(L.T("{0}  (refused)", c.Subject), c, false, c.Thumbprint))
            .Concat(trusted.Select(c => new ChecklistWindow.Row(c.Subject, c, true, c.Thumbprint))).ToList();
        var window = new ChecklistWindow(L.T("Clients of the document server"),
            L.T("Offered to the network, the document server admits only clients whose certificate is checked here. Check a refused client only if you know it; compare its thumbprint with the one the client shows."), rows, "\uE72E")
        { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true) return;
        var admitted = window.Checked.Cast<ClientCertificate>().ToHashSet();
        foreach (var c in refused.Where(admitted.Contains)) { AmlServerHost.TrustClient(c); PluginLog.Info($"Trusted client {c.Subject} ({c.Thumbprint})."); }
        foreach (var c in trusted.Where(c => !admitted.Contains(c))) { AmlServerHost.DistrustClient(c); PluginLog.Info($"No longer trusted: {c.Subject} ({c.Thumbprint})."); }
        SetStatus(L.T("Trusted clients changed; they apply from a client's next connection."));
    });

    internal async Task DisconnectAsync()
    {
        // The commands go first: ending the subscriptions of a server that is gone
        // takes as long as the stack waits, and meanwhile nothing may use the session
        // or start a second disconnect.
        var client = _client;
        _client = null;
        AddressTree.Items.Clear();
        SetBusy(true, L.T("Disconnecting …"));
        try
        {
            await StopLiveAsync();
            if (_watch != null) { await _watch.DisposeAsync(); _watch = null; }
            _watchRows.Clear();
            WatchList.Visibility = Visibility.Collapsed;
            if (client != null) await client.DisposeAsync();
        }
        finally
        {
            SetBusy(false, null);
            UpdateServerState();
        }
        PluginLog.Info("Disconnected.");
        SetStatus(L.T("Disconnected."));
    }

    // ── address space tree ──────────────────────────────────────────────────

    private UaBrowseItem? SelectedNode => (AddressTree.SelectedItem as TreeViewItem)?.Tag as UaBrowseItem;

    private async void AddressTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        UpdateServerState();
        var version = ++_detailsVersion;
        NodeDetails.Children.Clear();
        NodeDetails.RowDefinitions.Clear();
        _shownValue = null;
        var node = SelectedNode;
        var client = _client;
        if (node == null || client == null) return;

        DetailRow(L.T("Name"), node.DisplayName, bold: true);
        DetailRow(L.T("Class"), node.NodeClass);
        DetailRow(L.T("NodeId"), node.Address.ToString(), mono: true, copy: true);
        if (node.BrowseName != node.DisplayName) DetailRow(L.T("BrowseName"), node.BrowseName);
        if (node.ReferenceType.Length > 0) DetailRow(L.T("Held by"), node.ReferenceType);
        var typeRow = node.TypeDefinition != null ? DetailRow(L.T("Type"), node.TypeDefinition.ToString(), mono: true) : null;
        var valueRow = node.NodeClass == "Variable" ? DetailRow(L.T("Value"), "…", mono: true) : null;
        if (valueRow != null) _shownValue = (node.Address, valueRow);
        var element = MirroredElement(node.Address);
        if (element != null) DetailRow(L.T("In document"), ElementPath(element), select: element);

        if (typeRow != null && node.TypeDefinition != null)
        {
            try
            {
                var type = await client.DescribeAsync(node.TypeDefinition);
                if (version != _detailsVersion) return;
                var inDocument = TypeIndex().ContainsKey(node.TypeDefinition with { ServerUri = null });
                typeRow.Text = inDocument ? type.DisplayName : L.T("{0}   (not imported)", type.DisplayName);
                typeRow.FontFamily = FontFamily;
                typeRow.ToolTip = node.TypeDefinition.ToString();
            }
            catch (Exception) { /* keep the NodeId */ }
        }
        if (valueRow != null)
        {
            try
            {
                var r = await client.ReadAsync(node.Address);
                if (version != _detailsVersion) return;
                valueRow.Text = r.Good ? $"{r.ValueText}" : r.Status;
                if (r.Good) valueRow.ClearValue(TextBlock.ForegroundProperty);
                else valueRow.Foreground = new SolidColorBrush(Color.FromRgb(0xD0, 0x40, 0x40));
                if (r.Good && r.DataType != null) DetailRow(L.T("DataType"), r.DataType);
            }
            catch (Exception ex) { valueRow.Text = ex.Message; }
            // The unit, where the server states one (EngineeringUnits, OPC 10000-8).
            try
            {
                var unit = await client.ReadEngineeringUnitsAsync(node.Address);
                if (version != _detailsVersion) return;
                if (unit != null)
                {
                    var unitRow = DetailRow(L.T("Unit"), unit.DisplayName);
                    if (unit.Description.Length > 0) unitRow.ToolTip = unit.Description;
                }
            }
            catch (Exception ex) { PluginLog.Info($"No unit read for {node.Address}: {ex.Message}"); }
        }
    }

    /// <summary>The variable whose value the node details show, and where; null for none.</summary>
    private (UaNodeAddress Address, TextBlock Text)? _shownValue;

    /// <summary>
    /// A live value of the variable the node details show: the same number as
    /// in the document, not the one read when it was selected. Live updates
    /// arrive on the UI thread.
    /// </summary>
    private void ShowLiveValue(LiveUpdate update)
    {
        if (_shownValue is not { } shown || update.Address is not { } changed) return;
        if (changed with { ServerUri = null } != shown.Address with { ServerUri = null }) return;
        shown.Text.Text = update.Value ?? "";
        shown.Text.ClearValue(TextBlock.ForegroundProperty);
    }

    /// <summary>Adds a label and a value to the node details; returns the value's text block to fill in later.</summary>
    private TextBlock DetailRow(string label, string value, bool mono = false, bool bold = false, bool copy = false, InternalElementType? select = null)
    {
        var row = NodeDetails.RowDefinitions.Count;
        NodeDetails.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var name = new TextBlock { Text = label, Foreground = ThemePalette.Current(this).Muted, Margin = new Thickness(0, 2, 12, 2) };
        Grid.SetRow(name, row);
        NodeDetails.Children.Add(name);
        var text = new TextBlock
        {
            Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2),
            FontFamily = mono ? new FontFamily("Consolas") : FontFamily,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        };
        var cell = new DockPanel();
        if (copy)
        {
            var button = new Button
            {
                Content = new TextBlock { FontFamily = new FontFamily("Segoe MDL2 Assets"), Text = "\uE8C8", FontSize = 11 },
                ToolTip = L.T("Copy the {0}", label), Padding = new Thickness(3, 1, 3, 1), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top,
            };
            System.Windows.Automation.AutomationProperties.SetName(button, L.T("Copy the {0}", label));
            button.Click += (_, __) => CopyText(value);
            DockPanel.SetDock(button, Dock.Right);
            cell.Children.Add(button);
        }
        if (select != null)
        {
            var link = new Button
            {
                Content = L.T("Select"), Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top,
                ToolTip = L.T("Select the element in the editor"),
            };
            link.Click += (_, __) => Selected?.Invoke(this, new SelectionEventArgs(select));
            DockPanel.SetDock(link, Dock.Right);
            cell.Children.Add(link);
        }
        cell.Children.Add(text);
        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, 1);
        NodeDetails.Children.Add(cell);
        return text;
    }

    /// <summary>The element that mirrors a node in the hierarchy named under 'into', if any.</summary>
    private InternalElementType? MirroredElement(UaNodeAddress address) =>
        TakenIndex().TryGetValue(address with { ServerUri = null }, out var e) ? e : null;

    private static string ElementPath(InternalElementType e)
    {
        var parts = new List<string>();
        for (CAEXBasicObject? o = e; o is CAEXObject c; o = c.CAEXParent as CAEXBasicObject)
        {
            parts.Insert(0, c.Name);
            if (c is InstanceHierarchyType) break;
        }
        return string.Join("/", parts);
    }

    // ── into the document ───────────────────────────────────────────────────

    /// <summary>Where NodeSets fetched from servers are kept, one folder per server.</summary>
    internal static string ServerNodeSetsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AMLOpcUa", "server-nodesets");

    private async void ServerTypesButton_Click(object sender, RoutedEventArgs e)
    {
        var client = _client;
        if (client == null || _busy) return;
        IReadOnlyList<ServerNamespace> namespaces;
        SetBusy(true, L.T("Reading the server's namespaces …"));
        try
        {
            namespaces = await ServerNodeSets.ListAsync(client);
        }
        catch (Exception ex)
        {
            PluginLog.Error("Reading the namespaces failed", ex);
            SetStatus(L.T("Reading the namespaces failed: {0}", ex.Message));
            return;
        }
        finally
        {
            SetBusy(false, null);
        }

        var picker = new NamespacePickerWindow(namespaces.Where(n => n.Index > 0).ToList(), _document != null) { Owner = Window.GetWindow(this) };
        if (picker.ShowDialog() != true || picker.Selected is not { } ns) return;

        var folder = Path.Combine(ServerNodeSetsFolder, SafeFileName(client.ServerUri ?? client.EndpointUrl));
        ServerNodeSetFiles files;
        SetBusy(true, L.T("Reading the NodeSet of {0} …", ns.Uri));
        // Rebuilding a large namespace by browsing takes a while; it writes files only.
        var token = BeginCancellable();
        try
        {
            Directory.CreateDirectory(ModelsFolder);
            var catalog = NodeSetCatalog.Create(KnownNodeSetFolders(_settings.NodeSetFolders, ModelsFolder, CloudCache, OpcfFolder, ServerNodeSetsFolder, ImportedFolder));
            files = await ServerNodeSets.FetchForImportAsync(client, ns.Uri, catalog, folder,
                new ServerNodeSetOptions { IncludeInstances = picker.IncludeInstances }, token);
            foreach (var set in files.NodeSets)
            {
                PluginLog.Info($"{set.ModelUri}: {set.NodeCount} node(s) from the "
                               + (set.Source == ServerNodeSetSource.NamespaceFile ? "file the server publishes." : "server's address space (rebuilt by browsing)."));
                foreach (var note in set.Notes) PluginLog.Info($"{set.ModelUri}: {note}");
            }
        }
        // Closed meanwhile: its session ended with it; there is no one to tell.
        catch (Exception) when (_released)
        {
            return;
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            PluginLog.Info($"Reading the NodeSet of {ns.Uri} cancelled.");
            SetStatus(L.T("Reading the NodeSet of {0} cancelled.", ns.Uri));
            return;
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Reading the NodeSet of {ns.Uri} failed", ex);
            SetStatus(L.T("Reading the NodeSet of {0} failed: {1}", ns.Uri, ex.Message));
            return;
        }
        finally
        {
            EndCancellable();
            SetBusy(false, null);
        }
        // Closed or disconnected while reading: the files are written, and nothing more is done with them.
        if (_released || !ReferenceEquals(_client, client)) return;

        try
        {
            switch (picker.Action)
            {
                case NamespaceAction.Import when _document is { } document:
                    await ImportFileAsync(document, files.Paths[0], new[] { folder, ModelsFolder });
                    break;
                case NamespaceAction.Modeler:
                    if (!MayReplaceModel()) return;
                    Tabs.SelectedItem = ModelerTab;
                    if (!await EnsureModelerAsync()) return;
                    if (NodeSetInfo.TryRead(files.Paths[0]) is { } info) OpenInModeler(info, ModelerCatalog(folder));
                    else SetStatus(L.T("The NodeSet of {0} could not be read back from {1}.", ns.Uri, files.Paths[0]));
                    break;
                case NamespaceAction.Save:
                    var dialog = new SaveFileDialog
                    {
                        Title = L.T("Save the NodeSet of {0}", ns.Uri),
                        Filter = L.T("OPC UA NodeSet (*.xml)|*.xml"),
                        FileName = Path.GetFileName(files.Paths[0]),
                    };
                    if (dialog.ShowDialog() != true) return;
                    File.Copy(files.Paths[0], dialog.FileName, true);
                    SetStatus(L.T("Saved the NodeSet of {0} to {1}.", ns.Uri, dialog.FileName));
                    break;
            }
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Taking the NodeSet of {ns.Uri} failed", ex);
            SetStatus(L.T("Taking the NodeSet of {0} failed: {1}", ns.Uri, ex.Message));
        }
    }

    private void BindButton_Click(object sender, RoutedEventArgs e) => Guard(L.T("Binding"), () =>
    {
        var node = SelectedNode;
        var document = _document;
        var client = _client;
        if (node == null || document == null || client == null) return;

        var picker = new ElementPickerWindow(document, L.T("Bind {0} to", node.DisplayName)) { Owner = Window.GetWindow(this) };
        if (picker.ShowDialog() != true || picker.Selected is not { } element) return;

        var bound = node.Address with { ServerUri = client.ServerUri };
        UaNodeAddress? before = null;
        try { before = AnnexANodeId.Of(element); }
        catch (AddressingException) { /* an alias or browse path; replaced below after asking */ }
        var hasNodeId = element.Attribute["NodeId"] != null;
        if (hasNodeId && before != bound
            && !DialogKit.Confirm(Window.GetWindow(this), "\uE71B", DialogKit.Relate, L.T("Replace the binding?"),
                L.T("'{0}' is bound to another node already. Binding it to {1} replaces that NodeId.", element.Name, node.DisplayName),
                L.T("Replace"),
                DialogKit.Facts((L.T("Now"), before?.ToString() ?? L.T("(a browse path or alias)"), true), (L.T("New"), bound.ToString(), true)),
                risky: true))
            return;

        Bind(element, bound, node.NodeClass);
        var message = $"Bound {node.Address} to '{element.Name}'.";
        PluginLog.Info(message + (before != null && before != bound ? $" It was bound to {before}." : ""));
        SetStatus(L.T("Bound {0} to '{1}'.", node.Address, element.Name) + L.T(" Press Ctrl+S to save."));
        Selected?.Invoke(this, new SelectionEventArgs(element));
    });

    /// <summary>Write value…: the selected variable's type and value now, a new value typed and checked, then written.</summary>
    private async void WriteButton_Click(object sender, RoutedEventArgs e)
    {
        var node = SelectedNode;
        var client = _client;
        if (node is not { NodeClass: "Variable" } || client == null || _busy) return;
        UaVariableInfo info;
        UaReadResult now;
        // Busy while it reads, so no other command starts meanwhile and has its busy state cleared.
        SetBusy(true, L.T("Reading '{0}' …", node.DisplayName));
        try
        {
            info = await client.DescribeVariableAsync(node.Address);
            now = await client.ReadAsync(node.Address);
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Reading {node.Address} before writing failed", ex);
            SetStatus(L.T("Reading '{0}' failed: {1}", node.DisplayName, ex.Message));
            return;
        }
        finally { SetBusy(false, null); }
        if (_released || !ReferenceEquals(_client, client)) return;
        if (!info.Writable)
        {
            SetStatus(L.T("The server lets '{0}' only be read.", node.DisplayName));
            return;
        }
        var window = new WriteValueWindow(node.DisplayName, node.Address.ToString(), info, now.Good ? ValueEntry.ToText(info.BuiltInType, info.ValueRank, now.Value) : "") { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true) return;
        var text = window.Input.Text;
        SetBusy(true, L.T("Writing '{0}' …", node.DisplayName));
        try
        {
            var result = await client.WriteValueAsync(node.Address, text);
            PluginLog.Info($"Write {text} to {node.Address}: {result.Status}.");
            SetStatus(result.Good ? L.T("Wrote {0} into '{1}'.", text, node.DisplayName) : L.T("The server did not write '{0}': {1}", node.DisplayName, result.Status));
            if (result.Good && _shownValue is { } shown && shown.Address == node.Address)
            {
                var back = await client.ReadAsync(node.Address);
                if (back.Good) shown.Text.Text = back.ValueText ?? "";
            }
        }
        catch (ValueEntryException ex) { SetStatus(ValueTexts.Describe(ex)); }
        catch (Exception ex)
        {
            PluginLog.Error($"Writing {node.Address} failed", ex);
            SetStatus(L.T("Writing '{0}' failed: {1}", node.DisplayName, ex.Message));
        }
        finally { SetBusy(false, null); }
    }

    /// <summary>Call method…: the selected method on the object above it in the address space, with typed arguments.</summary>
    private async void CallButton_Click(object sender, RoutedEventArgs e)
    {
        var item = AddressTree.SelectedItem as TreeViewItem;
        var client = _client;
        if (item?.Tag is not UaBrowseItem { NodeClass: "Method" } method || client == null || _busy) return;
        if ((item.Parent as TreeViewItem)?.Tag is not UaBrowseItem target)
        {
            SetStatus(L.T("'{0}' has no object above it in the address space to be called on.", method.DisplayName));
            return;
        }
        UaMethodInfo info;
        SetBusy(true, L.T("Reading '{0}' …", method.DisplayName));
        try { info = await client.DescribeMethodAsync(method.Address); }
        catch (Exception ex)
        {
            PluginLog.Error($"Reading the arguments of {method.Address} failed", ex);
            SetStatus(L.T("Reading the arguments of '{0}' failed: {1}", method.DisplayName, ex.Message));
            return;
        }
        finally { SetBusy(false, null); }
        if (_released || !ReferenceEquals(_client, client)) return;
        var window = new CallMethodWindow(method.DisplayName, target.DisplayName, info, async inputs =>
        {
            var answer = await client.CallAsync(target.Address, method.Address, inputs);
            PluginLog.Info($"Call {method.Address} on {target.Address} ({string.Join(", ", inputs)}): {answer.Status}"
                           + string.Concat(answer.Outputs.Select(o => $"; {o.Name} = {o.Value}")));
            return answer;
        }) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
    }

    /// <summary>
    /// Bind model…: the variables below the selected object (the whole server
    /// without one) are read, a variable is proposed for each value of a model
    /// of the user's own, and what is ticked in the dialog is bound.
    /// </summary>
    private async void BindModelButton_Click(object sender, RoutedEventArgs e)
    {
        var document = _document;
        var client = _client;
        if (document == null || client == null) return;
        var models = ModelBindRows.Models(document).ToList();
        // An element of one's own selected in the editor, of this document, is the model first offered.
        if (_editorSelection is InternalElementType selected && ReferenceEquals(selected.CAEXDocument, document)
            && ModelBindRows.IsOwn(selected) && selected.InternalElement.Any())
            models.Insert(0, selected);
        if (models.Count == 0)
        {
            SetStatus(L.T("The document has no model of your own to bind: an InstanceHierarchy with an element for each value."));
            return;
        }
        var start = SelectedNode is { NodeClass: "Object" } node ? node : null;
        SetBusy(true, start == null ? L.T("Reading the server's variables …") : L.T("Reading the variables below {0} …", start.DisplayName));
        var token = BeginCancellable();
        CollectedVariables found;
        try
        {
            found = await ServerVariables.CollectAsync(client, start?.Address, ct: token);
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            SetStatus(L.T("Reading the server's variables cancelled."));
            return;
        }
        catch (Exception ex)
        {
            PluginLog.Error("Reading the server's variables failed", ex);
            SetStatus(L.T("Reading the server's variables failed: {0}", ex.Message));
            return;
        }
        finally
        {
            EndCancellable();
            SetBusy(false, null);
        }
        // Another document opened, the plugin closed or the server left while reading:
        // the models are those of a document no longer open.
        if (_released || !ReferenceEquals(_document, document) || !ReferenceEquals(_client, client)) return;
        if (found.Variables.Count == 0)
        {
            SetStatus(start == null ? L.T("The server has no variables to bind.") : L.T("The server has no variables below {0}.", start.DisplayName));
            return;
        }
        if (found.Unbrowsed > 0)
        {
            PluginLog.Warn($"Bind model: {found.Unbrowsed} node(s) could not be browsed.");
            SetStatus(L.T("{0} node(s) of the server could not be browsed; the proposals may lack what is below them.", found.Unbrowsed));
        }

        var window = new ModelBindWindow(models, models[0], found, start?.DisplayName ?? L.T("the whole server")) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true) return;
        int bound;
        try { bound = ModelBinder.Apply(window.Kept, client.ServerUri); }
        catch (Exception ex)
        {
            PluginLog.Error("Binding the model failed", ex);
            SetStatus(L.T("Binding the model failed: {0}", ex.Message));
            return;
        }
        foreach (var p in window.Kept) PluginLog.Info($"Bound {p.Variable!.Address} to '{p.Signal.PathText}'.");
        SetStatus(L.T("Bound {0} value(s) of '{1}' to the server.", bound, window.ModelName) + L.T(" Press Ctrl+S to save."));
        UpdateServerState();
    }

    /// <summary>
    /// Gives <paramref name="element"/> the node's address as its NodeId attribute
    /// (Annex A), and for a variable a Value attribute: reading and keeping values
    /// live take only elements that have one, so a variable bound without it was
    /// never read.
    /// </summary>
    internal static void Bind(InternalElementType element, UaNodeAddress bound, string nodeClass)
    {
        AnnexANodeId.Write(element, bound);
        if (nodeClass == "Variable" && element.Attribute["Value"] == null) element.Attribute.Append("Value");
    }

    private async void SnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        var document = _document;
        if (document == null || _client == null) return;
        SetBusy(true, L.T("Reading current values …"));
        var token = BeginCancellable();
        try
        {
            // All values are read before any is written: cancelled while reading, the document is as it was.
            var result = await ValueSnapshot.ApplyAsync(document, _client, token);
            foreach (var p in result.Problems) PluginLog.Warn(p);
            PluginLog.Info($"Snapshot from {_client.EndpointUrl}: {result}.");
            SetStatus(Summaries.Snapshot(result, _client.EndpointUrl));
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            SetStatus(L.T("Reading current values cancelled."));
        }
        catch (Exception ex)
        {
            PluginLog.Error("Snapshot failed", ex);
            SetStatus(L.T("Snapshot failed: {0}", ex.Message));
        }
        finally
        {
            EndCancellable();
            SetBusy(false, null);
        }
    }

    private async void LiveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_live != null)
        {
            await StopLiveAsync();
            SetStatus(L.T("Live values stopped. Press Ctrl+S to save the last values."));
            return;
        }
        var document = _document;
        if (document == null || _client == null || _liveStarting) return;
        // A second click while the subscription is set up would start a second one.
        _liveStarting = true;
        UpdateServerState();
        try
        {
            // Writes run on the UI thread, and only while the same document is open.
            var token = new object();
            _liveToken = token;
            var client = _client;
            var live = await LiveValues.FollowAsync(document, client,
                write => Dispatcher.BeginInvoke(() => { if (ReferenceEquals(_document, document) && ReferenceEquals(_liveToken, token)) write(); }),
                update =>
                {
                    ShowLiveValue(update);
                    if (!_statusLine.MayShowLive(DateTime.Now)) return;
                    ShowStatus(L.T("Live: {0} = {1}  ·  {2} value(s) written.", update.What, update.Value, update.Updates));
                });
            // Closed, disconnected or another document opened meanwhile: the subscription
            // would stay with nothing to stop it.
            if (_released || !ReferenceEquals(_document, document) || !ReferenceEquals(_client, client))
            {
                await live.DisposeAsync();
                return;
            }
            _live = live;
            foreach (var p in _live.Problems) PluginLog.Warn(p);
            var message = LiveStartText(_live.Count, _live.Skipped, _live.NotOnServer, _client.EndpointUrl);
            PluginLog.Info(message);
            SetStatus(message);
            if (_live.Count == 0) await StopLiveAsync();
            else StartLiveCheck();
        }
        catch (Exception ex)
        {
            PluginLog.Error("Live values failed", ex);
            SetStatus(L.T("Live values failed: {0}", ex.Message));
            await StopLiveAsync();
        }
        finally
        {
            _liveStarting = false;
            // Also when it ended early (another document opened meanwhile): the command is offered again.
            UpdateServerState();
        }
    }

    /// <summary>
    /// An element bound, copied or deleted while values are live is followed or
    /// dropped within two seconds; the editor tells a plugin of no change, so the
    /// plugin looks. Looking is reading the document, cheap; only a change subscribes anew.
    /// </summary>
    private void StartLiveCheck()
    {
        _liveCheck?.Stop();
        _liveCheck = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _liveCheck.Tick += LiveCheck_Tick;
        _liveCheck.Start();
    }

    private async void LiveCheck_Tick(object? sender, EventArgs e)
    {
        var live = _live;
        if (live == null || _liveStarting || _liveChecking || _busy || _document == null) return;
        _liveChecking = true;
        try
        {
            var change = await live.FollowChangesAsync();
            if (change == null) return;
            if (!ReferenceEquals(_live, live))
            {
                // Stopped meanwhile: the new subscription has no one to stop it.
                await change.Live.DisposeAsync();
                return;
            }
            _live = change.Live;
            foreach (var p in _live.Problems) PluginLog.Warn(p);
            var text = LiveChangeText(change.Added, change.Removed, _live.Count, _live.NotOnServer);
            PluginLog.Info(text);
            var followed = _live;
            if (_live.Count == 0) await StopLiveAsync();
            // What the user's last command said is read first: the result of
            // Take into document was gone after 1.7 s (editor probe all11c).
            await _statusLine.WaitTillReadAsync(() => DateTime.Now, Task.Delay);
            if (followed.Count == 0)
            {
                if (_live == null) SetStatus(Summaries.NothingBound);
                UpdateServerState();
            }
            else if (ReferenceEquals(_live, followed)) SetStatus(text);
        }
        catch (Exception ex)
        {
            PluginLog.Error("Following changed bindings failed", ex);
        }
        finally
        {
            _liveChecking = false;
        }
    }

    /// <summary>What the status line says when keeping values live takes up changed bindings.</summary>
    internal static string LiveChangeText(IReadOnlyList<string> added, IReadOnlyList<string> removed, int count, int notOnServer = 0)
    {
        var moved = added.Intersect(removed).ToList();
        string Names(IReadOnlyList<string> names) => names.Count <= 3 ? string.Join(", ", names) : L.T("{0} element(s)", names.Count);
        var parts = new List<string>();
        var newOnes = added.Except(moved).ToList();
        var gone = removed.Except(moved).ToList();
        if (newOnes.Count > 0) parts.Add(L.T("now also {0}", Names(newOnes)));
        if (gone.Count > 0) parts.Add(L.T("no longer {0}", Names(gone)));
        if (moved.Count > 0) parts.Add(L.T("{0} at another node", Names(moved)));
        // A bound element whose node the server lacks is named in the log, and counted here as at the start.
        var lacking = notOnServer > 0 ? L.T("; {0} not on this server (see log)", notOnServer) : "";
        return L.T("Live: {0}  ·  {1} value(s) followed{2}.", string.Join("; ", parts), count, lacking);
    }

    private async Task StopLiveAsync()
    {
        _liveCheck?.Stop();
        _liveCheck = null;
        var live = _live;
        _live = null;
        _liveToken = null;
        if (live != null)
        {
            PluginLog.Info($"Live values stopped after {live.Updates} value(s).");
            await live.DisposeAsync();
        }
        UpdateServerState();
    }

    /// <summary>"SignAndEncrypt Basic256Sha256" as "signed and encrypted, Basic256Sha256"; "None None" as "not secured".</summary>
    private static string SecurityText(string mode)
    {
        var parts = mode.Split(' ', 2);
        return parts[0] switch
        {
            "None" => L.T("not secured"),
            "Sign" => parts.Length > 1 ? L.T("signed, {0}", parts[1]) : L.T("signed"),
            "SignAndEncrypt" => parts.Length > 1 ? L.T("signed and encrypted, {0}", parts[1]) : L.T("signed and encrypted"),
            _ => mode,
        };
    }

    private void UpdateServerState()
    {
        var connected = _client != null;
        AddressTreeEmpty.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        // Not while connecting or while an operation uses the connection.
        ConnectButton.IsEnabled = !_busy;
        ConnectText.Text = connected ? L.T("Disconnect") : L.T("Connect");
        ConnectGlyph.Text = connected ? "\uE711" : "\uE703";
        EndpointBox.IsEnabled = !connected;
        FindServersButton.IsEnabled = !connected && !_busy;
        SecurityToggle.IsEnabled = !connected;
        UserBox.IsEnabled = !connected;
        PasswordBox.IsEnabled = !connected;
        ServerTypesButton.IsEnabled = connected && !_busy;
        // A method is shown to be called; binding, counting and taking are for objects and variables.
        var hasNode = connected && SelectedNode is { NodeClass: not "Method" } && _document != null && !_busy;
        MirrorButton.IsEnabled = connected && _document != null && !_busy
                                 && (_items.Count > 0 || SelectedNode is { NodeClass: not "Method" });
        InstancesOfButton.IsEnabled = connected && !_busy;
        LoadSelectionButton.IsEnabled = connected && _document != null && !_busy;
        PreviewButton.IsEnabled = connected && !_busy && (_items.Count > 0 || SelectedNode is { NodeClass: not "Method" });
        CheckServerButton.IsEnabled = connected && !_busy;
        NamespacesButton.IsEnabled = connected && !_busy;
        RemoveSelectionButton.IsEnabled = SelectionList.SelectedItem != null;
        ClearSelectionButton.IsEnabled = _items.Count > 0 || _excluded.Count > 0;
        BindButton.IsEnabled = hasNode;
        BindModelButton.IsEnabled = connected && _document != null && !_busy;
        SnapshotButton.IsEnabled = connected && _document != null && !_busy;
        LiveButton.IsEnabled = !_liveStarting && (_live != null || (connected && _document != null && !_busy));
        LiveText.Text = _live != null ? L.T("Stop keeping values live") : L.T("Keep document values live");
        LiveGlyph.Text = _live != null ? "\uE71A" : "\uE9D9";
        WatchButton.IsEnabled = connected && SelectedNode?.NodeClass == "Variable";
        WriteButton.IsEnabled = connected && !_busy && SelectedNode?.NodeClass == "Variable";
        CallButton.IsEnabled = connected && !_busy && SelectedNode?.NodeClass == "Method";
        UnwatchButton.IsEnabled = connected && _watchRows.Count > 0;
        SaveWatchButton.IsEnabled = _watchRows.Count > 0 || _recording.Count > 0;
        ServeText.Text = _host != null ? L.T("Stop serving") : L.T("Serve");
        ServeGlyph.Text = _host != null ? "\uE71A" : "\uE768";
        ServeGlyph.Foreground = _host != null ? new SolidColorBrush(Color.FromRgb(0xC0, 0x30, 0x30)) : (Brush)FindResource("Create");
        ServeButton.IsEnabled = (_host != null || _document != null) && !_busy;
        ServePortBox.IsEnabled = _host == null;
        ServeNetworkBox.IsEnabled = _host == null;
        ServeSimulateBox.IsEnabled = _host == null;
        ServeTypesBox.IsEnabled = _host == null;
        UpdatePracticePlantButton();
        var lost = connected && !_client!.Reachable;
        ServerStateText.Text = (lost ? L.T("Connection lost") : connected ? L.T("Connected  ·  {0}", SecurityText(_client!.SecurityMode)) : L.T("Not connected"))
                               + (_host != null ? L.T("  ·  serving at {0}", _host.EndpointUrl) : "");
        ConnectionDot.Fill = new SolidColorBrush(lost ? Color.FromRgb(0xD1, 0x34, 0x38) : connected ? Color.FromRgb(0x2E, 0x9E, 0x4F) : Color.FromRgb(0x9A, 0xA0, 0xA6));
        var palette = ThemePalette.Current(this);
        ConnectionPill.Background = connected ? palette.PillOn : palette.PillIdle;
        ConnectionPill.ToolTip = connected ? _client!.EndpointUrl : null;
    }

    /// <summary>
    /// Saves a copy of the open document without the addresses of its servers,
    /// to pass it on; the open document stays as it is.
    /// </summary>
    /// <summary>"plant.amlx" gives "plant-without-addresses.amlx", in the same folder and of the same kind.</summary>
    internal static string CopyWithoutAddressesName(string documentPath) =>
        Path.Combine(Path.GetDirectoryName(documentPath) ?? "",
            Path.GetFileNameWithoutExtension(documentPath) + "-without-addresses" + Path.GetExtension(documentPath));

    /// <summary>
    /// Writes <paramref name="xml"/> (a copy of the document) without server addresses to
    /// <paramref name="target"/>; into a copy of the container when both are .amlx.
    /// </summary>
    internal static StripReport SaveCopyWithoutAddresses(System.Xml.Linq.XDocument xml, string? documentPath, string target)
    {
        var report = ServerAddresses.Strip(xml);
        if (documentPath != null && AmlContainers.IsContainerPath(documentPath) && AmlContainers.IsContainerPath(target) && File.Exists(documentPath))
            AmlContainers.CopyWithRoot(documentPath, xml, target);
        else
            xml.Save(target);
        return report;
    }

    private void StripAddressesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_document is not { } document)
        {
            SetStatus(L.T("Open a document first."));
            return;
        }
        var suggested = CopyWithoutAddressesName(_documentPath ?? "Document.aml");
        var container = AmlContainers.IsContainerPath(suggested);
        var dialog = new SaveFileDialog
        {
            Title = L.T("Save a copy without server addresses"),
            // A container is passed on as one, with its libraries and geometry.
            Filter = container ? L.T("AutomationML container (*.amlx)|*.amlx") : L.T("AutomationML (*.aml)|*.aml"),
            FileName = Path.GetFileName(suggested),
            InitialDirectory = _documentPath != null ? Path.GetDirectoryName(_documentPath) : null,
        };
        if (dialog.ShowDialog() != true) return;
        var documentPath = _documentPath;
        Guard(L.T("Saving the copy without server addresses"), () =>
        {
            var report = SaveCopyWithoutAddresses(new System.Xml.Linq.XDocument(document.CAEXFile.Node.Document!), documentPath, dialog.FileName);
            PluginLog.Info($"Copy without server addresses: {dialog.FileName}: {report}");
            foreach (var place in report.Remaining) PluginLog.Warn($"A host name still appears in the copy: {place}");
            SetStatus(L.T("Saved {0}: {1}.", Path.GetFileName(dialog.FileName), report)
                + (report.Remaining.Count > 0 ? L.T(" The log names each place.") : ""));
        });
    }
}
