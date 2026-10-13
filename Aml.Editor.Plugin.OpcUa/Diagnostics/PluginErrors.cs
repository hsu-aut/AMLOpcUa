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
    /// <summary>
    /// The dispatchers already watched: one each, not one per process. A view
    /// created on another thread (a floating window, a test) has a dispatcher
    /// of its own that would otherwise go unwatched.
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Dispatcher, object> Watched = new();

    /// <summary>Catches the plugin's unhandled exceptions on <paramref name="dispatcher"/>; the last view installed reports them.</summary>
    public static void Install(Dispatcher dispatcher, Action<Exception> report)
    {
        _report = report;
        if (Watched.TryGetValue(dispatcher, out _)) return;
        Watched.Add(dispatcher, new object());
        dispatcher.UnhandledException += (_, e) =>
        {
            if (!IsOurs(e.Exception)) return;
            e.Handled = true;
            try { _report?.Invoke(e.Exception); }
            catch { /* reporting must not throw again */ }
        };
    }

    /// <summary>The plugin and its core; an exception whose way leads through them is the plugin's.</summary>
    private static readonly System.Reflection.Assembly[] Own =
    {
        typeof(PluginErrors).Assembly,
        typeof(OpcUaAml.Addressing.UaNodeAddress).Assembly,
    };

    /// <summary>
    /// Whether the plugin's code is on the exception's way, its own or that of
    /// an inner exception. Judged by the assembly of each frame's method, not by
    /// the text of the trace, whose file paths may name anything.
    /// </summary>
    internal static bool IsOurs(Exception? ex)
    {
        for (; ex != null; ex = ex.InnerException)
        {
            foreach (var frame in new System.Diagnostics.StackTrace(ex, false).GetFrames())
                if (frame.GetMethod()?.DeclaringType?.Assembly is { } assembly && Own.Contains(assembly)) return true;
        }
        return false;
    }
}
