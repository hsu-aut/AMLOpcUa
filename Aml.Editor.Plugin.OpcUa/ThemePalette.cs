// Colours that follow the editor's theme. The AutomationML Editor themes its
// controls with Aml.Skins on MahApps.Metro, light or dark; a fixed light grey
// band with the theme's white text would be unreadable in the dark theme. So
// every surface, line and muted text of the plugin is mixed from the theme's
// background, foreground and accent, and set as dynamic resources ("Ua.*") on
// the plugin and its dialogs. Without a theme (a test host) the light
// defaults apply.

using System.Windows;
using System.Windows.Media;

namespace Aml.Editor.Plugin.OpcUa;

internal sealed record ThemePalette(
    Brush Surface, Brush Foreground, Brush Band, Brush Line, Brush Muted, Brush CardHover, Brush PillIdle, Brush PillOn, bool Dark)
{
    // The colours of the kinds of command: exchange, create, verify, relate, plain.
    // On a dark background the light theme's blue, purple and grey are too dim to
    // read; the dark theme gets lighter ones of the same hue.
    public Brush Exchange => Dark ? DarkExchange : LightExchange;
    public Brush Create => Dark ? DarkCreate : LightCreate;
    public Brush Verify => Dark ? DarkVerify : LightVerify;
    public Brush Relate => Dark ? DarkRelate : LightRelate;
    public Brush Plain => Dark ? DarkPlain : LightPlain;

    private static readonly Brush LightExchange = Frozen(Color.FromRgb(0x20, 0x70, 0xC0));
    private static readonly Brush LightCreate = Frozen(Color.FromRgb(0x20, 0xA0, 0x40));
    private static readonly Brush LightVerify = Frozen(Color.FromRgb(0xE0, 0x80, 0x20));
    private static readonly Brush LightRelate = Frozen(Color.FromRgb(0x80, 0x40, 0xA0));
    private static readonly Brush LightPlain = Frozen(Color.FromRgb(0x80, 0x80, 0x80));
    private static readonly Brush DarkExchange = Frozen(Color.FromRgb(0x5A, 0xA2, 0xE8));
    private static readonly Brush DarkCreate = Frozen(Color.FromRgb(0x4C, 0xC0, 0x6A));
    private static readonly Brush DarkVerify = Frozen(Color.FromRgb(0xF0, 0x9A, 0x40));
    private static readonly Brush DarkRelate = Frozen(Color.FromRgb(0xB8, 0x8C, 0xE0));
    private static readonly Brush DarkPlain = Frozen(Color.FromRgb(0xA8, 0xA8, 0xA8));

    public static ThemePalette Current(FrameworkElement? scope = null)
    {
        Color Find(string key, Color fallback)
        {
            var value = scope?.TryFindResource(key) ?? Application.Current?.TryFindResource(key);
            return value switch
            {
                SolidColorBrush b => b.Color,
                Color c => c,
                _ => fallback,
            };
        }
        var background = Find("MahApps.Brushes.ThemeBackground", Colors.White);
        var foreground = Find("MahApps.Brushes.ThemeForeground", Colors.Black);
        var accent = Find("MahApps.Brushes.Accent", Color.FromRgb(0x20, 0x70, 0xC0));
        accent.A = 0xFF;
        var green = Color.FromRgb(0x2E, 0x9E, 0x4F);
        return new ThemePalette(
            Frozen(background),
            Frozen(foreground),
            Frozen(Mix(background, foreground, 0.05)),
            Frozen(Mix(background, foreground, 0.15)),
            Frozen(Mix(background, foreground, 0.55)),
            Frozen(Mix(background, accent, 0.14)),
            Frozen(Mix(background, foreground, 0.10)),
            Frozen(Mix(background, green, 0.22)),
            Luminance(background) < 0.5);
    }

    /// <summary>Sets the palette as the dynamic resources the XAML refers to.</summary>
    public void ApplyTo(FrameworkElement root)
    {
        root.Resources["Ua.Surface"] = Surface;
        root.Resources["Ua.Foreground"] = Foreground;
        root.Resources["Ua.Band"] = Band;
        root.Resources["Ua.Line"] = Line;
        root.Resources["Ua.Muted"] = Muted;
        root.Resources["Ua.CardHover"] = CardHover;
        root.Resources["Exchange"] = Exchange;
        root.Resources["Create"] = Create;
        root.Resources["Verify"] = Verify;
        root.Resources["Relate"] = Relate;
        root.Resources["Plain"] = Plain;
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;

    private static Brush Frozen(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }
}
