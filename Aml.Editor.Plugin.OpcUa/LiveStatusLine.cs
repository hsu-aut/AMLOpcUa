// When the values kept live may take the status line: at most once a second,
// and not over another message for five seconds, so what a command says is
// there to be read while values stream in.

namespace Aml.Editor.Plugin.OpcUa;

internal sealed class LiveStatusLine
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(5);

    private DateTime _live = DateTime.MinValue;
    private DateTime _other = DateTime.MinValue;

    /// <summary>Another message took the line.</summary>
    public void Other(DateTime now) => _other = now;

    /// <summary>Whether a live value may take the line now; if so, it counts as shown.</summary>
    public bool MayShowLive(DateTime now)
    {
        if (now - _other < Hold || now - _live < Every) return false;
        _live = now;
        return true;
    }
}
