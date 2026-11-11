using System.Windows;
using System.Windows.Controls;
using Aml.Editor.Plugin.OpcUa;
using Aml.Editor.Plugin.OpcUa.Localization;
using OpcUaAml.NodeSets;

namespace PluginTests;

/// <summary>
/// Fetching missing models from the OPC Foundation failed (offline at a
/// trade fair, or a vendor model it does not publish): the reason was replaced
/// by "Import … cancelled: required models are missing" although nobody
/// cancelled, and the other ways (a folder, the Cloud Library) were not offered
/// again. The dialog comes back and says why.
/// </summary>
public class MissingModelsTests
{
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }

    [Fact]
    public void Asked_again_the_dialog_says_what_went_wrong()
    {
        string? shown = null;
        Exception? failure = null;
        var german = L.German;
        L.German = false;
        try
        {
            var thread = new Thread(() =>
            {
                try
                {
                    var missing = new[] { new ModelRef("http://opcfoundation.org/UA/Machinery/", "1.03.0", null) };
                    var window = new MissingModelsWindow("Plant.NodeSet2.xml", missing, "GitHub cannot be reached.");
                    shown = string.Join(" | ", Descendants<TextBlock>(window).Select(t => t.Inlines.Count > 0
                        ? string.Concat(t.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text))
                        : t.Text));
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
        }
        finally { L.German = german; }
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());

        Assert.Contains("GitHub cannot be reached.", shown);
    }
}
