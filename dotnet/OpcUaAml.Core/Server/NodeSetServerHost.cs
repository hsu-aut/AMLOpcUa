// An OPC UA server whose address space is a NodeSet file: the model of a
// plant that does not exist yet, or a companion specification to try a client
// against. Where AmlServerHost serves the structure of an AML document, this
// one serves a model with its types, so a client sees what a real server of
// that specification would show: typed objects, state machines, DataTypes.
//
// The models a NodeSet requires are loaded before it, from the folders given
// and from the NodeSets that ship with this build. Each model is offered as
// the NamespaceFile of its namespace metadata (OPC 10000-5 6.3.13), so a
// client can fetch the NodeSet from the server instead of being handed a
// file. With Simulate the values move as a running plant's would.

using System.Globalization;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;
using OpcUaAml.NodeSets;

namespace OpcUaAml.Server;

public sealed class NodeSetServerOptions
{
    /// <summary>Another port than the document server's, so both can run at once.</summary>
    public int Port { get; init; } = 48410;

    /// <summary>Where the server keeps its certificate.</summary>
    public string PkiRoot { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AMLOpcUa", "pki-nodeset-server");

    /// <summary>
    /// Offer the server to other computers; as with the document server, off
    /// means the loopback address, an unsecured endpoint besides the secured
    /// ones and every client admitted.
    /// </summary>
    public bool Network { get; init; }

    /// <summary>Let the served values move (see <see cref="AmlServerOptions.Simulate"/>).</summary>
    public bool Simulate { get; init; }

    /// <summary>
    /// Offer each served model as the NamespaceFile of its namespace metadata.
    /// Off, a client has to rebuild the NodeSet by browsing, which is the other
    /// way "Types of the server" takes.
    /// </summary>
    public bool PublishNodeSets { get; init; } = true;

    /// <summary>Folders searched for the models a NodeSet requires, besides the bundled ones.</summary>
    public IReadOnlyList<string> Folders { get; init; } = Array.Empty<string>();
}

public sealed class NodeSetServerHost : IAsyncDisposable
{
    private readonly PlantServer _server;

    private NodeSetServerHost(PlantServer server, string endpointUrl, IReadOnlyList<string> served)
    {
        _server = server;
        EndpointUrl = endpointUrl;
        Served = served;
    }

    public string EndpointUrl { get; }

    /// <summary>The models served, in the order they were loaded.</summary>
    public IReadOnlyList<string> Served { get; }

    /// <summary>Number of nodes created from the NodeSets.</summary>
    public int Nodes => _server.NodeManager?.Nodes ?? 0;

    /// <summary>The server's certificate was made anew, because the old one named other addresses.</summary>
    public bool CertificateReplaced { get; private init; }

    public bool Simulating => _server.NodeManager?.Simulating == true;

    /// <summary>
    /// Starts a server for the NodeSets given, with the models they require.
    /// A path may name a file or a model URI the catalog knows.
    /// </summary>
    public static async Task<NodeSetServerHost> StartAsync(IEnumerable<string> nodeSets, NodeSetServerOptions? options = null, CancellationToken ct = default)
    {
        options ??= new NodeSetServerOptions();
        var files = Resolve(nodeSets, options.Folders);
        if (files.Count == 0) throw new ArgumentException("No NodeSet to serve.", nameof(nodeSets));

        var (app, url, replaced) = await ServerStartup.PrepareAsync(
            "AMLOpcUa NodeSet server", "NodeSetServer", "CN=AMLOpcUa NodeSet server",
            options.Port, options.Network, options.PkiRoot, ct).ConfigureAwait(false);

        var server = new PlantServer(files, options.PublishNodeSets, options.Simulate);
        try
        {
            await app.StartAsync(server).ConfigureAwait(false);
        }
        catch
        {
            server.Dispose();
            throw;
        }
        if (options.Simulate) server.NodeManager?.StartSimulation(TimeSpan.FromMilliseconds(500));
        return new NodeSetServerHost(server, url, files.Select(f => f.ModelUri).ToList()) { CertificateReplaced = replaced };
    }

    /// <summary>One NodeSet to serve: where it is, what it declares, and its namespaces.</summary>
    private sealed record ServedSet(string Path, string ModelUri, string? Version, DateTime? PublicationDate, IReadOnlyList<string> Namespaces);

