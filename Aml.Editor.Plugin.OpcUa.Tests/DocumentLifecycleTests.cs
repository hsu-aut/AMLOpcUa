using Aml.Editor.Plugin.OpcUa;
using Aml.Engine.CAEX;

namespace PluginTests;

/// <summary>
/// The editor unloads a document before it tells the plugin it is closed, and
/// opening another one closes the first. An exception here reaches the user as
/// the editor's "The PlugIn raised an exception" dialog.
/// </summary>
public class DocumentLifecycleTests
{
    private static void OnUiThread(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { test(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void Closing_a_document_the_editor_already_unloaded_does_not_throw()
    {
        OnUiThread(() =>
        {
            var plugin = new OpcUaPlugin();
            var first = CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX3_0);
            plugin.DocumentLoaded(first);

            first.Unload();
            plugin.DocumentUnLoaded();

            plugin.DocumentLoaded(CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX3_0));
        });
    }

    [Fact]
    public void Opening_a_document_after_the_last_one_was_unloaded_does_not_throw()
    {
        OnUiThread(() =>
        {
            var plugin = new OpcUaPlugin();
            var first = CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX3_0);
            plugin.DocumentLoaded(first);

            first.Unload();
            plugin.DocumentLoaded(CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX3_0));
        });
    }
}
