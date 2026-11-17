using Aml.Editor.Plugin.OpcUa;
using Aml.Editor.Plugin.OpcUa.Localization;

namespace PluginTests;

/// <summary>What the status line says when keeping values live takes up a change of the bindings.</summary>
public class LiveChangeTextTests
{
    private static string Text(string[] added, string[] removed, int count, int notOnServer = 0)
    {
        var german = L.German;
        L.German = false;
        try { return OpcUaPlugin.LiveChangeText(added, removed, count, notOnServer); }
        finally { L.German = german; }
    }

    [Fact]
    public void A_copy_or_a_new_binding_is_named()
    {
        Assert.Equal("Live: now also Blinker  ·  2 value(s) followed.", Text(new[] { "Blinker" }, Array.Empty<string>(), 2));
    }

    [Fact]
    public void A_deleted_element_is_named()
    {
        Assert.Equal("Live: no longer DO_Timer  ·  1 value(s) followed.", Text(Array.Empty<string>(), new[] { "DO_Timer" }, 1));
    }

    [Fact]
    public void An_element_bound_to_another_node_is_named_once()
    {
        Assert.Equal("Live: Blinker at another node  ·  2 value(s) followed.", Text(new[] { "Blinker" }, new[] { "Blinker" }, 2));
    }

    [Fact]
    public void Many_names_are_counted_instead()
    {
        Assert.Equal("Live: now also 5 element(s); no longer Lampe  ·  9 value(s) followed.",
            Text(new[] { "A", "B", "C", "D", "E" }, new[] { "Lampe" }, 9));
    }

    [Fact]
    public void Bound_values_the_server_lacks_are_counted_as_when_live_values_start()
    {
        Assert.Equal("Live: now also Blinker  ·  2 value(s) followed; 1 not on this server (see log).",
            Text(new[] { "Blinker" }, Array.Empty<string>(), 2, notOnServer: 1));
    }
}
