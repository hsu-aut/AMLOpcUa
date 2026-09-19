// Search dialog for the UA Cloud Library. Credentials are held for the
// editor session only; the user name is remembered, the password or API key
// never written anywhere.

using System.Windows;
using System.Windows.Controls;
using OpcUaAml.NodeSets;

namespace Aml.Editor.Plugin.OpcUa;

public sealed class CloudLibraryWindow : Window
{
    private static string? _sessionPassword;
    private static string? _sessionApiKey;

    private readonly TextBox _user = new() { Width = 140, Padding = new Thickness(3) };
    private readonly PasswordBox _password = new() { Width = 120, Padding = new Thickness(3) };
    private readonly PasswordBox _apiKey = new() { Width = 170, Padding = new Thickness(3) };
    private readonly TextBox _search = new();
    private readonly ListBox _results = new();
    private readonly TextBlock _info = DialogKit.Message();
    private readonly Button import;

    public CloudModel? Selected { get; private set; }
    public string? UserName => string.IsNullOrWhiteSpace(_user.Text) ? null : _user.Text.Trim();

    public CloudLibraryWindow(string? rememberedUser, string? search = null)
    {
        Width = 780;
        Height = 580;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        _user.Text = rememberedUser ?? "";
        _search.Text = search ?? "";
        _password.Password = _sessionPassword ?? "";
        _apiKey.Password = _sessionApiKey ?? "";

        var credentials = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        credentials.Children.Add(Caption("User"));
        credentials.Children.Add(_user);
        credentials.Children.Add(Caption("Password", 12));
        credentials.Children.Add(_password);
        credentials.Children.Add(Caption("or API key", 12));
        credentials.Children.Add(_apiKey);

        var searchButton = DialogKit.Action("Search", primary: true);
        searchButton.Margin = new Thickness(6, 0, 0, 0);
        searchButton.Click += async (_, __) =>
        {
            // One search at a time; the button says so while it runs.
            searchButton.IsEnabled = false;
            try { await SearchAsync(); }
            finally { searchButton.IsEnabled = true; }
        };
        var searchRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(searchButton, Dock.Right);
        searchRow.Children.Add(searchButton);
        searchRow.Children.Add(DialogKit.WithPlaceholder(_search, "Keywords, e.g. Machinery, or * for all"));

        import = DialogKit.Action("Download and import");
        import.Click += (_, __) =>
        {
            Selected = DialogKit.Selected<CloudModel>(_results);
            if (Selected == null) { DialogKit.ShowError(_info, "Choose a model."); return; }
            Remember();
            DialogResult = true;
        };
        _results.MouseDoubleClick += (_, __) => import.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

        var body = new DockPanel();
        DockPanel.SetDock(credentials, Dock.Top);
        DockPanel.SetDock(searchRow, Dock.Top);
        body.Children.Add(credentials);
        body.Children.Add(searchRow);
        body.Children.Add(_results);

        DialogKit.Frame(this, "\uE753", DialogKit.Exchange, "UA Cloud Library",
            "Models of the OPC Foundation's UA Cloud Library, imported with the models they require. Searching needs an account of uacloudlibrary.opcfoundation.org or an API key; the password is kept for this session only.",
            body, _info, import, DialogKit.Action("Cancel", cancel: true));
    }

    private static TextBlock Caption(string text, double left = 0) =>
        new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(left, 0, 6, 0) };

    public CloudLibraryClient CreateClient() =>
        new(CloudLibraryClient.CreateHttp(),
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
        DialogKit.ShowInfo(_info, "Searching …");
        try
        {
            var keywords = _search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var models = await CreateClient().SearchAsync(keywords.Length == 0 ? new[] { "*" } : keywords);
            Remember();
            _results.ItemsSource = models.OrderBy(m => m.NamespaceUri)
                .Select(m => DialogKit.Entry(m.Title ?? m.NamespaceUri, $"{m.NamespaceUri}   {m.Version} ({m.PublicationDate:yyyy-MM-dd})", m)).ToList();
            DialogKit.ShowInfo(_info, models.Count == 0 ? "No model matches." : $"{models.Count} model(s). Choose one and download it.");
            if (models.Count > 0) import.IsDefault = true;
        }
        catch (CloudLibraryException ex)
        {
            DialogKit.ShowError(_info, ex.Message);
        }
        catch (Exception ex)
        {
            DialogKit.ShowError(_info, "The search failed: " + ex.Message);
        }
    }
}