    /// <summary>
    /// The files to load, required models first. A name that is no file is
    /// looked up as a model URI; the bundled NodeSets and the folders given
    /// provide what a NodeSet requires.
    /// </summary>
    private static IReadOnlyList<ServedSet> Resolve(IEnumerable<string> nodeSets, IReadOnlyList<string> folders)
    {
        var searched = new List<string>(folders);
        foreach (var name in nodeSets)
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(name));
            if (File.Exists(name) && folder != null && !searched.Contains(folder)) searched.Add(folder);
        }
        var catalog = NodeSetCatalog.Create(searched);

        var result = new List<ServedSet>();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        void Take(NodeSetInfo info)
        {
            foreach (var dependency in catalog.Dependencies(info)) Add(dependency);
            Add(info);
        }
        void Add(NodeSetInfo info)
        {
            // The base model is the server's own address space; serving it again
            // would define every standard node twice.
            if (info.PrimaryModel.Model.ModelUri == NodeSetInfo.UaNamespace || !taken.Add(info.PrimaryModel.Model.ModelUri)) return;
            result.Add(new ServedSet(info.FilePath, info.PrimaryModel.Model.ModelUri,
                info.PrimaryModel.Model.Version, info.PrimaryModel.Model.PublicationDate, NamespacesOf(info.FilePath)));
        }

