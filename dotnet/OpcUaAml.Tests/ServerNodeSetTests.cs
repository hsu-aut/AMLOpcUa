using System.Xml.Linq;
using Aml.Engine.CAEX;
using Opc.Ua;
using Opc.Ua.Server;
using OpcUaAml.Compare;
using OpcUaAml.Import;
using OpcUaAml.NodeSets;
using OpcUaAml.Server;

namespace OpcUaAml.Tests;

/// <summary>A server whose address space is a NodeSet file: the bundled DI.</summary>
public class DiTestServer : TestServer
{
    public static string DiPath => Path.Combine(NodeSetCatalog.BundledFolder, "Opc.Ua.Di.NodeSet2.xml");

    /// <summary>What the namespace metadata of DI offers as its NamespaceFile; nothing when null.</summary>
    protected virtual byte[]? PublishedFile => null;

    protected override INodeManager CreateNodeManager(IServerInternal server, ApplicationConfiguration configuration) =>
        new NodeSetNodeManager(server, configuration, DiPath, Fixtures.DiUri, PublishedFile);

    private sealed class NodeSetNodeManager(IServerInternal server, ApplicationConfiguration configuration, string path, string uri, byte[]? published)
        : CustomNodeManager2(server, configuration, uri)
    {
        public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
        {
            lock (Lock)
            {
                using var stream = File.OpenRead(path);
                var set = Opc.Ua.Export.UANodeSet.Read(stream);
                var nodes = new NodeStateCollection();
                set.Import(SystemContext, nodes);
                if (published != null) AddNamespaceFile(nodes.Single(n => n.NodeId == new NodeId(15001u, NamespaceIndexes[0])), published);
                foreach (var n in nodes) AddPredefinedNode(SystemContext, n);
                AddReverseReferences(externalReferences);
            }
        }

        /// <summary>A FileType object that serves the NodeSet file, read in small chunks.</summary>
        private void AddNamespaceFile(NodeState metadata, byte[] content)
        {
            var position = 0;
            var file = new FileState(metadata) { ReferenceTypeId = ReferenceTypeIds.HasComponent };
            file.Create(SystemContext, new NodeId("DI.NamespaceFile", NamespaceIndexes[0]), new QualifiedName("NamespaceFile"), "NamespaceFile", true);
            metadata.AddChild(file);
            file.Open.OnCall = (ISystemContext _, MethodState _, NodeId _, byte mode, ref uint handle) =>
            {
                if (mode != 1) return StatusCodes.BadNotWritable;
                position = 0;
                handle = 7;
                return ServiceResult.Good;
            };
            file.Read.OnCall = (ISystemContext _, MethodState _, NodeId _, uint handle, int length, ref byte[] data) =>
            {
                var n = Math.Min(Math.Min(length, 65536), content.Length - position);
                data = content.AsSpan(position, n).ToArray();
                position += n;
                return ServiceResult.Good;
            };
            file.Close.OnCall = (ISystemContext _, MethodState _, NodeId _, uint handle) => ServiceResult.Good;
        }
    }
}

/// <summary>DI again, this time offering its NodeSet as the NamespaceFile of its metadata.</summary>
public sealed class DiFileTestServer : DiTestServer
{
    protected override byte[]? PublishedFile => File.ReadAllBytes(DiPath);
}

/// <summary>DI whose published file also declares the base model, newer than the real one.</summary>
public sealed class DiForgedFileTestServer : DiTestServer
{
    protected override byte[]? PublishedFile
    {
        get
        {
            var doc = XDocument.Load(DiPath);
            XNamespace ua = "http://opcfoundation.org/UA/2011/03/UANodeSet.xsd";
            doc.Root!.Element(ua + "Models")!.Add(new XElement(ua + "Model",
                new XAttribute("ModelUri", "http://opcfoundation.org/UA/"), new XAttribute("Version", "9.99"),
                new XAttribute("PublicationDate", "2099-01-01T00:00:00Z")));
            using var stream = new MemoryStream();
            doc.Save(stream);
            return stream.ToArray();
        }
    }
}

[Trait("Speed", "Slow")]
public class ServerForgedFileTests(DiForgedFileTestServer server) : IClassFixture<DiForgedFileTestServer>
{
    [Fact]
    public async Task A_published_file_that_declares_further_models_is_not_taken()
    {
        // Taken, it would stand in for the base model in the catalog of later imports.
        await using var client = await UaClient.ConnectAsync(new UaConnectOptions
        {
            EndpointUrl = server.EndpointUrl, UseSecurity = false, AcceptUntrustedServerCertificates = true, PkiRoot = Path.Combine(server.PkiRoot, "client"),
        });

        var set = await ServerNodeSets.FetchAsync(client, Fixtures.DiUri);

        Assert.Equal(ServerNodeSetSource.Browsed, set.Source);
        Assert.Contains(set.Notes, n => n.Contains("further models"));
    }
}

[Trait("Speed", "Slow")]
public class ServerNamespaceFileTests(DiFileTestServer server) : IClassFixture<DiFileTestServer>
{
    [Fact]
    public async Task Takes_the_file_the_server_publishes()
    {
        await using var client = await UaClient.ConnectAsync(new UaConnectOptions
        {
            EndpointUrl = server.EndpointUrl,
            UseSecurity = false,
            AcceptUntrustedServerCertificates = true,
            PkiRoot = Path.Combine(server.PkiRoot, "client"),
        });

        var namespaces = await ServerNodeSets.ListAsync(client);
        var set = await ServerNodeSets.FetchAsync(client, Fixtures.DiUri);
        var browsed = await ServerNodeSets.FetchAsync(client, Fixtures.DiUri, new ServerNodeSetOptions { PreferNamespaceFile = false });

        var di = namespaces.Single(n => n.Uri == Fixtures.DiUri);
        Assert.True(di.HasFile);
        Assert.Equal("1.05.0", di.Version);
        Assert.Equal(ServerNodeSetSource.NamespaceFile, set.Source);
        Assert.Empty(new NodeSetComparer().Compare(XDocument.Load(DiTestServer.DiPath), set.Document));
        Assert.Equal(ServerNodeSetSource.Browsed, browsed.Source);
    }
}

