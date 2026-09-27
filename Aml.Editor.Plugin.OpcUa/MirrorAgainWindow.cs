// Dialog when a hierarchy already holds a mirror of the connected server:
// update it (and what to do with elements whose node the server no longer
// has), or mirror into a new hierarchy.

using System.Windows;
using System.Windows.Controls;
using Aml.Editor.Plugin.OpcUa.Localization;
using OpcUaAml.Server;

namespace Aml.Editor.Plugin.OpcUa;

public sealed class MirrorAgainWindow : Window
{
    private readonly RadioButton _update = new() { GroupName = "again", IsChecked = true };
    private readonly RadioButton _copy = new() { GroupName = "again" };
    private readonly ComboBox _vanished = new() { Width = 300, Margin = new Thickness(22, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };

    /// <summary>True: update the mirror; false: mirror into a new hierarchy.</summary>
    public bool Update => _update.IsChecked == true;

    public VanishedNodes Vanished => (VanishedNodes)((ComboBoxItem)_vanished.SelectedItem).Tag;

    public MirrorAgainWindow(string hierarchy, VanishedNodes vanished)
    {
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;

        foreach (var (mode, text) in new[]
                 {
                     (VanishedNodes.Report, L.T("keep them, list them in the log")),
                     (VanishedNodes.Mark, L.T("keep them, mark them with NotOnServer")),
                     (VanishedNodes.Remove, L.T("remove them from the document, after asking")),
                 })
            _vanished.Items.Add(new ComboBoxItem { Content = text, Tag = mode });
        _vanished.SelectedIndex = (int)vanished;

        _update.Content = Option(L.T("Update it"),
            L.T("Values and types are read again, new nodes are added. Elements whose node the server no longer has:"));
        _copy.Content = Option(L.T("Take into a new InstanceHierarchy"), L.T("The earlier state stays as it is."));
        _update.Checked += (_, __) => _vanished.IsEnabled = true;
        _copy.Checked += (_, __) => _vanished.IsEnabled = false;

        var body = new StackPanel();
        body.Children.Add(_update);
        body.Children.Add(_vanished);
        _copy.Margin = new Thickness(0, 14, 0, 0);
        body.Children.Add(_copy);

        var ok = DialogKit.Action(L.T("Take again"), primary: true);
        ok.Click += (_, __) => DialogResult = true;
        DialogKit.Frame(this,"", DialogKit.Exchange, L.T("Take again"),
            L.T("'{0}' already holds what was taken from this server before.", hierarchy), body, null, ok, DialogKit.Action(L.T("Cancel"), cancel: true));
    }

    private static UIElement Option(string title, string text)
    {
        var panel = new StackPanel { Margin = new Thickness(4, 0, 0, 0) };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = text, Foreground = DialogKit.Muted, TextWrapping = TextWrapping.Wrap, MaxWidth = 480 });
        return panel;
    }
}
