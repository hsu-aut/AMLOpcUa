using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using OpcUaAml.Addressing;
using OpcUaAml.Server;

namespace OpcUaAml.Tests;

public class MirrorSelectionTests(TestServer server, DiDocument di) : IClassFixture<TestServer>, IClassFixture<DiDocument>
{
    private static UaNodeAddress Plant(string id) => new(TestServer.Namespace, UaIdType.String, id);
    private static readonly UaNodeAddress BaseObjectType = new("http://opcfoundation.org/UA/", UaIdType.Numeric, "58");

    private UaConnectOptions Options() => new()
    {
        EndpointUrl = server.EndpointUrl,
        UseSecurity = false,
        AcceptUntrustedServerCertificates = true,
        PkiRoot = Path.Combine(server.PkiRoot, "client-" + Guid.NewGuid().ToString("N")[..6]),
    };

    private static List<string> Paths(InternalElementType root) =>
        root.Descendants<InternalElementType>().Select(e =>
        {
            var parts = new List<string>();
            for (CAEXBasicObject? o = e; o is InternalElementType ie && ie != root; o = ie.CAEXParent as CAEXBasicObject) parts.Insert(0, ie.Name);
            return string.Join("/", parts);
        }).OrderBy(p => p, StringComparer.Ordinal).ToList();

    [Fact]
    public async Task Takes_several_parts_with_the_way_to_them()
    {
        await using var client = await UaClient.ConnectAsync(Options());
        var selection = new MirrorSelection
        {
            Items = { new MirrorItem(Plant("Plant.Pump1.Motor"), MirrorScope.Subtree), new MirrorItem(Plant("Plant.Counter"), MirrorScope.Node) },
        };

        var result = await AddressSpaceMirror.MirrorSelectionAsync(client, selection, di.Hierarchy("Parts"));

        Assert.Equal(new[]
        {
            "Objects", "Objects/Plant", "Objects/Plant/Counter", "Objects/Plant/Pump1", "Objects/Plant/Pump1/Motor",
            "Objects/Plant/Pump1/Motor/Samples", "Objects/Plant/Pump1/Motor/Temperature",
        }, Paths(result.Server));
        Assert.Equal(7, result.Nodes);
        Assert.Equal(7, result.Created);
        var temperature = result.Server.Descendants<InternalElementType>().Single(e => e.Name == "Temperature");
        Assert.Equal("42", temperature.Attribute["Value"]!.Value);
        Assert.Equal(client.ServerUri, result.Server.Attribute[AddressSpaceMirror.ServerUriAttribute]!.Value);
    }

    [Fact]
    public async Task Filters_leave_out_variables_and_what_is_outside_the_namespaces()
    {
        await using var client = await UaClient.ConnectAsync(Options());
        var objectsOnly = new MirrorSelection
        {
            Items = { new MirrorItem(Plant("Plant"), MirrorScope.Subtree) },
            Filter = new MirrorFilter { ObjectsOnly = true },
        };
        var otherNamespace = new MirrorSelection
        {
            Items = { new MirrorItem(Plant("Plant"), MirrorScope.Subtree) },
            Filter = new MirrorFilter { Namespaces = new[] { "http://example.org/Other/" } },
        };

        var objects = await AddressSpaceMirror.MirrorSelectionAsync(client, objectsOnly, di.Hierarchy("ObjectsOnly"));
        var preview = await AddressSpaceMirror.PreviewAsync(client, otherNamespace);

        Assert.Equal(new[] { "Objects", "Objects/Plant", "Objects/Plant/Pump1", "Objects/Plant/Pump1/Motor" }, Paths(objects.Server));
        Assert.Equal((2, false), preview); // Objects and Plant, the way to the selected node
    }

