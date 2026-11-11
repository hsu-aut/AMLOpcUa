using Aml.Editor.Plugin.OpcUa;
using Aml.Editor.Plugin.OpcUa.Localization;

namespace PluginTests;

/// <summary>
/// While the document is served, elements added by "Take into document" or
/// "New instance…" need a restart of the server. The hint about that replaced
/// what the command had just reported ("12 node(s), 12 added …") about 0.3 s
/// later, so the result could not be read.
/// </summary>
public class ServeStatusTests
{
    [Fact]
    public void The_restart_hint_is_added_to_what_was_reported_once()
    {
        string? status = null;
        Exception? failure = null;
        var german = L.German;
        L.German = false;
        try
        {
            var thread = new Thread(() =>
            {
                try
                {
                    var plugin = new OpcUaPlugin();
                    plugin.StatusText.Text = "'OpcUaServer': 12 node(s), 12 added. Press Ctrl+S to save.";
                    plugin.NoteServedStructureChanged();
                    plugin.NoteServedStructureChanged();
                    status = plugin.StatusText.Text;
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
        }
        finally { L.German = german; }
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());

        Assert.StartsWith("'OpcUaServer': 12 node(s), 12 added. Press Ctrl+S to save.", status);
        Assert.Contains("restart serving", status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(status!, "estart serving").Count);
    }
}
