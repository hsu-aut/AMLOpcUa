// The plugin in English or in German, as Windows' display language says. The
// English text is the key; a text without a German entry stays English, and
// a test sees to it that none is without one. Messages that come verbatim from
// the OPC UA stack or the conversion stay English, as the command line does.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Markup;

namespace Aml.Editor.Plugin.OpcUa.Localization;

public static class L
{
    /// <summary>Whether texts are German; from Windows' display language, settable for tests.</summary>
    public static bool German { get; set; } = WindowsLanguage().TwoLetterISOLanguageName == "de";

    /// <summary>
    /// The display language of Windows. Not the UI culture of the process: the
    /// AML Editor sets that to English for everything it runs, plugins included.
    /// </summary>
    internal static CultureInfo WindowsLanguage()
    {
        try { return new CultureInfo(GetUserDefaultUILanguage()); }
        catch (CultureNotFoundException) { return CultureInfo.InstalledUICulture; }
    }

    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();

    /// <summary>The language WebView2 gets, so the embedded modeler speaks the same.</summary>
    public static string Language => German ? "de-DE" : "en-US";

    /// <summary>A text in the plugin's language, with its values put in for {0}, {1} …</summary>
    public static string T(string english, params object?[] args)
    {
        var template = German && De.Texts.TryGetValue(english, out var german) ? german : english;
        return args.Length == 0 ? template : string.Format(CultureInfo.CurrentCulture, template, args);
    }
}

/// <summary>A text in XAML: Text="{l:T 'Count nodes'}".</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }

    public TExtension(string text) => Text = text;

    [ConstructorArgument("text")]
    public string Text { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Text);
}
