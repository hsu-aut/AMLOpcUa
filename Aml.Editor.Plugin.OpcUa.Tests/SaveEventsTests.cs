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
            container = new SaveEventsWindow(@"C:\plant\Plant.amlx", false, "Line").AttachmentChoice.IsChecked == true;
            var forDocument = new SaveEventsWindow(@"C:\plant\Plant.aml", false, "Line");
            document = forDocument.AttachmentChoice.IsChecked == true;
            documentTexts = Texts(forDocument);
            unsaved = new SaveEventsWindow(null, false, "Line").AttachmentChoice.IsEnabled;
            return Task.CompletedTask;
        });

        Assert.True(container);
        Assert.False(document);
        Assert.Contains(documentTexts, t => t.Contains("a file beside the document"));
        Assert.False(unsaved);
    }

    /// <summary>
    /// Editor round 2026-11-19: for a container the editor gives a plugin no
    /// path (only that a container is open, and the folder of its working copy),
    /// so the attachment was offered only after "save the document first".
    /// </summary>
    [Fact]
    public void A_container_without_a_path_preselects_the_attachment()
    {
        bool enabled = false, chosen = false;
        string[] texts = [];
        OnUiThread(() =>
        {
            var window = new SaveEventsWindow(null, true, "Line");
            enabled = window.AttachmentChoice.IsEnabled;
            chosen = window.AttachmentChoice.IsChecked == true;
            texts = Texts(window);
            return Task.CompletedTask;
        });

        Assert.True(enabled);
        Assert.True(chosen);
        Assert.Contains(texts, t => t.Contains("When you save the container"));
    }

    [Fact]
    public void In_a_container_the_attachment_names_its_file_by_its_full_path()
    {
        var folder = Directory.CreateTempSubdirectory("opcuaaml-events-container-").FullName;
        string? uri = null;
        string status = "";
        var before = OpcUaPlugin.AttachmentsFolder;
        try
        {
            OpcUaPlugin.AttachmentsFolder = folder;
            OnUiThread(async () =>
            {
                var doc = CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX3_0);
                var line = doc.CAEXFile.InstanceHierarchy.Append("Plant").InternalElement.Append("Line");
                var plugin = new OpcUaPlugin();
                plugin.DocumentLoaded(doc);
                // What the editor tells a plugin when it opens a container: the folder of its working copy and the part.
                plugin.PublishAutomationMLContainer(Path.GetTempPath(), "/AlarmPlant.aml", null!);
                plugin.DeliverEvents([Event(1)]);
                await Until(() => plugin.EventList.Items.Count == 1, 5);

                plugin.SaveEventsAsAttachment(line, new DateTime(2026, 11, 19, 10, 41, 12));

                uri = line.ExternalInterface.FirstOrDefault()?.Attribute["refURI"]?.Value;
                status = plugin.StatusText.Text;
            });

            var file = Path.Combine(folder, "AlarmPlant.events-20261119-104112.csv");
            Assert.True(File.Exists(file));
            Assert.Equal(new Uri(file).AbsoluteUri, uri);
            Assert.Contains("When you save the container", status);
        }
        finally
        {
            OpcUaPlugin.AttachmentsFolder = before;
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Editor round 2026-11-19: the editor tells plugins nothing of a container
    /// it opens (ChangeAMLFilePath is for files of their own, and it publishes the
    /// container before it has set it up); its API names the .amlx all the same.
    /// </summary>
    [Fact]
    public void The_editors_path_of_a_container_puts_the_attachment_beside_it()
    {
        var folder = Directory.CreateTempSubdirectory("opcuaaml-events-amlx-").FullName;
        string? uri = null;
        (string? Path, bool Container) place = (null, false);
        try
        {
            OnUiThread(async () =>
            {
                var doc = CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX3_0);
                var line = doc.CAEXFile.InstanceHierarchy.Append("Plant").InternalElement.Append("Line");
                var plugin = new OpcUaPlugin { EditorDocumentPath = () => Path.Combine(folder, "Plant.amlx") };
                plugin.DocumentLoaded(doc);
                place = plugin.AttachmentPlace();
                plugin.DeliverEvents([Event(1)]);
                await Until(() => plugin.EventList.Items.Count == 1, 5);

                plugin.SaveEventsAsAttachment(line, new DateTime(2026, 11, 19, 10, 41, 12));
                uri = line.ExternalInterface.FirstOrDefault()?.Attribute["refURI"]?.Value;
            });

            var file = Path.Combine(folder, "Plant.events-20261119-104112.csv");
            Assert.Equal((Path.Combine(folder, "Plant.amlx"), true), place);
            Assert.True(File.Exists(file));
            Assert.Equal(new Uri(file).AbsoluteUri, uri);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Editor round 2026-11-19: after a container, the path of the .aml opened before was kept, and an attachment would have gone beside it.</summary>
    [Fact]
    public void Another_document_forgets_the_path_of_the_one_before()
    {
        (string? Path, bool Container) place = ("x", true);
        OnUiThread(() =>
        {
            var plugin = new OpcUaPlugin { EditorDocumentPath = () => null };
            plugin.DocumentLoaded(CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX3_0));
            plugin.ChangeAMLFilePath(@"C:\plant\Before.aml");
            plugin.DocumentLoaded(CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX3_0));
            place = plugin.AttachmentPlace();
            return Task.CompletedTask;
        });

        Assert.Equal((null, false), place);
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
