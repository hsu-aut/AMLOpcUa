// Find servers…: the servers a discovery server knows (a Local Discovery
// Server at port 4840, or a server about itself), the endpoints of the one
// chosen, and its address taken into the endpoint box instead of typed.

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Aml.Editor.Plugin.OpcUa.Diagnostics;
using Aml.Editor.Plugin.OpcUa.Localization;
using OpcUaAml.Server;

namespace Aml.Editor.Plugin.OpcUa;

/// <summary>A server in the list; its text is what a screen reader says.</summary>
public sealed record ServerRow(DiscoveredServer Server)
{
    public string Name => Server.Name;
    public string Kind => Server.Kind;
    public string Url => Server.DiscoveryUrls.FirstOrDefault() ?? "";
    public override string ToString() => $"{Name}, {Url}";
}

/// <summary>An endpoint in the list.</summary>
public sealed record EndpointRow(DiscoveredEndpoint Endpoint)
{
    public string Url => Endpoint.Url;
    public string Security => Endpoint.SecurityMode == "None" ? L.T("none") : $"{Endpoint.SecurityMode} {Endpoint.SecurityPolicy}";
    public string Users => string.Join(", ", Endpoint.UserTokens);
    public override string ToString() => $"{Url}, {Security}, {Users}";
}

public sealed class FindServersWindow : Window
{
    private readonly TextBox _where = new() { MinWidth = 260 };
    private readonly ListView _servers = new() { Height = 150 };
    private readonly ListView _endpoints = new() { Height = 130 };
    private readonly TextBlock _message = DialogKit.Message();
    private readonly Button _search = DialogKit.Action(L.T("Search"));
    private readonly Button _use = DialogKit.Action(L.T("Use"), primary: true);

    /// <summary>The endpoint URL to connect to, once the dialog is closed with Use.</summary>
    public string? Chosen { get; private set; }

    /// <summary>Whether the endpoint chosen is secured; null when a server was chosen without an endpoint.</summary>
    public bool? Secure { get; private set; }

    /// <summary>What Use takes: the endpoint chosen, or else the server's first discovery URL.</summary>
    public static (string? Url, bool? Secure) Choice(DiscoveredServer? server, DiscoveredEndpoint? endpoint) =>
        endpoint != null ? (endpoint.Url, endpoint.SecurityMode != "None")
        : server != null && server.DiscoveryUrls.Count > 0 ? (server.DiscoveryUrls[0], null)
        : (null, null);

    public FindServersWindow(string where)
    {
        Width = 760;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        _where.Text = where;
        AutomationProperties.SetName(_where, L.T("Discovery server"));

        var top = new DockPanel();
        DockPanel.SetDock(_search, Dock.Right);
        _search.Margin = new Thickness(8, 0, 0, 0);
        top.Children.Add(_search);
        top.Children.Add(DialogKit.WithPlaceholder(_where, L.T("Host, host:port or URL; empty for this computer"), search: false));

        _servers.View = Grid((L.T("Server"), nameof(ServerRow.Name), 250), (L.T("Kind"), nameof(ServerRow.Kind), 110), (L.T("Discovery URL"), nameof(ServerRow.Url), 330));
        _endpoints.View = Grid((L.T("Endpoint"), nameof(EndpointRow.Url), 300), (L.T("Security"), nameof(EndpointRow.Security), 220), (L.T("Users"), nameof(EndpointRow.Users), 170));
        AutomationProperties.SetName(_servers, L.T("Servers found"));
        AutomationProperties.SetName(_endpoints, L.T("Endpoints of the server"));

        var body = new StackPanel();
        body.Children.Add(DialogKit.Label(L.T("Discovery server"), 0));
        body.Children.Add(top);
        body.Children.Add(DialogKit.Label(L.T("Servers found")));
        body.Children.Add(_servers);
        body.Children.Add(DialogKit.Label(L.T("Endpoints of the server")));
        body.Children.Add(_endpoints);
        _message.Margin = new Thickness(0, 8, 0, 0);
        body.Children.Add(_message);

        _search.Click += async (_, _) => await SearchAsync();
        // Enter in the field searches the server typed; Use, the default button, would take the one found before.
        _where.KeyDown += async (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            e.Handled = true;
            if (_search.IsEnabled) await SearchAsync();
        };
        _servers.SelectionChanged += async (_, _) => await EndpointsAsync();
        _endpoints.SelectionChanged += (_, _) => Update();
        _use.Click += (_, _) =>
        {
            (Chosen, Secure) = Choice((_servers.SelectedItem as ServerRow)?.Server, (_endpoints.SelectedItem as EndpointRow)?.Endpoint);
            if (Chosen != null) DialogResult = true;
        };
        DialogKit.Frame(this, "", DialogKit.Exchange, L.T("Find servers"),
            L.T("A discovery server lists the servers it knows: the Local Discovery Server at port 4840 of a computer, or a server about itself. Choose a server, or one of its endpoints, to take its address."),
            body, null, _use, DialogKit.Action(L.T("Cancel"), cancel: true));
        Update();
        Loaded += async (_, _) => await SearchAsync();
    }

    private static GridView Grid(params (string Header, string Path, double Width)[] columns)
    {
        var grid = new GridView();
        foreach (var (header, path, width) in columns)
            grid.Columns.Add(new GridViewColumn { Header = header, Width = width, DisplayMemberBinding = new Binding(path) });
        return grid;
    }

    private void Update() => _use.IsEnabled = _servers.SelectedItem != null;

    private async Task SearchAsync()
    {
        _search.IsEnabled = false;
        _servers.ItemsSource = null;
        _endpoints.ItemsSource = null;
        var where = UaDiscovery.DiscoveryUrl(_where.Text);
        DialogKit.ShowInfo(_message, L.T("Asking {0} …", where));
        try
        {
            var servers = await UaDiscovery.FindServersAsync(_where.Text);
            _servers.ItemsSource = servers.Select(s => new ServerRow(s)).ToList();
            DialogKit.ShowInfo(_message, servers.Count == 0 ? L.T("{0} knows no server.", where) : L.T("{0} knows {1} server(s).", where, servers.Count));
            if (servers.Count == 1) _servers.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            PluginLog.Info($"Finding servers at {where}: {ex.Message}");
            DialogKit.ShowError(_message, L.T("No discovery server answered at {0}. Is a server or a Local Discovery Server running there, and is the port open?", where));
        }
        finally
        {
            _search.IsEnabled = true;
            Update();
        }
    }

    private async Task EndpointsAsync()
    {
        _endpoints.ItemsSource = null;
        Update();
        if (_servers.SelectedItem is not ServerRow row || row.Url.Length == 0) return;
        try
        {
            var endpoints = await UaDiscovery.GetEndpointsAsync(row.Url);
            if (!ReferenceEquals(_servers.SelectedItem, row)) return;
            _endpoints.ItemsSource = endpoints.Select(e => new EndpointRow(e)).ToList();
        }
        catch (Exception ex)
        {
            PluginLog.Info($"Endpoints of {row.Url}: {ex.Message}");
            DialogKit.ShowError(_message, L.T("{0} did not list its endpoints; Use takes its discovery URL.", row.Url));
        }
    }
}
