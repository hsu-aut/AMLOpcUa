// A small dialog to maintain the NodeSet search folders. Built in code, not
// XAML: it is a list with three buttons and needs no styling of its own.

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
        Title = "NodeSet folders";
        Width = 560;
        Height = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResizeWithGrip;

        _folders = new ObservableCollection<string>(folders);
        _list = new ListBox { ItemsSource = _folders, Margin = new Thickness(0, 4, 0, 4) };

        var add = new Button { Content = "Add…", Width = 80, Margin = new Thickness(0, 0, 6, 0) };
        add.Click += (_, __) =>
        {
            var dialog = new OpenFolderDialog { Title = "Folder with NodeSet files" };
            if (dialog.ShowDialog(this) == true && !_folders.Contains(dialog.FolderName))
                _folders.Add(dialog.FolderName);
        };
        var remove = new Button { Content = "Remove", Width = 80 };
        remove.Click += (_, __) =>
        {
            if (_list.SelectedItem is string s) _folders.Remove(s);
        };
        var ok = new Button { Content = "OK", Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        ok.Click += (_, __) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };

        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(add);
        left.Children.Add(remove);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(ok);
        right.Children.Add(cancel);

        var buttons = new DockPanel();
        DockPanel.SetDock(left, Dock.Left);
        buttons.Children.Add(left);
        buttons.Children.Add(right);

        var hint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "Searched for NodeSets an import requires, after the NodeSet's own folder. " +
                   "The UA base model and DI are built in; a newer copy in a folder here wins.",
        };

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(hint, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(hint);
        root.Children.Add(buttons);
        root.Children.Add(_list);
        Content = root;
    }
}
