// The "Server" tab: connect to a running OPC UA server, browse its address
// space, take its types and parts of its address space into the document, bind
// nodes to elements and read current values. All server work goes through
// OpcUaAml.Core.Server.

using System.IO;
using System.Windows;
using System.Windows.Controls;
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
    private AmlServerHost? _host;
    private readonly System.Collections.ObjectModel.ObservableCollection<WatchRow> _watchRows = new();

    /// <summary>A row of the live list; updated from the subscription thread through the dispatcher.</summary>
    public sealed class WatchRow : System.ComponentModel.INotifyPropertyChanged
    {
        public required UaNodeAddress Address { get; init; }
        public required string Node { get; init; }
        public string Value { get; private set; } = "";
        public string Status { get; private set; } = "";
        public string Received { get; private set; } = "";

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public void Update(UaReadResult r)
        {
            Value = r.ValueText ?? "";
            Status = r.Status;
            Received = DateTime.Now.ToString("HH:mm:ss");
            PropertyChanged?.Invoke(this, new(null));
        }
    }

    /// <summary>Asks the editor to select an element in its tree (after mirroring or binding).</summary>
    public event EventHandler<SelectionEventArgs>? Selected;

    private void InitServerTab()
    {
        EndpointBox.Text = _settings.LastEndpointUrl ?? "opc.tcp://localhost:4840";
        SecurityToggle.IsChecked = _settings.UseSecurity;
        InitSelection();
        UpdateServerState();
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
            PluginLog.Info($"Connected to {url} ({_client.SecurityMode}); {_client.NamespaceTable.Count} namespaces.");
            SetStatus($"Connected to {url}.");
            await LoadRootAsync();
        }
        catch (UaConnectionException ex)
        {
            PluginLog.Error(ex.Message);
            SetStatus(ex.Message);
        }
        finally
        {
            SetBusy(false, null);
            UpdateServerState();
        }
    }

    /// <summary>
    /// Connects; if the server's certificate is unknown, asks the user once
    /// instead of trusting silently.
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
        catch (UaConnectionException ex) when (ex.Message.Contains("not trusted"))
        {
            var answer = MessageBox.Show(Window.GetWindow(this),
                $"The server at {url} presents a certificate this computer does not trust yet.\n\n" +
                "Trust it for this connection?", "OPC UA server certificate",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) throw;
            return await UaClient.ConnectAsync(new UaConnectOptions
            {
                EndpointUrl = options.EndpointUrl,
                UseSecurity = options.UseSecurity,
                UserName = options.UserName,
                Password = options.Password,
                AcceptUntrustedServerCertificates = true,
            });
        }
    }

    private async void WatchButton_Click(object sender, RoutedEventArgs e)
    {
        var node = SelectedNode;
        if (node == null || _client == null || node.NodeClass != "Variable") return;
        if (_watchRows.Any(r => r.Address == node.Address)) return;
        _watchRows.Add(new WatchRow { Address = node.Address, Node = node.DisplayName + "   " + node.Address.Identifier });
        await RestartWatchAsync();
    }

    private async void UnwatchButton_Click(object sender, RoutedEventArgs e)
    {
        _watchRows.Clear();
        await RestartWatchAsync();
    }

    /// <summary>One subscription for the whole list, recreated when the list changes.</summary>
    private async Task RestartWatchAsync()
    {
        if (_watch != null) { await _watch.DisposeAsync(); _watch = null; }
        WatchList.ItemsSource = _watchRows;
        WatchList.Visibility = _watchRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_client == null || _watchRows.Count == 0) return;
        var rows = _watchRows.ToDictionary(r => r.Address);
        try
        {
            _watch = await _client.WatchAsync(rows.Keys.ToList(), r =>
                Dispatcher.BeginInvoke(() => { if (rows.TryGetValue(r.Address, out var row)) row.Update(r); }));
        }
        catch (Exception ex)
        {
            PluginLog.Error("Watching failed", ex);
            SetStatus("Watching failed: " + ex.Message);
        }
    }

    private async void ServeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_host != null)
        {
            await _host.DisposeAsync();
            _host = null;
            PluginLog.Info("Document server stopped.");
            SetStatus("Document server stopped.");
            UpdateServerState();
            return;
        }
        var document = _document;
        if (document == null) return;
        if (!int.TryParse(ServePortBox.Text, out var port) || port is < 1 or > 65535) { SetStatus("Give a port between 1 and 65535."); return; }
        SetBusy(true, "Starting the document server …");
        try
        {
            _host = await AmlServerHost.StartAsync(document, new AmlServerOptions { Port = port });
            var message = $"Serving {_host.Nodes} node(s) of this document at {_host.EndpointUrl}.";
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

    private async Task DisconnectAsync()
    {
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
        var node = SelectedNode;
        if (node == null || _client == null) { NodeDetails.Text = ""; return; }
        var text = $"{node.Address}\nBrowseName: {node.BrowseName}   Class: {node.NodeClass}   Reference: {node.ReferenceType}"
                   + (node.TypeDefinition != null ? $"\nType: {node.TypeDefinition}" : "");
        if (node.NodeClass == "Variable")
        {
            try
            {
                var r = await _client.ReadAsync(node.Address);
                text += r.Good ? $"\nValue: {r.ValueText} ({r.DataType})" : $"\nValue: {r.Status}";
            }
            catch (Exception ex) { text += "\nValue: " + ex.Message; }
        }
        NodeDetails.Text = text;
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

        switch (picker.Action)
        {
            case NamespaceAction.Import when _document is { } document:
                await ImportFileAsync(document, files.Paths[0], new[] { folder, ModelsFolder });
                break;
            case NamespaceAction.Modeler:
                Tabs.SelectedItem = ModelerTab;
                await EnsureModelerAsync();
                OpenInModeler(NodeSetInfo.TryRead(files.Paths[0])!, ModelerCatalog(folder));
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

    private void BindButton_Click(object sender, RoutedEventArgs e)
    {
        var node = SelectedNode;
        var document = _document;
        if (node == null || document == null || _client == null) return;

        var picker = new ElementPickerWindow(document, $"Bind {node.DisplayName} to") { Owner = Window.GetWindow(this) };
        if (picker.ShowDialog() != true || picker.Selected == null) return;

        AnnexANodeId.Write(picker.Selected, node.Address with { ServerUri = _client.ServerUri });
        var message = $"Bound {node.Address} to '{picker.Selected.Name}' (NodeId attribute, OPC 10000-83 Annex A).";
        PluginLog.Info(message);
        SetStatus(message + " Press Ctrl+S to save.");
        Selected?.Invoke(this, new SelectionEventArgs(picker.Selected));
    }

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

    private void UpdateServerState()
    {
        var connected = _client != null;
        ConnectButton.Content = connected ? "Disconnect" : "Connect";
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
        WatchButton.IsEnabled = connected && SelectedNode?.NodeClass == "Variable";
        UnwatchButton.IsEnabled = connected && _watchRows.Count > 0;
        ServeButton.Content = _host != null ? "Stop serving" : "Serve this document";
        ServeButton.IsEnabled = (_host != null || _document != null) && !_busy;
        ServePortBox.IsEnabled = _host == null;
        ServerStateText.Text = (connected ? $"{_client!.EndpointUrl}  ({_client.SecurityMode})" : "Not connected.")
                               + (_host != null ? $"    Serving this document at {_host.EndpointUrl}" : "");
    }
}

public enum NamespaceAction { Import, Modeler, Save }

/// <summary>Picks a namespace of a server and what to do with its NodeSet.</summary>
public sealed class NamespacePickerWindow : Window
{
    private readonly ListBox _list = new();
    private readonly CheckBox _instances = new()
    {
        Content = "Include the namespace's objects (for the modeler or a file; an import takes the types)",
        Margin = new Thickness(0, 6, 0, 0),
    };

    public ServerNamespace? Selected { get; private set; }
    public NamespaceAction Action { get; private set; }
    public bool IncludeInstances => _instances.IsChecked == true;

    public NamespacePickerWindow(IReadOnlyList<ServerNamespace> namespaces, bool canImport)
    {
        Title = "Types of the server";
        Width = 640;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _list.ItemsSource = namespaces.Select(n => new Row(n)).ToList();
        _list.SelectedIndex = namespaces.Count > 0 ? 0 : -1;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        void Add(string text, NamespaceAction action, bool enabled, bool isDefault)
        {
            var b = new Button { Content = text, MinWidth = 110, Margin = new Thickness(0, 0, 6, 0), IsEnabled = enabled, IsDefault = isDefault };
            b.Click += (_, __) =>
            {
                Selected = (_list.SelectedItem as Row)?.Namespace;
                Action = action;
                if (Selected != null) DialogResult = true;
            };
            buttons.Children.Add(b);
        }
        Add("Import types", NamespaceAction.Import, canImport, canImport);
        Add("Open in modeler", NamespaceAction.Modeler, true, !canImport);
        Add("Save as…", NamespaceAction.Save, true, false);
        buttons.Children.Add(new Button { Content = "Cancel", MinWidth = 90, IsCancel = true });

        var hint = new TextBlock
        {
            Text = "Namespaces marked 'file' are published by the server as a NodeSet; the others are rebuilt by browsing, "
                   + "without documentation links and without nodes no reference leads to.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.Gray,
            Margin = new Thickness(0, 0, 0, 6),
        };
        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(hint, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(_instances, Dock.Bottom);
        root.Children.Add(hint);
        root.Children.Add(buttons);
        root.Children.Add(_instances);
        root.Children.Add(_list);
        Content = root;
    }

    private sealed record Row(ServerNamespace Namespace)
    {
        public override string ToString() =>
            $"{Namespace.Uri}   {Namespace.Version}{(Namespace.PublicationDate is { } d ? $" ({d:yyyy-MM-dd})" : "")}{(Namespace.HasFile ? "   file" : "")}";
    }
}

/// <summary>Picks an InternalElement of the document's instance hierarchies.</summary>
public sealed class ElementPickerWindow : Window
{
    private readonly ListBox _list = new();
    private readonly TextBox _search = new() { Margin = new Thickness(0, 0, 0, 4) };
    private readonly List<(string Path, InternalElementType Element)> _all;

    public InternalElementType? Selected { get; private set; }

    public ElementPickerWindow(CAEXDocument document, string title)
    {
        Title = title;
        Width = 560;
        Height = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _all = document.CAEXFile.InstanceHierarchy
            .SelectMany(ih => ih.Descendants<InternalElementType>().Select(ie => (PathOf(ie), ie)))
            .ToList();
        _search.TextChanged += (_, __) => Filter();

        var ok = new Button { Content = "Bind", Width = 90, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        ok.Click += (_, __) =>
        {
            Selected = (_list.SelectedItem as PickItem)?.Element;
            if (Selected != null) DialogResult = true;
        };
        var cancel = new Button { Content = "Cancel", Width = 90, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(_search, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(_search);
        root.Children.Add(buttons);
        root.Children.Add(_list);
        Content = root;
        Filter();
    }

    private void Filter()
    {
        var text = _search.Text.Trim();
        _list.ItemsSource = _all.Where(x => text.Length == 0 || x.Path.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Select(x => new PickItem(x.Path, x.Element)).ToList();
    }

    private static string PathOf(InternalElementType ie)
    {
        var parts = new List<string>();
        for (CAEXBasicObject? o = ie; o is CAEXObject c; o = c.CAEXParent as CAEXBasicObject)
        {
            parts.Add(c.Name);
            if (c is InstanceHierarchyType) break;
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    private sealed record PickItem(string Path, InternalElementType Element)
    {
        public override string ToString() => Path;
    }
}