    [Fact]
    public async Task Mirroring_again_updates_and_reports_what_vanished()
    {
        await using var client = await UaClient.ConnectAsync(Options());
        var ih = di.Hierarchy("Again");
        var selection = new MirrorSelection { Items = { new MirrorItem(Plant("Plant.Pump1"), MirrorScope.Subtree) } };
        var first = await AddressSpaceMirror.MirrorSelectionAsync(client, selection, ih);
        // An element for a node the server does not have (any more).
        var motor = first.Server.Descendants<InternalElementType>().Single(e => e.Name == "Motor");
        var gone = motor.InternalElement.Append("Gearbox");
        AnnexANodeId.Write(gone, Plant("Plant.Pump1.Motor.Gearbox"));

        var stored = MirrorSelection.ReadFrom(first.Server)!;
        var second = await AddressSpaceMirror.MirrorSelectionAsync(client, stored, ih);

        Assert.Equal(first.Server.ID, second.Server.ID);
        Assert.Equal(0, second.Created);
        Assert.Equal(first.Created, second.Updated);
        Assert.Single(ih.InternalElement);
        Assert.Contains(second.Vanished, v => v.Contains("Motor/Gearbox", StringComparison.Ordinal));
        Assert.Equal(first.Server.ID, AddressSpaceMirror.MirroredServer(ih, client)?.ID);
    }

    [Fact]
    public async Task A_selection_survives_the_document()
    {
        var selection = new MirrorSelection
        {
            Items =
            {
                new MirrorItem(Plant("Plant"), MirrorScope.InstancesOf, Type: BaseObjectType),
                new MirrorItem(Plant("Maintenance"), MirrorScope.Subtree, View: Plant("Maintenance")),
            },
            Depth = 5,
            Filter = new MirrorFilter { SkipProperties = true, HideServer = false, Namespaces = new[] { TestServer.Namespace } },
            Excluded = { Plant("Plant.Pump1") },
        };
        var element = di.Hierarchy("Stored").InternalElement.Append("Server");

        selection.WriteTo(element);
        var read = MirrorSelection.ReadFrom(element)!;

        Assert.Equal(selection.Items, read.Items);
        Assert.Equal(selection.Filter.SkipProperties, read.Filter.SkipProperties);
        Assert.Equal(selection.Filter.HideServer, read.Filter.HideServer);
        Assert.Equal(selection.Filter.Namespaces, read.Filter.Namespaces);
        Assert.Equal(5, read.Depth);
        Assert.Equal(selection.Excluded, read.Excluded);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Instances_of_a_type_and_exclusions()
    {
        await using var client = await UaClient.ConnectAsync(Options());
        var byType = new MirrorItem(Plant("Plant"), MirrorScope.InstancesOf, Type: BaseObjectType);
        var all = new MirrorSelection { Items = { byType }, Depth = 1 };
        var without = new MirrorSelection { Items = { byType }, Excluded = { Plant("Plant.Pump1") } };

        var result = await AddressSpaceMirror.MirrorSelectionAsync(client, all, di.Hierarchy("ByType"));
        var none = await AddressSpaceMirror.PreviewAsync(client, without);

        // Pump1 is a BaseObjectType; Motor below it belongs to Pump1's subtree.
        Assert.Contains("Objects/Plant/Pump1/Motor", Paths(result.Server));
        Assert.DoesNotContain("Objects/Plant/Counter", Paths(result.Server));
        Assert.Equal((0, false), none);
    }

    [Fact]
    public async Task A_view_is_mirrored_below_the_Views_folder()
    {
        await using var client = await UaClient.ConnectAsync(Options());
        var views = await client.ViewsAsync();
        var maintenance = views.Single(v => v.BrowseName == "Maintenance");
        var selection = new MirrorSelection { Items = { new MirrorItem(maintenance.Address, MirrorScope.Subtree, View: maintenance.Address) } };

        var result = await AddressSpaceMirror.MirrorSelectionAsync(client, selection, di.Hierarchy("View"));

        Assert.Equal(new[] { "Views", "Views/Maintenance", "Views/Maintenance/Motor", "Views/Maintenance/Motor/Samples", "Views/Maintenance/Motor/Temperature" },
            Paths(result.Server));
    }

    [Fact]
    public async Task A_second_copy_does_not_take_the_first_for_a_plan()
    {
        await using var client = await UaClient.ConnectAsync(Options());
        var selection = new MirrorSelection { Items = { new MirrorItem(Plant("Plant.Pump1.Motor"), MirrorScope.Node) } };

        await AddressSpaceMirror.MirrorSelectionAsync(client, selection, di.Hierarchy("First"));
        var copy = await AddressSpaceMirror.MirrorSelectionAsync(client, selection, di.Hierarchy("Copy"));

        Assert.Equal(0, copy.Linked);
    }
}