[Trait("Speed", "Slow")]
public class ServerNodeSetTests(DiTestServer server) : IClassFixture<DiTestServer>
{
    private const string Di = "nsu=http://opcfoundation.org/UA/DI/;";

    private UaConnectOptions Options() => new()
    {
        EndpointUrl = server.EndpointUrl,
        UseSecurity = false,
        AcceptUntrustedServerCertificates = true,
        PkiRoot = Path.Combine(server.PkiRoot, "client-" + Guid.NewGuid().ToString("N")[..6]),
    };

    /// <summary>
    /// DI nodes a server cannot give back by browsing: objects that only name
    /// well-known FunctionalGroups and hang below nothing, the deprecated type
    /// dictionaries, and children the NodeSet attaches only through an inverse
    /// ConnectsTo, which the test server does not turn into a forward reference.
    /// </summary>
    private static readonly HashSet<string> Unreachable = new[]
    {
        73, 74, 75, 90, 91, 92, 93, 94, 95,
        6423, 6425, 6435, 6437, 6539, 6548, 6555, 6564, 15893, 15894, 15897, 15902, 15903, 15906,
        6248, 6292, 6599,
    }.Select(i => $"{Di}i={i}").ToHashSet();

    private static string? NodeOf(string path)
    {
        if (!path.StartsWith("node:", StringComparison.Ordinal)) return null;
        var end = path.IndexOfAny(new[] { '/', ' ' }, path.IndexOf(';', 5) + 1);
        return end < 0 ? path[5..] : path[5..end];
    }

    private static bool Touches(Difference d, HashSet<string> nodes) =>
        NodeOf(d.Path) is { } n && nodes.Contains(n) || nodes.Any(n => d.Path.EndsWith(">" + n, StringComparison.Ordinal) || d.Path.EndsWith("<" + n, StringComparison.Ordinal));

    [Fact]
    public async Task Rebuilds_DI_from_the_server_up_to_what_browsing_cannot_see()
    {
        await using var client = await UaClient.ConnectAsync(Options());

        var set = await ServerNodeSets.FetchAsync(client, Fixtures.DiUri, new ServerNodeSetOptions { IncludeInstances = true });
        var diffs = new NodeSetComparer().Compare(XDocument.Load(DiTestServer.DiPath), set.Document);

        Assert.Equal(ServerNodeSetSource.Browsed, set.Source);
        Assert.Equal(new[] { Fixtures.UaUri }, set.RequiredModels);
        var unexplained = diffs.Where(d => !d.Path.EndsWith("/@Documentation", StringComparison.Ordinal) && !Touches(d, Unreachable)).ToList();
        // What remains: the version of the UA model the server runs, and references
        // the server holds in both directions where the file lists one.
        Assert.All(unexplained, d => Assert.True(
            d.Path.StartsWith("model:", StringComparison.Ordinal) || (d.Kind == DifferenceKind.OnlyRight && d.Path.Contains("/ref:")),
            d.ToString()));
        Assert.True(unexplained.Count <= 3, string.Join("\n", unexplained));
        var original = XDocument.Load(DiTestServer.DiPath).Root!.Elements().Count(e => e.Attribute("NodeId") != null);
        Assert.Equal(original - Unreachable.Count, set.NodeCount);
    }

    [Fact]
    public async Task Types_only_leave_out_the_objects_of_the_namespace()
    {
        await using var client = await UaClient.ConnectAsync(Options());

        var set = await ServerNodeSets.FetchAsync(client, Fixtures.DiUri);

        var names = set.Document.Root!.Elements().Select(e => (string?)e.Attribute("BrowseName")).ToHashSet();
        Assert.Contains("1:DeviceType", names);
        Assert.Contains("1:TopologyElementType", names);
        Assert.DoesNotContain("1:DeviceSet", names);
    }

    [Fact]
    public async Task Imports_the_types_of_the_server_as_the_file_would()
    {
        await using var client = await UaClient.ConnectAsync(Options());
        var folder = Path.Combine(Path.GetTempPath(), "amlopcua-server-nodesets-" + Guid.NewGuid().ToString("N")[..6]);
        var catalog = NodeSetCatalog.Create(Array.Empty<string>());

        var files = await ServerNodeSets.FetchForImportAsync(client, Fixtures.DiUri, catalog, folder);
        var fromServer = CAEXDocument.New_CAEXDocument();
        var fromFile = CAEXDocument.New_CAEXDocument();
        OpcUaImport.ImportInto(fromServer, files.Paths[0], catalog);
        OpcUaImport.ImportInto(fromFile, DiTestServer.DiPath, NodeSetCatalog.Create(Array.Empty<string>()));

        Assert.Single(files.Paths);
        var diffs = new LibraryComparer { LibraryFilter = n => n.Contains("/DI/", StringComparison.Ordinal) }.Compare(fromFile, fromServer);
        Assert.True(diffs.Count == 0, string.Join("\n", diffs.Take(30)));
    }
}
