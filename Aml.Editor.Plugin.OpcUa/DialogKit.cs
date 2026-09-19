// The frame every dialog of the plugin shares, so they read as one family
// with the tabs: a header with the command's glyph in the colour of its kind,
// a title and one sentence on what the dialog does; the body; a footer with
// messages or secondary buttons on the left and the answer on the right.
// Built in code like the dialogs themselves.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Aml.Editor.Plugin.OpcUa;

internal static class DialogKit
{
    public static readonly Brush Exchange = Frozen(0x20, 0x70, 0xC0);
    public static readonly Brush Create = Frozen(0x20, 0xA0, 0x40);
    public static readonly Brush Verify = Frozen(0xE0, 0x80, 0x20);
    public static readonly Brush Relate = Frozen(0x80, 0x40, 0xA0);
    public static readonly Brush Plain = Frozen(0x60, 0x60, 0x60);
    public static readonly Brush Muted = Frozen(0x70, 0x70, 0x70);
    private static readonly Brush Band = Frozen(0xF4, 0xF6, 0xF8);
    private static readonly Brush Line = Frozen(0xDD, 0xE1, 0xE6);
    private static readonly FontFamily Icons = new("Segoe MDL2 Assets");

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>Sets the window's content to header, body and footer.</summary>
    public static void Frame(Window window, string glyph, Brush accent, string title, string text, UIElement body,
        UIElement? footerLeft, params Button[] buttons)
    {
        window.Title = title;
        window.Background = Brushes.White;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.ShowInTaskbar = false;

        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold });
        if (text.Length > 0)
            heading.Children.Add(new TextBlock { Text = text, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        var headerRow = new DockPanel();
        var icon = new TextBlock { FontFamily = Icons, Text = glyph, FontSize = 24, Foreground = accent, Margin = new Thickness(0, 2, 14, 0), VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(icon, Dock.Left);
        headerRow.Children.Add(icon);
        headerRow.Children.Add(heading);
        var header = new Border { Background = Band, BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(16, 12, 16, 12), Child = headerRow };

        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var b in buttons)
        {
            b.Margin = new Thickness(6, 0, 0, 0);
            right.Children.Add(b);
        }
        var footerRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(right, Dock.Right);
        footerRow.Children.Add(right);
        if (footerLeft != null)
        {
            if (footerLeft is FrameworkElement f) f.VerticalAlignment = VerticalAlignment.Center;
            footerRow.Children.Add(footerLeft);
        }
        var footer = new Border { Background = Band, BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(16, 10, 16, 10), Child = footerRow };

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(new Border { Padding = new Thickness(16, 12, 16, 12), Child = body });
        window.Content = root;
    }

    public static Button Action(string text, bool primary = false, bool cancel = false) => new()
    {
        Content = text,
        MinWidth = 88,
        Padding = new Thickness(10, 3, 10, 3),
        IsDefault = primary,
        IsCancel = cancel,
        FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
    };

    public static TextBlock Label(string text, double top = 10) =>
        new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 3) };

    public static TextBlock Message() => new() { Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) };

    /// <summary>A text box with a grey hint while it is empty, and a search glyph.</summary>
    public static UIElement WithPlaceholder(TextBox box, string placeholder, bool search = true)
    {
        box.Padding = new Thickness(search ? 20 : 3, 3, 3, 3);
        var hint = new TextBlock
        {
            Text = placeholder, Foreground = Muted, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(search ? 23 : 6, 0, 0, 0),
        };
        void Update() => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, __) => Update();
        Update();
        var grid = new Grid { Margin = box.Margin };
        box.Margin = new Thickness(0);
        grid.Children.Add(box);
        grid.Children.Add(hint);
        if (search)
            grid.Children.Add(new TextBlock
            {
                FontFamily = Icons, Text = "", Foreground = Muted, FontSize = 12, IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
            });
        return grid;
    }

    /// <summary>A list entry: the name, and in grey what tells it apart. The object is the Tag.</summary>
    public static ListBoxItem Entry(string name, string detail, object tag, string? glyph = null, Brush? glyphBrush = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
        if (glyph != null)
            row.Children.Add(new TextBlock { FontFamily = Icons, Text = glyph, Foreground = glyphBrush ?? Plain, FontSize = 11, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
        if (detail.Length > 0)
            row.Children.Add(new TextBlock { Text = detail, Foreground = Muted, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return new ListBoxItem { Content = row, Tag = tag, ToolTip = detail.Length > 0 ? $"{name}  {detail}" : name };
    }

    /// <summary>The object of the selected entry.</summary>
    public static T? Selected<T>(ListBox list) where T : class => (list.SelectedItem as ListBoxItem)?.Tag as T;
}
