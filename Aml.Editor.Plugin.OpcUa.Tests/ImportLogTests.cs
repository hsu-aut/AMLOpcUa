using Aml.Editor.Plugin.OpcUa;
using Aml.Editor.Plugin.OpcUa.Diagnostics;
using Aml.Engine.CAEX;
using static PluginTests.LessonHarness;

namespace PluginTests;

/// <summary>
/// The log said "(only its types are imported)" where the status line said
/// "(an import brings types; the modeler's Apply to document brings the
/// instances too)": the log took the command line's summary.
/// </summary>
public class ImportLogTests
{
    [Fact]
    public void The_log_says_what_the_status_line_says_after_an_import()
    {
        ProvideConverter();
        var lines = new List<string>();
        string status = "";
        void Heard(string line) { lock (lines) lines.Add(line); }
        PluginLog.OnLine += Heard;
        try
        {
            OnUiThread(async () =>
            {
                var plugin = new OpcUaPlugin();
                var document = CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX3_0);
                plugin.DocumentLoaded(document);
                await plugin.ImportFileAsync(document, OpcUaPlugin.PracticePlantFile, Array.Empty<string>());
                status = plugin.StatusText.Text;
            });
        }
        finally { PluginLog.OnLine -= Heard; }

        Assert.Contains("instance(s) of the model not taken", status);
        string imported;
        lock (lines) imported = lines.Last(l => l.Contains("Imported http://hsu-hh.de/UA/MPS500/", StringComparison.Ordinal));
        Assert.DoesNotContain("only its types are imported", imported);
        Assert.Contains("the modeler's Apply to document brings the instances too", imported);
    }
}
