// The NodeSet search folders: a list with add and remove.

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Aml.Editor.Plugin.OpcUa;

public sealed class FolderListWindow : Window
{
    private readonly ObservableCollection<string> _folders;
    private readonly ListBox _list;

    public IReadOnlyList<string> Folders => _folders;

    public FolderListWindow(IEnumerable<string> folders)
    {
        Width = 600;
        Height = 380;
        ResizeMode = ResizeMode.CanResizeWithGrip;

        _folders = new ObservableCollection<string>(folders);
        _list = new ListBox { ItemsSource = _folders };

        var add = DialogKit.Action("Add…");
        add.Click += (_, __) =>
        {
            var dialog = new OpenFolderDialog { Title = "Folder with NodeSet files" };
            if (dialog.ShowDialog(this) == true && !_folders.Contains(dialog.FolderName))
                _folders.Add(dialog.FolderName);
        };
        var remove = DialogKit.Action("Remove");
        remove.Margin = new Thickness(6, 0, 0, 0);
        remove.Click += (_, __) =>
        {
            if (_list.SelectedItem is string s) _folders.Remove(s);
        };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(add);
        left.Children.Add(remove);

        var ok = DialogKit.Action("OK", primary: true);
        ok.Click += (_, __) => DialogResult = true;

        var body = new Grid();
        var empty = new TextBlock
        {
            Text = "No folders yet. The folder of an imported NodeSet is searched anyway.",
            Foreground = DialogKit.Muted, Margin = new Thickness(8), IsHitTestVisible = false,
        };
        _folders.CollectionChanged += (_, __) => empty.Visibility = _folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        empty.Visibility = _folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        body.Children.Add(_list);
        body.Children.Add(empty);

        DialogKit.Frame(this, "", DialogKit.Plain, "NodeSet folders",
            "Searched for NodeSets an import requires, after the NodeSet's own folder. The UA base model and DI are built in; a newer copy in a folder here wins.",
            body, left, ok, DialogKit.Action("Cancel", cancel: true));
    }
}
