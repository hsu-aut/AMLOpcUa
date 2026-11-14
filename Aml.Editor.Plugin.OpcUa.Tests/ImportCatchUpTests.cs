using System.Windows.Threading;
using Aml.Editor.Plugin.OpcUa;
using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Commands;
using Aml.Engine.Services;
using static PluginTests.LessonHarness;

namespace PluginTests;

/// <summary>
/// After an import the editor rebuilds its trees on the UI thread and asks its
/// lookup service about every element (IsMaster). The service built its tables
/// for the document then, with automatic updates on, and reported every
/// reference as updated: about 160 000 reports, each answered by every tree
/// searching itself. Machinery imported after taking the practice plant kept
/// the editor busy for 120 to 150 s. Updates stay off until the editor has
/// caught up.
/// </summary>
public class ImportCatchUpTests
{
    [Fact]
    public void While_the_editor_catches_up_after_an_import_automatic_updates_are_off()
    {
        ProvideConverter();
        var seen = new List<bool>();
        bool? after = null;
        var lookup = LookupService.Register();
        try
        {
            OnUiThread(async () =>
            {
                lookup.IsAutoUpdateEnabled = true;
                var plugin = new OpcUaPlugin();
                var document = CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX3_0);
                plugin.DocumentLoaded(document);
                var ui = Dispatcher.CurrentDispatcher;
                // As the editor's trees: each change answered later, on the UI thread.
                void Changed(object? s, CAEXElementChangeEventArgs e) =>
                    ui.BeginInvoke(() => { lock (seen) seen.Add(lookup.IsAutoUpdateEnabled); }, DispatcherPriority.Background);
                CaexCommand.CAEXElementChangedEvent += Changed;
                try { await plugin.ImportFileAsync(document, OpcUaPlugin.PracticePlantFile, Array.Empty<string>()); }
                finally { CaexCommand.CAEXElementChangedEvent -= Changed; }
                after = lookup.IsAutoUpdateEnabled;
            });
        }
        finally
        {
            LookupService.UnRegister();
        }

        Assert.NotEmpty(seen);
        Assert.All(seen, on => Assert.False(on));
        Assert.True(after);
    }
}
