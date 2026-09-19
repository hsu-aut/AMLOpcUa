// The companion specifications the OPC Foundation publishes on GitHub, to
// search and import without an account. The list comes from GitHub at most
// once a day; the UA Cloud Library, which needs an account, is one click away.

using System.Windows;
using System.Windows.Controls;
using OpcUaAml.NodeSets;

namespace Aml.Editor.Plugin.OpcUa;

public sealed class OpcFoundationWindow : Window
{
    private readonly OpcFoundationNodeSets _source;
    private readonly TextBox _search = new();
    private readonly ListBox _results = new();
    private readonly TextBlock _info = DialogKit.Message();
    private readonly Button _import;
    private IReadOnlyList<PublishedModel> _models = Array.Empty<PublishedModel>();

    public PublishedModel? Selected { get; private set; }

    /// <summary>The user chose the UA Cloud Library instead.</summary>
    public bool CloudLibraryWanted { get; private set; }

    public OpcFoundationWindow(OpcFoundationNodeSets source, string? search = null)
    {
        _source = source;
        Width = 780;
        Height = 560;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        _search.Text = search ?? "";
        _search.TextChanged += (_, __) => Filter();

        _import = DialogKit.Action("Download and import", primary: true);
        _import.IsEnabled = false;
        _import.Click += (_, __) =>
        {
            Selected = DialogKit.Selected<PublishedModel>(_results);
            if (Selected == null) { DialogKit.ShowError(_info, "Choose a model."); return; }
            DialogResult = true;
        };
        _results.MouseDoubleClick += (_, __) => _import.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        _results.SelectionChanged += (_, __) => _import.IsEnabled = _results.SelectedItem != null;

        var cloud = DialogKit.Action("UA Cloud Library…");
        cloud.ToolTip = "Models of other publishers too; needs an account of uacloudlibrary.opcfoundation.org or an API key.";
        cloud.Click += (_, __) => { CloudLibraryWanted = true; DialogResult = false; };
        var refresh = DialogKit.Action("Refresh list");
        refresh.Margin = new Thickness(6, 0, 0, 0);
        refresh.ToolTip = "Ask GitHub for the current list now instead of using today's.";
        refresh.Click += async (_, __) => await LoadAsync(refresh: true);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(cloud);
        left.Children.Add(refresh);

        var body = new DockPanel();
        var box = DialogKit.WithPlaceholder(_search, "Name or namespace, e.g. Machinery or Robotics");
        ((FrameworkElement)box).Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(box, Dock.Top);
        DockPanel.SetDock(_info, Dock.Bottom);
        _info.Margin = new Thickness(0, 6, 0, 0);
        body.Children.Add(box);
        body.Children.Add(_info);
        body.Children.Add(_results);

        DialogKit.Frame(this, "", DialogKit.Exchange, "Companion specifications",
            "The NodeSets the OPC Foundation publishes (github.com/OPCFoundation/UA-Nodeset), downloaded with the models they require and imported. No account needed.",
            body, left, _import, DialogKit.Action("Cancel", cancel: true));
        Loaded += async (_, __) =>
        {
            _search.Focus();
            await LoadAsync(refresh: false);
        };
    }

    private async Task LoadAsync(bool refresh)
    {
        DialogKit.ShowInfo(_info, "Reading the OPC Foundation's list …");
        try
        {
            _models = await _source.ModelsAsync(refresh);
            Filter();
        }
        catch (OpcFoundationNodeSetsException ex)
        {
            DialogKit.ShowError(_info, ex.Message + " The UA Cloud Library is an alternative.");
        }
        catch (Exception ex)
        {
            DialogKit.ShowError(_info, "The list could not be read: " + ex.Message);
        }
    }

    private void Filter()
    {
        var found = OpcFoundationNodeSets.Search(_models, _search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        _results.ItemsSource = found.Select(m => DialogKit.Entry(m.Folder.Length > 0 ? m.Folder : System.IO.Path.GetFileName(m.Path),
            $"{m.ModelUri}   {m.Version}" + (m.PublicationDate is { } d ? $" ({d:yyyy-MM-dd})" : ""), m)).ToList();
        if (_models.Count > 0)
            DialogKit.ShowInfo(_info, found.Count == 0 ? "No model matches." : $"{found.Count} of {_models.Count} model(s).");
    }
}
