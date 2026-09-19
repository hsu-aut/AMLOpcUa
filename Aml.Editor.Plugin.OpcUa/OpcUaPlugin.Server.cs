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
        public string Status { get; private set; } = "waiting for the first value";
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
        if (e.Key != System.Windows.Input.Key.F5 || _client == null) return;
        e.Handled = true;
        if (await LoadRootAsync()) SetStatus("Address space reloaded.");
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

        SetBusy(true, $"Connecting to {url} …");
        try
        {
            _client = await ConnectWithTrustPromptAsync(url);
            var client = _client;
            client.ReachableChanged += reachable => Dispatcher.BeginInvoke(() =>
            {
                if (!ReferenceEquals(_client, client)) return;
                if (reachable) PluginLog.Info($"{url} answers again.");
                else PluginLog.Warn($"{url} does not answer any more.");
                SetStatus(reachable ? $"{url} answers again." : $"{url} does not answer any more. Disconnect, and connect again once it is back.");
                UpdateServerState();
            });
            PluginLog.Info($"Connected to {url} ({_client.SecurityMode}); {_client.NamespaceTable.Count} namespaces.");
            SetStatus($"Connected to {url}.");
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
            SetStatus($"Connecting to {url} failed: {ex.Message}");
        }
        finally
        {
            SetBusy(false, null);
            UpdateServerState();
        }
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
            var dates = System.Globalization.CultureInfo.CurrentCulture;
            var trust = DialogKit.Confirm(Window.GetWindow(this), "\uE72E", DialogKit.Verify, "Trust this server?",
                $"The server at {url} presents a certificate this computer does not know. Trust it only if it is the server you mean: " +
                "compare the fingerprint with the one its administrator gives you. Trusted, it is kept; a server that later shows another certificate is asked about again.",
                "Trust this certificate",
                DialogKit.Facts(
                    ("Subject", certificate.Subject, false),
                    ("Issued by", certificate.Issuer == certificate.Subject ? "itself (self-signed)" : certificate.Issuer, false),
                    ("Valid", $"{certificate.NotBefore.ToString("d", dates)} to {certificate.NotAfter.ToString("d", dates)}"
                              + (certificate.NotAfter < DateTime.Now ? "  (expired)" : ""), false),
                    ("SHA-256", certificate.Sha256, true)),
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
            SetStatus("Watching failed: " + ex.Message);
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
        if (!int.TryParse(ServePortBox.Text, out var port) || port is < 1 or > 65535) { SetStatus("Enter a port between 1 and 65535."); return; }
        var network = ServeNetworkBox.IsChecked == true;
        SetBusy(true, "Starting the document server …");
        try
        {
            _host = await AmlServerHost.StartAsync(document, new AmlServerOptions { Port = port, Network = network });
            // Values edited in the document reach the served nodes; new or removed elements need a restart.
            _structureNoted = false;
            var host = _host;
            _hostFollow = host.FollowDocument(refresh => Dispatcher.BeginInvoke(() => { if (ReferenceEquals(_host, host)) refresh(); }), changed =>
            {
                if (changed > 0) PluginLog.Debug($"Served values updated: {changed}.");
                if (!host.StructureChanged || _structureNoted) return;
                _structureNoted = true;
                PluginLog.Info("Elements were added or removed; restart serving to show them.");
                SetStatus("The document server shows the values as they change; restart it to show added or removed elements.");
            });
            var message = $"Serving {_host.Nodes} node(s) of this document at {_host.EndpointUrl}, "
                          + (network ? "to other computers too, to trusted clients only (Clients…)." : "to this computer only.");
            PluginLog.Info(message);
            SetStatus(message);
            if (_client == null) EndpointBox.Text = _host.EndpointUrl;
        }
        catch (Exception ex)
        {
            PluginLog.Error("Starting the document server failed", ex);
            SetStatus("Starting the document server failed: " + ex.Message);
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
        var message = why == null ? "Document server stopped." : $"Document server stopped: {why}.";
        PluginLog.Info(message);
        SetStatus(message);
    }

    /// <summary>
    /// The clients a server offered to the network refused, to trust, and those
    /// it trusts, to keep or drop: checked means admitted.
    /// </summary>
    private void ServeClients_Click(object sender, RoutedEventArgs e) => Guard("Changing the trusted clients", () =>
    {
        var refused = AmlServerHost.RejectedClients();
        var trusted = AmlServerHost.TrustedClients();
        if (refused.Count + trusted.Count == 0)
        {
            SetStatus("No client has tried to connect to the document server over the network yet.");
            return;
        }
        var rows = refused.Select(c => new ChecklistWindow.Row($"{c.Subject}  (refused)", c, false, c.Thumbprint))
            .Concat(trusted.Select(c => new ChecklistWindow.Row(c.Subject, c, true, c.Thumbprint))).ToList();
        var window = new ChecklistWindow("Clients of the document server",
            "Offered to the network, the document server admits only clients whose certificate is checked here. " +
            "Check a refused client only if you know it; compare its thumbprint with the one the client shows.", rows, "\uE72E")
        { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true) return;
        var admitted = window.Checked.Cast<ClientCertificate>().ToHashSet();
        foreach (var c in refused.Where(admitted.Contains)) { AmlServerHost.TrustClient(c); PluginLog.Info($"Trusted client {c.Subject} ({c.Thumbprint})."); }
        foreach (var c in trusted.Where(c => !admitted.Contains(c))) { AmlServerHost.DistrustClient(c); PluginLog.Info($"No longer trusted: {c.Subject} ({c.Thumbprint})."); }
        SetStatus("Trusted clients changed; they apply from a client's next connection.");
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
        SetStatus("Disconnected.");
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

        DetailRow("Name", node.DisplayName, bold: true);
        DetailRow("Class", node.NodeClass);
        DetailRow("NodeId", node.Address.ToString(), mono: true, copy: true);
        if (node.BrowseName != node.DisplayName) DetailRow("BrowseName", node.BrowseName);
        if (node.ReferenceType.Length > 0) DetailRow("Held by", node.ReferenceType);
        var typeRow = node.TypeDefinition != null ? DetailRow("Type", node.TypeDefinition.ToString(), mono: true) : null;
        var valueRow = node.NodeClass == "Variable" ? DetailRow("Value", "…", mono: true) : null;
        var element = MirroredElement(node.Address);
        if (element != null) DetailRow("In document", ElementPath(element), select: element);

        if (typeRow != null && node.TypeDefinition != null)
        {
            try
            {
                var type = await client.DescribeAsync(node.TypeDefinition);
                if (version != _detailsVersion) return;
                var inDocument = _document != null && AddressSpaceMirror.TypeIndex(_document).ContainsKey(node.TypeDefinition with { ServerUri = null });
                typeRow.Text = type.DisplayName + (inDocument ? "" : "   (not imported)");
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
                if (r.Good && r.DataType != null) DetailRow("DataType", r.DataType);
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
                ToolTip = $"Copy the {label}", Padding = new Thickness(3, 1, 3, 1), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top,
            };
            button.Click += (_, __) => CopyText(value);
            DockPanel.SetDock(button, Dock.Right);
            cell.Children.Add(button);
        }
        if (select != null)
        {
            var link = new Button
            {
                Content = "Select", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top,
                ToolTip = "Select the element in the editor",
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
    private InternalElementType? MirroredElement(UaNodeAddress address)
    {
        if (_document == null || _client == null || _document.CAEXFile.InstanceHierarchy[HierarchyName()] is not { } ih
            || AddressSpaceMirror.MirroredServer(ih, _client) is not { } server) return null;
        var key = address with { ServerUri = null };
        foreach (var e in server.Descendants<InternalElementType>())
        {
            try { if (AnnexANodeId.Of(e) is { } a && a with { ServerUri = null } == key) return e; }
            catch (AddressingException) { /* not a node */ }
        }
        return null;
    }

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
        SetBusy(true, "Reading the server's namespaces …");
        try
        {
            namespaces = await ServerNodeSets.ListAsync(client);
        }
        catch (Exception ex)
        {
            PluginLog.Error("Reading the namespaces failed", ex);
            SetStatus("Reading the namespaces failed: " + ex.Message);
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
        SetBusy(true, $"Reading the NodeSet of {ns.Uri} …");
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
            SetStatus($"Reading the NodeSet of {ns.Uri} failed: " + ex.Message);
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
                    Tabs.SelectedItem = ModelerTab;
                    await EnsureModelerAsync();
                    if (NodeSetInfo.TryRead(files.Paths[0]) is { } info) OpenInModeler(info, ModelerCatalog(folder));
                    else SetStatus($"The NodeSet of {ns.Uri} could not be read back from {files.Paths[0]}.");
                    break;
                case NamespaceAction.Save:
                    var dialog = new SaveFileDialog
                    {
                        Title = $"Save the NodeSet of {ns.Uri}",
                        Filter = "OPC UA NodeSet (*.xml)|*.xml",
                        FileName = Path.GetFileName(files.Paths[0]),
                    };
                    if (dialog.ShowDialog() != true) return;
                    File.Copy(files.Paths[0], dialog.FileName, true);
                    SetStatus($"Saved the NodeSet of {ns.Uri} to {dialog.FileName}.");
                    break;
            }
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Taking the NodeSet of {ns.Uri} failed", ex);
            SetStatus($"Taking the NodeSet of {ns.Uri} failed: {ex.Message}");
        }
    }

    private void BindButton_Click(object sender, RoutedEventArgs e) => Guard("Binding", () =>
    {
        var node = SelectedNode;
        var document = _document;
        var client = _client;
        if (node == null || document == null || client == null) return;

        var picker = new ElementPickerWindow(document, $"Bind {node.DisplayName} to") { Owner = Window.GetWindow(this) };
        if (picker.ShowDialog() != true || picker.Selected is not { } element) return;

        var bound = node.Address with { ServerUri = client.ServerUri };
        UaNodeAddress? before = null;
        try { before = AnnexANodeId.Of(element); }
        catch (AddressingException) { /* an alias or browse path; replaced below after asking */ }
        var hasNodeId = element.Attribute["NodeId"] != null;
        if (hasNodeId && before != bound
            && !DialogKit.Confirm(Window.GetWindow(this), "\uE71B", DialogKit.Relate, "Replace the binding?",
                $"'{element.Name}' is bound to another node already. Binding it to {node.DisplayName} replaces that NodeId.",
                "Replace",
                DialogKit.Facts(("Now", before?.ToString() ?? "(a browse path or alias)", true), ("New", bound.ToString(), true)),
                risky: true))
            return;

        AnnexANodeId.Write(element, bound);
        var message = $"Bound {node.Address} to '{element.Name}' (NodeId attribute, OPC 10000-83 Annex A).";
        PluginLog.Info(message + (before != null && before != bound ? $" It was bound to {before}." : ""));
        SetStatus(message + " Press Ctrl+S to save.");
        Selected?.Invoke(this, new SelectionEventArgs(element));
    });

    private async void SnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        var document = _document;
        if (document == null || _client == null) return;
        SetBusy(true, "Reading current values …");
        try
        {
            var result = await ValueSnapshot.ApplyAsync(document, _client);
            foreach (var p in result.Problems) PluginLog.Warn(p);
            PluginLog.Info($"Snapshot from {_client.EndpointUrl}: {result}.");
            SetStatus($"Snapshot: {result}." + (result.Read > 0 ? " Press Ctrl+S to save." : ""));
        }
        catch (Exception ex)
        {
            PluginLog.Error("Snapshot failed", ex);
            SetStatus("Snapshot failed: " + ex.Message);
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
            SetStatus("Live values stopped. Press Ctrl+S to save the last values.");
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
            _live = await LiveValues.FollowAsync(document, _client,
                write => Dispatcher.BeginInvoke(() => { if (ReferenceEquals(_document, document) && _live != null) write(); }),
                update =>
                {
                    if ((DateTime.Now - _liveStatusShown).TotalSeconds < 1) return;
                    _liveStatusShown = DateTime.Now;
                    SetStatus($"Live: {update.What} = {update.Value}  ·  {update.Updates} value(s) written.");
                });
            foreach (var p in _live.Problems) PluginLog.Warn(p);
            var message = $"Following {_live.Count} bound value(s) of {_client.EndpointUrl}"
                          + (_live.Skipped > 0 ? $"; {_live.Skipped} bound to another server left out" : "") + ".";
            PluginLog.Info(message);
            SetStatus(_live.Count == 0 ? "No element of this document is bound to this server." : message);
            if (_live.Count == 0) await StopLiveAsync();
        }
        catch (Exception ex)
        {
            PluginLog.Error("Live values failed", ex);
            SetStatus("Live values failed: " + ex.Message);
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
            "None" => "not secured",
            "Sign" => "signed" + (parts.Length > 1 ? ", " + parts[1] : ""),
            "SignAndEncrypt" => "signed and encrypted" + (parts.Length > 1 ? ", " + parts[1] : ""),
            _ => mode,
        };
    }

    private void UpdateServerState()
    {
        var connected = _client != null;
        // Not while connecting or while an operation uses the connection.
        ConnectButton.IsEnabled = !_busy;
        ConnectText.Text = connected ? "Disconnect" : "Connect";
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
        LiveText.Text = _live != null ? "Stop live values" : "Keep values live";
        LiveGlyph.Text = _live != null ? "\uE71A" : "\uE9D9";
        WatchButton.IsEnabled = connected && SelectedNode?.NodeClass == "Variable";
        UnwatchButton.IsEnabled = connected && _watchRows.Count > 0;
        ServeText.Text = _host != null ? "Stop serving" : "Serve this document";
        ServeGlyph.Text = _host != null ? "\uE71A" : "\uE768";
        ServeGlyph.Foreground = _host != null ? new SolidColorBrush(Color.FromRgb(0xC0, 0x30, 0x30)) : (Brush)FindResource("Create");
        ServeButton.IsEnabled = (_host != null || _document != null) && !_busy;
        ServePortBox.IsEnabled = _host == null;
        ServeNetworkBox.IsEnabled = _host == null;
        var lost = connected && !_client!.Reachable;
        ServerStateText.Text = (lost ? "Connection lost" : connected ? $"Connected  ·  {SecurityText(_client!.SecurityMode)}" : "Not connected")
                               + (_host != null ? $"  ·  serving at {_host.EndpointUrl}" : "");
        ConnectionDot.Fill = new SolidColorBrush(lost ? Color.FromRgb(0xD1, 0x34, 0x38) : connected ? Color.FromRgb(0x2E, 0x9E, 0x4F) : Color.FromRgb(0x9A, 0xA0, 0xA6));
        var palette = ThemePalette.Current(this);
        ConnectionPill.Background = connected ? palette.PillOn : palette.PillIdle;
        ConnectionPill.ToolTip = connected ? _client!.EndpointUrl : null;
    }
}
