using System.IO;
using System.Windows.Controls;
using Aml.Editor.Plugin.OpcUa;
using Aml.Editor.Plugin.OpcUa.Guide;
using Aml.Engine.CAEX;
using OpcUaAml.Server;
using static PluginTests.LessonHarness;

namespace PluginTests;

/// <summary>
/// The lesson for the case the tutorial left out: a plant model of one's own,
/// whose elements are bound to the nodes of a running server one by one. It
/// is walked to its end against the practice plant, and a lesson before it
/// in the document (an instance, nodes taken) must not tick its steps off.
/// </summary>
public class BindLessonTests
{
    [Fact]
    public void The_lesson_on_binding_an_element_of_ones_own_can_be_walked_to_its_end()
    {
        ProvideConverter();
        var pki = Directory.CreateTempSubdirectory("opcuaaml-lesson-pki-").FullName;
        var done = new List<string>();
        string? notDone = null;
        try
        {
            OnUiThread(async () =>
            {
                var plugin = new OpcUaPlugin();
                var steps = plugin.TourLessons().Single(l => l.Id == "bind").Steps;
                bool Done(int i) => plugin.TourLessons().Single(l => l.Id == "bind").Steps[i].Done?.Invoke() == true;
                void Check(int i, string what)
                {
                    if (Done(i)) done.Add(what);
                    else notDone ??= $"step {i + 1} ({what}) did not tick off; status: {plugin.StatusText.Text}";
                }
                try
                {
                    Assert.False(Done(0));
                    var document = CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX3_0);
                    var plant = document.CAEXFile.InstanceHierarchy.Append("Plant");
                    plugin.DocumentLoaded(document);
                    Check(0, "document");

                    Assert.False(Done(1));
                    await plugin.StartPracticePlantAsync(FreePort(), Path.Combine(pki, "server"));
                    Check(1, "plant");

                    Assert.False(Done(2));
                    var url = plugin.PracticePlant!.EndpointUrl;
                    var client = await UaClient.ConnectAsync(new UaConnectOptions { EndpointUrl = url, UseSecurity = false, PkiRoot = Path.Combine(pki, "client") });
                    await plugin.AttachClientAsync(client, url);
                    Check(2, "connected");

                    // The user adds it in the editor's tree.
                    Assert.False(Done(3));
                    var own = plant.InternalElement.Append("PartsProduced");
                    Check(3, "own element");

                    Assert.False(Done(4));
                    await plugin.LoadRootAsync();
                    var mps = plugin.AddressTree.Items.OfType<TreeViewItem>().First(i => i.Tag is UaBrowseItem { DisplayName: "MPS500" });
                    // The way the step names; a variable without a value in the plant (HardwareRevision) reads as BadWaitingForInitialData.
                    var variable = await Child(await Child(await Child(mps, "ST30_Processing"), "ParameterSet"), "PartsProduced");
                    variable.IsSelected = true;
                    await Task.Delay(500);
                    Check(4, "variable selected");

                    // Bind to element… picks the element in a dialog; behind it, this.
                    Assert.False(Done(5));
                    OpcUaPlugin.Bind(own, ((UaBrowseItem)variable.Tag).Address with { ServerUri = client.ServerUri }, "Variable");
                    Check(5, "bound");

                    Assert.False(Done(6));
                    Click(plugin.SnapshotButton);
                    await Until(() => Done(6), 30);
                    Check(6, "read");

                    Assert.False(Done(7));
                    await plugin.StopPracticePlantAsync();
                    Check(7, "stopped");
                    Assert.Equal(8, steps.Count);
                }
                finally
                {
                    await plugin.StopPracticePlantAsync();
                }
            });
        }
        finally
        {
            try { Directory.Delete(pki, recursive: true); } catch (IOException) { }
        }

        Assert.True(notDone == null, notDone + "; done: " + string.Join(", ", done));
    }

    [Fact]
    public void An_instance_of_a_UA_type_or_nodes_taken_from_a_server_are_not_an_element_of_ones_own()
    {
        OnUiThread(() =>
        {
            var plugin = new OpcUaPlugin();
            bool Done(int i) => plugin.TourLessons().Single(l => l.Id == "bind").Steps[i].Done?.Invoke() == true;
            var document = CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX3_0);
            var ih = document.CAEXFile.InstanceHierarchy.Append("Plant");
            // As New instance… makes one: typed by a UA class, its children too.
            var instance = ih.InternalElement.Append("Software1");
            instance.RefBaseSystemUnitPath = "[SUC_http://opcfoundation.org/UA/DI/]/[SoftwareType]";
            instance.InternalElement.Append("Manufacturer");
            // As Take into document makes them: below the server element.
            var server = ih.InternalElement.Append("OpcUaServer");
            server.Attribute.Append("EndpointUrl").Value = "opc.tcp://localhost:4840";
            server.InternalElement.Append("MPS500").InternalElement.Append("Ready");
            plugin.DocumentLoaded(document);

            Assert.False(Done(3));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void The_lesson_on_binding_follows_the_practice_plant()
    {
        var order = Lessons.Titles.Keys.ToList();
        Assert.Equal(order.IndexOf("plant") + 1, order.IndexOf("bind"));
        Assert.StartsWith("4", Lessons.Titles["bind"]);
    }
}
