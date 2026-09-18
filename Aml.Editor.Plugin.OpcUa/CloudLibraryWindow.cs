// Search dialog for the UA Cloud Library. Credentials are held for the
// editor session only; the user name is remembered, the password or API key
// never written anywhere.

using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using OpcUaAml.NodeSets;

namespace Aml.Editor.Plugin.OpcUa;

public sealed class CloudLibraryWindow : Window
{
    private static string? _sessionPassword;
    private static string? _sessionApiKey;

    private readonly TextBox _user = new() { Width = 140 };
    private readonly PasswordBox _password = new() { Width = 120, Margin = new Thickness(4, 0, 0, 0) };
    private readonly PasswordBox _apiKey = new() { Width = 160, Margin = new Thickness(4, 0, 0, 0) };
    private readonly TextBox _search = new();
    private readonly ListBox _results = new();
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 4, 0, 0) };

    public CloudModel? Selected { get; private set; }
    public string? UserName => string.IsNullOrWhiteSpace(_user.Text) ? null : _user.Text.Trim();

    public CloudLibraryWindow(string? rememberedUser)
    {
        Title = "UA Cloud Library";
        Width = 720;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _user.Text = rememberedUser ?? "";
        _password.Password = _sessionPassword ?? "";
        _apiKey.Password = _sessionApiKey ?? "";

        var credentials = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        credentials.Children.Add(new TextBlock { Text = "User ", VerticalAlignment = VerticalAlignment.Center });
        credentials.Children.Add(_user);
        credentials.Children.Add(new TextBlock { Text = "  Password", VerticalAlignment = VerticalAlignment.Center });
        credentials.Children.Add(_password);
        credentials.Children.Add(new TextBlock { Text = "   or API key", VerticalAlignment = VerticalAlignment.Center });
        credentials.Children.Add(_apiKey);

        var searchButton = new Button { Content = "Search", Width = 80, Margin = new Thickness(6, 0, 0, 0), IsDefault = true };
        searchButton.Click += async (_, __) => await SearchAsync();
        var searchRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(searchButton, Dock.Right);
        searchRow.Children.Add(searchButton);
        searchRow.Children.Add(_search);

        var import = new Button { Content = "Download and import", Width = 150, Margin = new Thickness(0, 0, 6, 0) };
        import.Click += (_, __) =>
        {
            Selected = (_results.SelectedItem as ResultItem)?.Model;
            if (Selected == null) { _info.Text = "Choose a model."; return; }
            Remember();
            DialogResult = true;
        };
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(import);
        buttons.Children.Add(cancel);

        var root = new DockPanel { Margin = new Thickness(10) };
        foreach (var top in new UIElement[] { credentials, searchRow })
        {
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);
        }
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(_info, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(_info);
        root.Children.Add(_results);
        Content = root;
        _info.Text = "Search needs an account of uacloudlibrary.opcfoundation.org or an API key. Keywords, e.g. Machinery, or * for all.";
    }

    public CloudLibraryClient CreateClient() =>
        new(new HttpClient { Timeout = TimeSpan.FromSeconds(60) },
            _apiKey.Password.Length == 0 ? UserName : null,
            _apiKey.Password.Length == 0 ? _password.Password : null,
            _apiKey.Password.Length > 0 ? _apiKey.Password : null);

    private void Remember()
    {
        _sessionPassword = _password.Password;
        _sessionApiKey = _apiKey.Password;
    }

    private async Task SearchAsync()
    {
        _info.Text = "Searching …";
        try
        {
            var keywords = _search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var models = await CreateClient().SearchAsync(keywords.Length == 0 ? new[] { "*" } : keywords);
            Remember();
            _results.ItemsSource = models.OrderBy(m => m.NamespaceUri).Select(m => new ResultItem(m)).ToList();
            _info.Text = $"{models.Count} model(s).";
        }
        catch (CloudLibraryException ex)
        {
            _info.Text = ex.Message;
        }
    }

    private sealed record ResultItem(CloudModel Model)
    {
        public override string ToString() =>
            $"{Model.Title ?? Model.NamespaceUri}    {Model.NamespaceUri}    {Model.Version} ({Model.PublicationDate:yyyy-MM-dd})";
    }
}