        foreach (var name in nodeSets)
        {
            var info = File.Exists(name) ? catalog.AddFile(Path.GetFullPath(name)) : catalog.Find(name);
            if (info == null) throw new FileNotFoundException($"No NodeSet for '{name}'.", name);
            Take(info);
        }
        return result;
    }

    /// <summary>The namespaces a NodeSet writes, so the node manager can register them before importing.</summary>
    private static IReadOnlyList<string> NamespacesOf(string path)
    {
        using var stream = File.OpenRead(path);
        var set = Opc.Ua.Export.UANodeSet.Read(stream);
        return set.NamespaceUris ?? Array.Empty<string>();
    }

    public async ValueTask DisposeAsync()
    {
        _server.NodeManager?.StopSimulation();
        try { await _server.StopAsync().ConfigureAwait(false); }
        catch (Exception) { /* already stopped */ }
        _server.Dispose();
    }

    private sealed class PlantServer(IReadOnlyList<ServedSet> sets, bool publish, bool simulate) : StandardServer
    {
        public PlantNodeManager? NodeManager { get; private set; }

        protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        {
            NodeManager = new PlantNodeManager(server, configuration, sets, publish, simulate);
            return new(server, configuration, null, NodeManager);
        }
    }

    private sealed class PlantNodeManager(IServerInternal server, ApplicationConfiguration configuration,
        IReadOnlyList<ServedSet> sets, bool publish, bool simulate)
        : CustomNodeManager2(server, configuration, sets.SelectMany(s => s.Namespaces)
            .Where(u => u != NodeSetInfo.UaNamespace).Distinct().ToArray())
    {
        private readonly List<BaseDataVariableState> _variables = new();
        private readonly Dictionary<BaseDataVariableState, object?> _middle = new();
        private Timer? _simulation;
        private readonly DateTime _started = DateTime.UtcNow;

        public int Nodes { get; private set; }

        public bool Simulating => _simulation != null;

        public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
        {
            lock (Lock)
            {
                foreach (var set in sets)
                {
                    using var stream = File.OpenRead(set.Path);
                    var nodeSet = Opc.Ua.Export.UANodeSet.Read(stream);
                    var nodes = new NodeStateCollection();
                    nodeSet.Import(SystemContext, nodes);
                    NodeState? own = null;
                    foreach (var node in nodes)
                    {
                        AddPredefinedNode(SystemContext, node);
                        Nodes++;
                        Collect(node);
                        // A NodeSet may bring the metadata of its own namespace
                        // (DI does); the file then belongs there, not on a
                        // second metadata object beside it. An imported node
                        // is a plain object, so it is known by its BrowseName,
                        // which is the namespace it describes.
                        if (node.BrowseName?.Name == set.ModelUri) own = node;
                    }
                    if (publish) Publish(set, own);
                }
                AddReverseReferences(externalReferences);
            }
        }

        /// <summary>The variables whose values the simulation moves.</summary>
        private void Collect(NodeState node)
        {
            if (node is BaseDataVariableState variable && variable.Value != null) _variables.Add(variable);
            var children = new List<BaseInstanceState>();
            node.GetChildren(SystemContext, children);
            foreach (var child in children) Collect(child);
        }

        /// <summary>
        /// Offers the file as the NamespaceFile of the model's namespace
        /// metadata, which the server keeps under Server/Namespaces. A client
        /// then reads the NodeSet from the server (FileType Open, Read, Close).
        /// </summary>
        private void Publish(ServedSet set, NodeState? own)
        {
            var configuration = Server.NodeManager.ConfigurationNodeManager;
            NodeState? metadata = own
                ?? configuration.GetNamespaceMetadataState(set.ModelUri)
                ?? configuration.CreateNamespaceMetadataState(set.ModelUri);
            if (metadata == null) return;
            if (metadata is NamespaceMetadataState typed)
            {
                if (typed.NamespaceVersion != null && string.IsNullOrEmpty(typed.NamespaceVersion.Value))
                    typed.NamespaceVersion.Value = set.Version;
                if (typed.NamespacePublicationDate != null && set.PublicationDate is { } published
                    && typed.NamespacePublicationDate.Value == default)
                    typed.NamespacePublicationDate.Value = published;
            }

            var content = File.ReadAllBytes(set.Path);
            var file = new AddressSpaceFileState(metadata) { ReferenceTypeId = ReferenceTypeIds.HasComponent };
            file.Create(SystemContext, new NodeId(set.ModelUri + "/NamespaceFile", NamespaceIndexes[0]),
                new QualifiedName(BrowseNames.NamespaceFile), BrowseNames.NamespaceFile, true);
            // Create gives the children (Open, Read, Close and their arguments)
            // NodeIds of the base namespace, which another node manager owns:
            // a call on them would not reach this one, and even their
            // BrowseName could not be read. They get NodeIds of this manager.
            AssignIds(file, set.ModelUri + "/NamespaceFile");
            file.Size.Value = (ulong)content.Length;
            file.Writable.Value = false;
            file.UserWritable.Value = false;
            file.OpenCount.Value = 0;
            if (metadata is NamespaceMetadataState holder) holder.NamespaceFile = file;
            metadata.AddChild(file);

            var position = 0;
            file.Open.OnCall = (ISystemContext _, MethodState _, NodeId _, byte mode, ref uint handle) =>
            {
                if (mode != 1) return StatusCodes.BadNotWritable;   // read only
                position = 0;
                handle = 1;
                return ServiceResult.Good;
            };
            file.Read.OnCall = (ISystemContext _, MethodState _, NodeId _, uint handle, int length, ref byte[] data) =>
            {
                var n = Math.Max(0, Math.Min(Math.Min(length, 65536), content.Length - position));
                data = content.AsSpan(position, n).ToArray();
                position += n;
                return ServiceResult.Good;
            };
            file.Close.OnCall = (ISystemContext _, MethodState _, NodeId _, uint handle) => ServiceResult.Good;
            // The file's NodeId is of a namespace this manager owns, so calls
            // on Open, Read and Close are routed here: it has to be indexed
            // here too, however the metadata object came about.
            AddPredefinedNode(SystemContext, file);
            metadata.ClearChangeMasks(SystemContext, true);
        }

        /// <summary>Gives every node below one a NodeId of this manager, named after its path.</summary>
        private void AssignIds(NodeState node, string prefix)
        {
            var children = new List<BaseInstanceState>();
            node.GetChildren(SystemContext, children);
            foreach (var child in children)
            {
                var path = prefix + "/" + child.BrowseName.Name;
                child.NodeId = new NodeId(path, NamespaceIndexes[0]);
                AssignIds(child, path);
            }
        }

        public void StartSimulation(TimeSpan every)
        {
            lock (Lock)
            {
                foreach (var state in _variables) _middle[state] = state.Value;
            }
            _simulation = new Timer(_ => Simulate(), null, every, every);
        }

        public void StopSimulation()
        {
            _simulation?.Dispose();
            _simulation = null;
        }

        /// <summary>The same movement the document server simulates, on the NodeSet's values.</summary>
        private void Simulate()
        {
            var seconds = (DateTime.UtcNow - _started).TotalSeconds;
            lock (Lock)
            {
                var i = 0;
                foreach (var state in _variables)
                {
                    var phase = i++ * 0.7;
                    _middle.TryGetValue(state, out var middle);
                    var next = ValueSimulation.Next(state.DataType, middle, seconds, phase);
                    if (next == null || Equals(next, state.Value)) continue;
                    state.Value = next;
                    state.StatusCode = StatusCodes.Good;
                    state.Timestamp = DateTime.UtcNow;
                    state.ClearChangeMasks(SystemContext, false);
                }
            }
        }
    }
}
