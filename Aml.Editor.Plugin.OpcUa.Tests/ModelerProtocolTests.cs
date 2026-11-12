using System.Text.Json;
using Aml.Editor.Plugin.OpcUa.Bridge;

namespace PluginTests;

/// <summary>
/// The modeler (NodeSet.js 0.3.0 on) counts its changes and sends the count
/// with "apply" and "save"; the plugin answers with the same count, and the
/// modeler calls the model clean only when nothing changed since. Before,
/// edits made while a large model was applied were marked as saved, and a
/// later Edit or New dropped them without asking.
/// </summary>
public class ModelerProtocolTests
{
    private static JsonElement Reply(string request, string answer)
    {
        JsonElement sent = default;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var modeler = new ModelerWebView(new PageKeysWebView());
                modeler.Receive(request);
                modeler.Reply(answer, true, "done");
                sent = JsonDocument.Parse(modeler.Pending!).RootElement.Clone();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
        return sent;
    }

    [Fact]
    public void Applied_echoes_the_count_the_apply_came_with()
    {
        var sent = Reply("""{"type":"apply","xml":"<UANodeSet/>","modelUri":"http://example.org/Pump/","change":7}""", "applied");

        Assert.Equal("applied", sent.GetProperty("type").GetString());
        Assert.Equal(7, sent.GetProperty("change").GetInt64());
    }

    [Fact]
    public void Saved_echoes_the_count_the_save_came_with()
    {
        var sent = Reply("""{"type":"save","xml":"<UANodeSet/>","name":"Pump.NodeSet2.xml","change":12}""", "saved");

        Assert.Equal(12, sent.GetProperty("change").GetInt64());
    }

    [Fact]
    public void A_modeler_that_sends_no_count_gets_none_back()
    {
        var sent = Reply("""{"type":"apply","xml":"<UANodeSet/>","modelUri":"http://example.org/Pump/"}""", "applied");

        Assert.False(sent.TryGetProperty("change", out _));
    }
}
