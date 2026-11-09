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
using OpcUaAml.NodeSets;
using OpcUaAml.Server;

namespace Aml.Editor.Plugin.OpcUa;

public partial class OpcUaPlugin : ISupportsSelection
{
    private UaClient? _client;
    private IAsyncDisposable? _watch;
    private LiveValues? _live;
    private DateTime _liveStatusShown;
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
    private int _detailsVersion;
    private readonly System.Collections.ObjectModel.ObservableCollection<WatchRow> _watchRows = new();

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

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public void Update(UaReadResult r)
        {
            Value = r.ValueText ?? "";
            Status = r.Good ? "Good" : r.Status;
            Good = r.Good;
            Received = DateTime.Now.ToString("HH:mm:ss");
            PropertyChanged?.Invoke(this, new(null));
        }
    }

    /// <summary>Asks the editor to select an element in its tree (after mirroring or binding).</summary>
    public event EventHandler<SelectionEventArgs>? Selected;

    private void InitServerTab()
    {
        EndpointBox.ItemsSource = _settings.RecentEndpoints;
        EndpointBox.Text = _settings.LastEndpointUrl ?? "opc.tcp://localhost:4840";
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
        try
        {
            _client = await ConnectWithTrustPromptAsync(url);
            var client = _client;
            client.ReachableChanged += reachable => Dispatcher.BeginInvoke(() =>
            {
                if (!ReferenceEquals(_client, client)) return;
                if (reachable) PluginLog.Info($"{url} answers again.");
                else PluginLog.Warn($"{url} does not answer any more.");
                SetStatus(reachable ? L.T("{0} answers again.", url) : L.T("{0} does not answer any more. Disconnect, and connect again once it is back.", url));
                UpdateServerState();
            });
            PluginLog.Info($"Connected to {url} ({_client.SecurityMode}); {_client.NamespaceTable.Count} namespaces.");
            SetStatus(L.T("Connected to {0}.", url));
            RememberEndpoint(url);
            await LoadRootAsync();
        }
        catch (UaConnectionException ex)
        {
            PluginLog.Error(ex.Message);
            SetStatus(ex.Message);
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Connecting to {url} failed", ex);
            SetStatus(L.T("Connecting to {0} failed: {1}", url, ex.Message));
        }
        finally
        {
            SetBusy(false, null);
            UpdateServerState();
        }
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
        foreach (var part in new FrameworkElement[] { ConnectionPill, Credentials, ConnectCommands }) part.Measure(unbounded);
        var oneLine = ConnectionPill.DesiredSize.Width + EndpointRoom + Credentials.DesiredSize.Width + ConnectCommands.DesiredSize.Width;
        var twoLines = oneLine > width;
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
    private async Task<UaClient> ConnectWithTrustPromptAsync(string url)
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
            return await UaClient.ConnectAsync(options);
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
            if (!trust) throw;
            UaClient.TrustServer(certificate, options.PkiRoot);
            PluginLog.Info($"Trusted the certificate of {url}: {certificate.Subject}, SHA-256 {certificate.Sha256}.");
            return await UaClient.ConnectAsync(options);
        }
    }

    private async void WatchButton_Click(object sender, RoutedEventArgs e)
    {
        var node = SelectedNode;
        if (node == null || _client == null || node.NodeClass != "Variable") return;
        if (_watchRows.Any(r => r.Address == node.Address)) return;
        _watchRows.Add(new WatchRow { Address = node.Address, Node = node.DisplayName });
        await RestartWatchAsync();
    }

    private async void UnwatchButton_Click(object sender, RoutedEventArgs e)
    {
        _watchRows.Clear();
        await RestartWatchAsync();
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
            if (_client == null || _watchRows.Count == 0) return;
            var rows = _watchRows.ToDictionary(r => r.Address);
            _watch = await _client.WatchAsync(rows.Keys.ToList(), r =>
                Dispatcher.BeginInvoke(() => { if (rows.TryGetValue(r.Address, out var row)) row.Update(r); }));
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
        SetBusy(true, L.T("Starting the document server …"));
        try
        {
            _host = await AmlServerHost.StartAsync(document, new AmlServerOptions { Port = port, Network = network, Simulate = simulate });
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
                PluginLog.Info("Elements were added or removed; restart serving to show them.");
                SetStatus(L.T("The document server shows the values as they change; restart it to show added or removed elements."));
            });
            var message = (network, simulate) switch
            {
                (true, true) => L.T("Serving {0} node(s) of this document at {1}, to other computers too, to trusted clients only (Clients…), with simulated values.", _host.Nodes, _host.EndpointUrl),
                (true, false) => L.T("Serving {0} node(s) of this document at {1}, to other computers too, to trusted clients only (Clients…).", _host.Nodes, _host.EndpointUrl),
                (false, true) => L.T("Serving {0} node(s) of this document at {1}, to this computer only, with simulated values.", _host.Nodes, _host.EndpointUrl),
                _ => L.T("Serving {0} node(s) of this document at {1}, to this computer only.", _host.Nodes, _host.EndpointUrl),
            };
            PluginLog.Info(message);
            SetStatus(message);
            if (_client == null) EndpointBox.Text = _host.EndpointUrl;
        }
        catch (Exception ex)
        {
            PluginLog.Error("Starting the document server failed", ex);
            SetStatus(L.T("Starting the document server failed: {0}", ex.Message));
        }
        finally
        {
            SetBusy(false, null);
            UpdateServerState();
        }
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

    private async Task DisconnectAsync()
    {
        await StopLiveAsync();
        if (_watch != null) { await _watch.DisposeAsync(); _watch = null; }
        _watchRows.Clear();
        WatchList.Visibility = Visibility.Collapsed;
        var client = _client;
        _client = null;
        AddressTree.Items.Clear();
        UpdateServerState();
        if (client != null) await client.DisposeAsync();
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
        }
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
        try
        {
            Directory.CreateDirectory(ModelsFolder);
            var catalog = NodeSetCatalog.Create(new[] { ModelsFolder }.Concat(_settings.NodeSetFolders));
            files = await ServerNodeSets.FetchForImportAsync(client, ns.Uri, catalog, folder,
                new ServerNodeSetOptions { IncludeInstances = picker.IncludeInstances });
            foreach (var set in files.NodeSets)
            {
                PluginLog.Info($"{set.ModelUri}: {set.NodeCount} node(s) from the "
                               + (set.Source == ServerNodeSetSource.NamespaceFile ? "file the server publishes." : "server's address space (rebuilt by browsing)."));
                foreach (var note in set.Notes) PluginLog.Info($"{set.ModelUri}: {note}");
            }
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Reading the NodeSet of {ns.Uri} failed", ex);
            SetStatus(L.T("Reading the NodeSet of {0} failed: {1}", ns.Uri, ex.Message));
            return;
        }
        finally
        {
            SetBusy(false, null);
        }

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
                    await EnsureModelerAsync();
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

        AnnexANodeId.Write(element, bound);
        var message = $"Bound {node.Address} to '{element.Name}'.";
        PluginLog.Info(message + (before != null && before != bound ? $" It was bound to {before}." : ""));
        SetStatus(L.T("Bound {0} to '{1}'.", node.Address, element.Name) + L.T(" Press Ctrl+S to save."));
        Selected?.Invoke(this, new SelectionEventArgs(element));
    });

    private async void SnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        var document = _document;
        if (document == null || _client == null) return;
        SetBusy(true, L.T("Reading current values …"));
        try
        {
            var result = await ValueSnapshot.ApplyAsync(document, _client);
            foreach (var p in result.Problems) PluginLog.Warn(p);
            PluginLog.Info($"Snapshot from {_client.EndpointUrl}: {result}.");
            SetStatus(L.T("Snapshot: {0}.", result) + (result.Read > 0 ? L.T(" Press Ctrl+S to save.") : ""));
        }
        catch (Exception ex)
        {
            PluginLog.Error("Snapshot failed", ex);
            SetStatus(L.T("Snapshot failed: {0}", ex.Message));
        }
        finally
        {
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
            _live = await LiveValues.FollowAsync(document, _client,
                write => Dispatcher.BeginInvoke(() => { if (ReferenceEquals(_document, document) && ReferenceEquals(_liveToken, token)) write(); }),
                update =>
                {
                    if ((DateTime.Now - _liveStatusShown).TotalSeconds < 1) return;
                    _liveStatusShown = DateTime.Now;
                    SetStatus(L.T("Live: {0} = {1}  ·  {2} value(s) written.", update.What, update.Value, update.Updates));
                });
            foreach (var p in _live.Problems) PluginLog.Warn(p);
            var message = _live.Skipped > 0
                ? L.T("Following {0} bound value(s) of {1}; {2} bound to another server left out.", _live.Count, _client.EndpointUrl, _live.Skipped)
                : L.T("Following {0} bound value(s) of {1}.", _live.Count, _client.EndpointUrl);
            PluginLog.Info(message);
            SetStatus(_live.Count == 0 ? L.T("No element of this document is bound to this server.") : message);
            if (_live.Count == 0) await StopLiveAsync();
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
        }
        UpdateServerState();
    }

    private async Task StopLiveAsync()
    {
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
        SecurityToggle.IsEnabled = !connected;
        UserBox.IsEnabled = !connected;
        PasswordBox.IsEnabled = !connected;
        ServerTypesButton.IsEnabled = connected && !_busy;
        var hasNode = connected && SelectedNode != null && _document != null && !_busy;
        MirrorButton.IsEnabled = connected && _document != null && !_busy
                                 && (_items.Count > 0 || SelectedNode is { NodeClass: not "Method" });
        InstancesOfButton.IsEnabled = connected && !_busy;
        LoadSelectionButton.IsEnabled = connected && _document != null && !_busy;
        PreviewButton.IsEnabled = connected && !_busy && (_items.Count > 0 || SelectedNode != null);
        NamespacesButton.IsEnabled = connected && !_busy;
        RemoveSelectionButton.IsEnabled = SelectionList.SelectedItem != null;
        ClearSelectionButton.IsEnabled = _items.Count > 0 || _excluded.Count > 0;
        BindButton.IsEnabled = hasNode;
        SnapshotButton.IsEnabled = connected && _document != null && !_busy;
        LiveButton.IsEnabled = !_liveStarting && (_live != null || (connected && _document != null && !_busy));
        LiveText.Text = _live != null ? L.T("Stop keeping values live") : L.T("Keep document values live");
        LiveGlyph.Text = _live != null ? "\uE71A" : "\uE9D9";
        WatchButton.IsEnabled = connected && SelectedNode?.NodeClass == "Variable";
        UnwatchButton.IsEnabled = connected && _watchRows.Count > 0;
        ServeText.Text = _host != null ? L.T("Stop serving") : L.T("Serve");
        ServeGlyph.Text = _host != null ? "\uE71A" : "\uE768";
        ServeGlyph.Foreground = _host != null ? new SolidColorBrush(Color.FromRgb(0xC0, 0x30, 0x30)) : (Brush)FindResource("Create");
        ServeButton.IsEnabled = (_host != null || _document != null) && !_busy;
        ServePortBox.IsEnabled = _host == null;
        ServeNetworkBox.IsEnabled = _host == null;
        ServeSimulateBox.IsEnabled = _host == null;
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
    private void StripAddressesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_document is not { } document)
        {
            SetStatus(L.T("Open a document first."));
            return;
        }
        var name = _documentPath != null ? Path.GetFileNameWithoutExtension(_documentPath) : "Document";
        var dialog = new SaveFileDialog
        {
            Title = L.T("Save a copy without server addresses"),
            Filter = L.T("AutomationML (*.aml)|*.aml"),
            FileName = name + "-without-addresses.aml",
            InitialDirectory = _documentPath != null ? Path.GetDirectoryName(_documentPath) : null,
        };
        if (dialog.ShowDialog() != true) return;
        Guard(L.T("Saving the copy without server addresses"), () =>
        {
            var copy = new System.Xml.Linq.XDocument(document.CAEXFile.Node.Document!);
            var report = ServerAddresses.Strip(copy);
            copy.Save(dialog.FileName);
            PluginLog.Info($"Copy without server addresses: {dialog.FileName}: {report}");
            foreach (var place in report.Remaining) PluginLog.Warn($"A host name still appears in the copy: {place}");
            SetStatus(L.T("Saved {0}: {1}.", Path.GetFileName(dialog.FileName), report)
                + (report.Remaining.Count > 0 ? L.T(" The log names each place.") : ""));
        });
    }
}
