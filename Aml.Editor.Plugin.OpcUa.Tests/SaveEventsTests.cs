using System.IO;
using System.Windows.Controls;
using Aml.Editor.Plugin.OpcUa;
using Aml.Engine.CAEX;
using OpcUaAml.Addressing;
using OpcUaAml.Server;
using static PluginTests.LessonHarness;

namespace PluginTests;

/// <summary>
/// Save events…: as a CSV file, or as an attachment of an element, preselected
/// for a container; grey while the list is empty.
/// </summary>
public class SaveEventsTests
{
    private static UaEvent Event(int id) =>
        new(BitConverter.GetBytes(id), null, null, "Drill", DateTime.UtcNow, "Spindle overloaded", 800,
            new UaNodeAddress("urn:plant", UaIdType.String, "Drill/SpindleHigh"), "SpindleHigh", true, false, true, null, null);

    private static string[] Texts(System.Windows.DependencyObject root) =>
        Logical(root).OfType<TextBlock>().Select(t => t.Text).ToArray();

    [Fact]
    public void The_dialog_preselects_by_document()
    {
        bool container = false, document = true, unsaved = true;
        string[] documentTexts = [];
        OnUiThread(() =>
        {
            container = new SaveEventsWindow(@"C:\plant\Plant.amlx", "Line").AttachmentChoice.IsChecked == true;
            var forDocument = new SaveEventsWindow(@"C:\plant\Plant.aml", "Line");
            document = forDocument.AttachmentChoice.IsChecked == true;
            documentTexts = Texts(forDocument);
            unsaved = new SaveEventsWindow(null, "Line").AttachmentChoice.IsEnabled;
            return Task.CompletedTask;
        });

        Assert.True(container);
        Assert.False(document);
        Assert.Contains(documentTexts, t => t.Contains("a file beside the document"));
        Assert.False(unsaved);
    }

    [Fact]
    public void Save_events_is_grey_for_an_empty_list()
    {
        bool before = true, after = false;
        OnUiThread(async () =>
        {
            var plugin = new OpcUaPlugin();
            before = plugin.SaveEventsButton.IsEnabled;
            plugin.DeliverEvents([Event(1)]);
            await Until(() => plugin.EventList.Items.Count > 0, 5);
            after = plugin.SaveEventsButton.IsEnabled;
        });

        Assert.False(before);
        Assert.True(after);
    }

    [Fact]
    public void An_attachment_is_written_beside_the_document_and_linked()
    {
        var folder = Directory.CreateTempSubdirectory("opcuaaml-events-attach-").FullName;
        string? uri = null;
        string status = "";
        try
        {
            OnUiThread(async () =>
            {
                var doc = CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX3_0);
                var line = doc.CAEXFile.InstanceHierarchy.Append("Plant").InternalElement.Append("Line");
                var plugin = new OpcUaPlugin();
                plugin.DocumentLoaded(doc);
                plugin.ChangeAMLFilePath(Path.Combine(folder, "Plant.aml"));
                plugin.DeliverEvents([Event(1), Event(2)]);
                await Until(() => plugin.EventList.Items.Count == 2, 5);

                plugin.SaveEventsAsAttachment(line, new DateTime(2026, 11, 19, 10, 41, 12));

                uri = line.ExternalInterface.FirstOrDefault()?.Attribute["refURI"]?.Value;
                status = plugin.StatusText.Text;
            });

            Assert.Equal("Plant.events-20261119-104112.csv", uri);
            var csv = File.ReadAllText(Path.Combine(folder, "Plant.events-20261119-104112.csv"));
            Assert.Contains("Spindle overloaded", csv);
            Assert.StartsWith("Saved 2 event(s) to Plant.events-20261119-104112.csv and attached it to 'Line'.", status);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }
}
