using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using Aml.Editor.Plugin.OpcUa;
using Aml.Editor.Plugin.OpcUa.Bridge;
using Aml.Engine.CAEX;
using OpcUaAml.Addressing;
using OpcUaAml.Server;
using static PluginTests.LessonHarness;

namespace PluginTests;

/// <summary>
/// Binding by dragging: an element dragged from the editor's tree onto a node of
/// the address space is bound to it, as with Bind to element…. The editor's trees
/// (Aml.Toolkit's AMLTreeView) drag a DataObject in the format of their view
/// model's type name, holding the view model; its CAEXNode is the element's XML.
/// </summary>
public class DragBindTests
{
    /// <summary>Stands in for the editor's AMLNodeViewModel: only its CAEXNode is read.</summary>
    private sealed class FakeNodeViewModel
    {
        public XElement? CAEXNode { get; init; }
    }

    private static IDataObject Dragged(XElement node) =>
        new DataObject(EditorDrag.NodeFormat, new FakeNodeViewModel { CAEXNode = node });

    private static CAEXDocument PlantDocument(out InternalElementType parts)
    {
        var document = CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX3_0);
        parts = document.CAEXFile.InstanceHierarchy.Append("Plant").InternalElement.Append("Station").InternalElement.Append("PartsProduced");
        return document;
    }

    private static void Sta(Action body) => OnUiThread(() => { body(); return Task.CompletedTask; });

    [Fact]
    public void An_element_dragged_from_the_editor_tree_is_found_in_the_document()
    {
        string? id = null;
        var document = PlantDocument(out var parts);
        Sta(() => id = EditorDrag.ElementOf(Dragged(parts.Node), document)?.ID);

        Assert.Equal(parts.ID, id);
    }

    [Fact]
    public void A_class_dragged_from_a_library_is_not_an_element_to_bind()
    {
        InternalElementType? found = null;
        var document = PlantDocument(out _);
        var station = document.CAEXFile.SystemUnitClassLib.Append("Lib").SystemUnitClass.Append("StationClass");
        Sta(() => found = EditorDrag.ElementOf(Dragged(station.Node), document));

        Assert.Null(found);
    }

    [Fact]
    public void An_element_of_another_document_is_not_taken()
    {
        InternalElementType? found = null;
        var document = PlantDocument(out _);
        PlantDocument(out var foreign);
        Sta(() => found = EditorDrag.ElementOf(Dragged(foreign.Node), document));

        Assert.Null(found);
    }

    [Fact]
    public void Files_and_text_are_not_an_element()
    {
        bool files = true, text = true, none = true;
        var document = PlantDocument(out _);
        Sta(() =>
        {
            var drop = new DataObject(DataFormats.FileDrop, new[] { @"C:\some\Model.NodeSet2.xml" });
            files = EditorDrag.IsEditorNode(drop) || EditorDrag.ElementOf(drop, document) != null;
            var words = new DataObject(DataFormats.Text, "PartsProduced");
            text = EditorDrag.IsEditorNode(words) || EditorDrag.ElementOf(words, document) != null;
            none = EditorDrag.ElementOf(new DataObject(EditorDrag.NodeFormat, new FakeNodeViewModel()), document) != null;
        });

        Assert.False(files);
        Assert.False(text);
        Assert.False(none);
    }

    [Fact]
    public void Without_a_connection_a_drag_is_refused()
    {
        var effect = DragDropEffects.Move;
        var document = PlantDocument(out var parts);
        Sta(() =>
        {
            var plugin = new OpcUaPlugin();
            plugin.DocumentLoaded(document);
            var item = new UaBrowseItem(new UaNodeAddress("http://hsu-hh.de/UA/MPS500/", UaIdType.Numeric, "1042"), "PartsProduced", "PartsProduced", "Variable", null, "HasComponent");
            effect = plugin.DragEffect(new TreeViewItem { Tag = item }, Dragged(parts.Node));
        });

        Assert.Equal(DragDropEffects.None, effect);
    }

    // ── with the practice plant ──────────────────────────────────────────────

    private static async Task<(OpcUaPlugin Plugin, UaClient Client)> Connected(Window window, CAEXDocument document, string pki)
    {
        var plugin = new OpcUaPlugin();
        window.Content = plugin;
        window.Show();
        plugin.DocumentLoaded(document);
        await plugin.StartPracticePlantAsync(FreePort(), Path.Combine(pki, "server"));
        var url = plugin.PracticePlant!.EndpointUrl;
        var client = await UaClient.ConnectAsync(new UaConnectOptions { EndpointUrl = url, UseSecurity = false, PkiRoot = Path.Combine(pki, "client") });
        await plugin.AttachClientAsync(client, url);
        await plugin.LoadRootAsync();
        return (plugin, client);
    }

    private static async Task<TreeViewItem> PartsProduced(OpcUaPlugin plugin)
    {
        var mps = plugin.AddressTree.Items.OfType<TreeViewItem>().First(i => i.Tag is UaBrowseItem { DisplayName: "MPS500" });
        return await Child(await Child(await Child(mps, "ST30_Processing"), "ParameterSet"), "PartsProduced");
    }

    private static void Run(Func<Window, string, Task> body)
    {
        ProvideConverter();
        var pki = Directory.CreateTempSubdirectory("opcuaaml-drag-pki-").FullName;
        try
        {
            OnUiThread(async () =>
            {
                var window = new Window { Width = 900, Height = 800, ShowInTaskbar = false, Left = -4000 };
                try { await body(window, pki); }
                finally
                {
                    if (window.Content is OpcUaPlugin plugin) await plugin.StopPracticePlantAsync();
                    window.Close();
                }
            });
        }
        finally
        {
            try { Directory.Delete(pki, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Dropping_an_element_on_a_variable_binds_it()
    {
        UaNodeAddress? bound = null, expected = null;
        bool hasValue = false, selected = false;
        string status = "";
        var document = PlantDocument(out var parts);
        Run(async (window, pki) =>
        {
            var (plugin, client) = await Connected(window, document, pki);
            var variable = await PartsProduced(plugin);
            expected = ((UaBrowseItem)variable.Tag).Address with { ServerUri = client.ServerUri };

            plugin.DropOn(variable, Dragged(parts.Node))?.Invoke();

            bound = AnnexANodeId.Of(parts);
            hasValue = parts.Attribute["Value"] != null;
            selected = variable.IsSelected;
            status = plugin.StatusText.Text;
        });

        Assert.Equal(expected, bound);
        Assert.True(hasValue);
        Assert.True(selected);
        Assert.Contains("Bound", status);
        Assert.Contains("PartsProduced", status);
    }

    [Fact]
    public void A_drag_is_taken_over_variables_and_objects_only()
    {
        DragDropEffects onVariable = DragDropEffects.None, onObject = DragDropEffects.None, onMethod = DragDropEffects.Move,
            aClass = DragDropEffects.Move, nowhere = DragDropEffects.Move;
        var document = PlantDocument(out var parts);
        var station = document.CAEXFile.SystemUnitClassLib.Append("Lib").SystemUnitClass.Append("StationClass");
        Run(async (window, pki) =>
        {
            var (plugin, _) = await Connected(window, document, pki);
            var variable = await PartsProduced(plugin);
            var mps = plugin.AddressTree.Items.OfType<TreeViewItem>().First(i => i.Tag is UaBrowseItem { DisplayName: "MPS500" });
            plugin.ShowServerBox.IsChecked = true;
            await plugin.LoadRootAsync();
            var server = plugin.AddressTree.Items.OfType<TreeViewItem>().First(i => i.Tag is UaBrowseItem { DisplayName: "Server" });
            var file = await Child(await Child(await Child(server, "Namespaces"), "http://hsu-hh.de/UA/MPS500/"), "NamespaceFile");
            var method = await Child(file, "Open");
            variable = await PartsProduced(plugin);
            mps = plugin.AddressTree.Items.OfType<TreeViewItem>().First(i => i.Tag is UaBrowseItem { DisplayName: "MPS500" });

            onVariable = plugin.DragEffect(variable, Dragged(parts.Node));
            onObject = plugin.DragEffect(mps, Dragged(parts.Node));
            onMethod = plugin.DragEffect(method, Dragged(parts.Node));
            aClass = plugin.DragEffect(variable, Dragged(station.Node));
            nowhere = plugin.DragEffect(null, Dragged(parts.Node));
        });

        Assert.Equal(DragDropEffects.Move, onVariable);
        Assert.Equal(DragDropEffects.Move, onObject);
        Assert.Equal(DragDropEffects.None, onMethod);
        Assert.Equal(DragDropEffects.None, aClass);
        Assert.Equal(DragDropEffects.None, nowhere);
    }
}
