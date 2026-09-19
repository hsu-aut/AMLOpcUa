// The last line of defence: an exception of the plugin that no handler caught
// (an async void event handler that throws) reaches the editor's dispatcher,
// and the editor may end on it, with the user's unsaved work. Such exceptions
// are logged and shown by the plugin instead. Exceptions that did not pass
// through the plugin are left to the editor.

using System.Windows.Threading;

namespace Aml.Editor.Plugin.OpcUa.Diagnostics;

internal static class PluginErrors
{
    private static Action<Exception>? _report;
    private static bool _installed;

    /// <summary>Catches the plugin's unhandled exceptions on <paramref name="dispatcher"/>; the last view installed reports them.</summary>
    public static void Install(Dispatcher dispatcher, Action<Exception> report)
    {
        _report = report;
        if (_installed) return;
        _installed = true;
        dispatcher.UnhandledException += (_, e) =>
        {
            if (!IsOurs(e.Exception)) return;
            e.Handled = true;
            try { _report?.Invoke(e.Exception); }
            catch { /* reporting must not throw again */ }
        };
    }

    /// <summary>Whether the plugin's code is on the exception's way, its own or that of an inner exception.</summary>
    internal static bool IsOurs(Exception? ex)
    {
        for (; ex != null; ex = ex.InnerException)
        {
            var trace = ex.StackTrace ?? "";
            if (trace.Contains("Aml.Editor.Plugin.OpcUa.", StringComparison.Ordinal) || trace.Contains("OpcUaAml.", StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
