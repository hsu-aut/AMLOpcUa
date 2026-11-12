using Aml.Editor.Plugin.OpcUa.Localization;
using OpcUaAml.Server;

namespace PluginTests;

/// <summary>
/// "Read values into document" said what it did in the core's English words,
/// in the German plugin too ("Momentaufnahme: 3 value(s) read, 0 failed, 0
/// skipped."), and with nothing bound it said "0 value(s) read" and no more.
/// </summary>
public class SnapshotStatusTests
{
    private const string Url = "opc.tcp://127.0.0.1:4840/AMLOpcUa";

    private static string Text(SnapshotResult result, bool german = false)
    {
        var before = L.German;
        L.German = german;
        try { return Summaries.Snapshot(result, Url); }
        finally { L.German = before; }
    }

    [Fact]
    public void Values_read_are_counted_with_the_server_and_how_to_keep_them()
    {
        Assert.Equal($"Read 3 bound value(s) of {Url} into the document. Press Ctrl+S to save.",
            Text(new SnapshotResult(3, 0, 0, Array.Empty<string>())));
    }

    [Fact]
    public void The_German_plugin_says_it_in_German()
    {
        Assert.Equal($"3 gebundene Wert(e) von {Url} ins Dokument gelesen. Drücke Strg+S, um zu speichern.",
            Text(new SnapshotResult(3, 0, 0, Array.Empty<string>()), german: true));
    }

    [Fact]
    public void Values_left_out_and_not_read_are_named()
    {
        var problems = new[] { "Speed: BadNodeIdUnknown", "Cycle: the browse path leads nowhere" };
        Assert.Equal($"Read 2 bound value(s) of {Url} into the document; 1 bound to another server left out; 1 could not be read (see log); "
                     + "1 not on this server (see log). Press Ctrl+S to save.",
            Text(new SnapshotResult(2, 1, 1, problems)));
    }

    [Fact]
    public void Nothing_bound_says_how_to_bind_as_live_values_do()
    {
        var before = L.German;
        L.German = false;
        string live;
        try { live = Aml.Editor.Plugin.OpcUa.OpcUaPlugin.LiveStartText(0, 0, 0, Url); }
        finally { L.German = before; }
        Assert.Equal(live, Text(new SnapshotResult(0, 0, 0, Array.Empty<string>())));
    }
}
