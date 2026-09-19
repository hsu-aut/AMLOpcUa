// Publishing a model of the document to the UA Cloud Library: what the library
// asks about it (title, description, copyright, license, keywords), the
// account, and whether an earlier upload is replaced. The plugin asks once
// more before it sends: after the OPC Foundation's review everyone can
// download the model.

using System.Windows;
using System.Windows.Controls;
using OpcUaAml.NodeSets;

namespace Aml.Editor.Plugin.OpcUa;

public sealed class CloudUploadWindow : Window
{
    private readonly TextBox _title = new() { Padding = new Thickness(3) };
    private readonly TextBox _description = new() { Padding = new Thickness(3), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 70, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _copyright = new() { Padding = new Thickness(3) };
    private readonly ComboBox _license = new() { ItemsSource = new[] { "MIT", "ApacheLicense20", "Custom" }, SelectedIndex = 0, Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _keywords = new() { Padding = new Thickness(3) };
    private readonly TextBox _docUrl = new() { Padding = new Thickness(3) };
    private readonly TextBox _user = new() { Width = 140, Padding = new Thickness(3) };
    private readonly PasswordBox _password = new() { Width = 120, Padding = new Thickness(3) };
    private readonly PasswordBox _apiKey = new() { Width = 170, Padding = new Thickness(3) };
    private readonly CheckBox _overwrite = new() { Content = "Replace my earlier upload of this model", Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _info = DialogKit.Message();

    public CloudUpload? Metadata { get; private set; }
    public bool Overwrite => _overwrite.IsChecked == true;
    public string? UserName => string.IsNullOrWhiteSpace(_user.Text) ? null : _user.Text.Trim();

    public CloudUploadWindow(string modelUri, string? version, string? rememberedUser)
    {
        Width = 700;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        var parts = modelUri.Split(new[] { '/', ':' }, StringSplitOptions.RemoveEmptyEntries);
        _title.Text = parts.Length > 0 ? parts[^1] : modelUri;
        _copyright.Text = $"© {DateTime.Now.Year} ";
        _user.Text = rememberedUser ?? "";
        var (password, apiKey) = CloudLibraryWindow.SessionSecrets;
        _password.Password = password ?? "";
        _apiKey.Password = apiKey ?? "";

        var form = new StackPanel();
        form.Children.Add(DialogKit.Facts(("Model", modelUri, true), ("Version", version ?? "(none)", false)));
        if (modelUri.StartsWith("http://opcfoundation.org/UA/", StringComparison.OrdinalIgnoreCase))
            form.Children.Add(new TextBlock
            {
                Text = "This namespace belongs to the OPC Foundation. Publish only models you own.",
                Foreground = DialogKit.Danger, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            });
        void Field(string label, UIElement input) { form.Children.Add(DialogKit.Label(label)); form.Children.Add(input); }
        Field("Title", _title);
        Field("Description", _description);
        Field("Copyright", _copyright);
        Field("License", _license);
        Field("Keywords, comma separated (optional)", _keywords);
        Field("Documentation URL (optional)", _docUrl);
        var credentials = new StackPanel { Orientation = Orientation.Horizontal };
        credentials.Children.Add(Caption("User"));
        credentials.Children.Add(_user);
        credentials.Children.Add(Caption("Password", 12));
        credentials.Children.Add(_password);
        credentials.Children.Add(Caption("or API key", 12));
        credentials.Children.Add(_apiKey);
        Field("Account of uacloudlibrary.opcfoundation.org", credentials);
        form.Children.Add(_overwrite);

        var publish = DialogKit.Action("Publish…", primary: true);
        publish.Click += (_, __) =>
        {
            Uri? doc = null;
            if (_docUrl.Text.Trim().Length > 0 && !Uri.TryCreate(_docUrl.Text.Trim(), UriKind.Absolute, out doc))
            {
                DialogKit.ShowError(_info, "The documentation URL is not a URL.");
                return;
            }
            if (_title.Text.Trim().Length == 0 || _description.Text.Trim().Length == 0 || _copyright.Text.Trim().Length <= 2)
            {
                DialogKit.ShowError(_info, "The Cloud Library needs a title, a description and a copyright.");
                return;
            }
            if (UserName == null && _apiKey.Password.Length == 0)
            {
                DialogKit.ShowError(_info, "Publishing needs an account or an API key.");
                return;
            }
            if (!DialogKit.Confirm(this, "", DialogKit.Exchange, "Publish to the UA Cloud Library?",
                    $"{modelUri} goes to uacloudlibrary.opcfoundation.org. Once the OPC Foundation has reviewed it, everyone can find and download it.",
                    "Publish", risky: true))
                return;
            Metadata = new CloudUpload(_title.Text.Trim(), _description.Text.Trim(), _copyright.Text.Trim())
            {
                License = (string)_license.SelectedItem,
                Keywords = _keywords.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                DocumentationUrl = doc,
            };
            CloudLibraryWindow.SessionSecrets = (_password.Password, _apiKey.Password);
            DialogResult = true;
        };

        DialogKit.Frame(this, "", DialogKit.Exchange, "Publish to the UA Cloud Library",
            "The model's NodeSet with a description, for others to find and download. The OPC Foundation reviews it before it is listed.",
            form, _info, publish, DialogKit.Action("Cancel", cancel: true));
        Loaded += (_, __) => _description.Focus();
    }

    public CloudLibraryClient CreateClient(System.Net.Http.HttpClient http) =>
        new(http,
            _apiKey.Password.Length == 0 ? UserName : null,
            _apiKey.Password.Length == 0 ? _password.Password : null,
            _apiKey.Password.Length > 0 ? _apiKey.Password : null);

    private static TextBlock Caption(string text, double left = 0) =>
        new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(left, 0, 6, 0) };
}
