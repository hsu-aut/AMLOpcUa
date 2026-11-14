// Where the user manual is: on GitHub, beside the code, in English. Who
// installs the plugin from the PlugIn Manager never sees the repository, so
// the "?" popups and the status bar lead there. German readers start with the
// short guide in German, which leads into the manual.

using System.Diagnostics;
using Aml.Editor.Plugin.OpcUa.Diagnostics;

namespace Aml.Editor.Plugin.OpcUa.Guide;

internal static class Manual
{
    private const string Docs = "https://github.com/hsu-aut/AMLOpcUa/blob/main/docs/";

    public const string Url = Docs + "manual.md";

    /// <summary>A section of the manual, by the anchor GitHub gives its heading.</summary>
    public static string At(string section) => $"{Url}#{section}";

    /// <summary>Where a reader starts: the manual, or for German readers the short guide.</summary>
    public static string Start(bool german) => german ? Docs + "kurzanleitung.md" : Url;

    /// <summary>Opens <paramref name="url"/> in the browser; a failure goes to the log, never to the editor.</summary>
    public static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            PluginLog.Warn($"The browser could not be opened for {url}: {ex.Message}");
        }
    }
}
