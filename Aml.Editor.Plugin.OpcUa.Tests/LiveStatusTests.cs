using Aml.Editor.Plugin.OpcUa;
using Aml.Editor.Plugin.OpcUa.Localization;

namespace PluginTests;

/// <summary>
/// "Keep document values live" against a server that lacks some bound nodes
/// (the learning factory's document against the simulated plant, say): they
/// were counted as followed and never moved, and nothing said so.
/// </summary>
public class LiveStatusTests
{
    private const string Url = "opc.tcp://127.0.0.1:4840/AMLOpcUa";

    private static string Text(int count, int skipped, int notOnServer)
    {
        var german = L.German;
        L.German = false;
        try { return OpcUaPlugin.LiveStartText(count, skipped, notOnServer, Url); }
        finally { L.German = german; }
    }

    [Fact]
    public void Nodes_the_server_lacks_are_named_beside_the_followed_ones()
    {
        Assert.Equal($"Following 5 bound value(s) of {Url}; 2 not on this server (see log).", Text(5, 0, 2));
    }

    [Fact]
    public void When_the_server_has_none_of_them_that_is_said_and_not_that_nothing_is_bound()
    {
        Assert.Equal($"None of the 3 bound value(s) is on {Url} (see log).", Text(0, 0, 3));
    }

    [Fact]
    public void Nothing_bound_says_how_to_bind()
    {
        // It said only that nothing is bound, and a first-time user did not know how to change that.
        Assert.Equal("No element of this document is bound to this server. Take its nodes into the document (Take into document) "
                     + "or bind an element of your own to a variable (Bind to element…).", Text(0, 0, 0));
    }

    [Fact]
    public void Values_of_another_server_and_missing_ones_are_both_named()
    {
        Assert.Equal($"Following 4 bound value(s) of {Url}; 1 bound to another server left out; 2 not on this server (see log).", Text(4, 1, 2));
    }
}
