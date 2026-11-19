// An OPC UA server whose address space is the instance hierarchies of an AML
// document, so that clients can be tested against the engineering model
// before the plant exists.
//
// Each instance hierarchy becomes a folder under Objects. Each element becomes
// an Object, or a Variable if its UA type is a VariableType or it carries a
// Value attribute and no children. An element with an Annex A NodeId keeps
// it (its namespace is registered on the server); the others get string
// NodeIds from their path in a namespace of their own. Values come from the
// Value attributes, typed by their AttributeDataType. The nodes are those of
// the document at start; their values follow the document (RefreshValues,
// FollowDocument), new or removed elements need a restart (StructureChanged).

using System.Globalization;
using System.Xml.Linq;
using Aml.Engine.CAEX;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;
using OpcUaAml.Addressing;
using OpcUaAml.Alarms;
using OpcUaAml.Types;

namespace OpcUaAml.Server;

public sealed class AmlServerOptions
{
    public int Port { get; init; } = 48400;

    /// <summary>Namespace for elements without an Annex A NodeId.</summary>
    public string DocumentNamespace { get; init; } = "urn:amlopcua:document";

    /// <summary>Where the server keeps its certificate.</summary>
    public string PkiRoot { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AMLOpcUa", "pki-server");

    /// <summary>
    /// Offer the server to other computers. Off by default: the server then
    /// listens on the loopback address only, offers an unsecured endpoint as
    /// well and admits every client, which is what testing on this computer
    /// needs. On: it listens on every address, offers secured endpoints only
    /// and admits only clients whose certificate was trusted; the others are
    /// refused and kept among <see cref="AmlServerHost.RejectedClients"/>.
    /// </summary>
    public bool Network { get; init; }

    /// <summary>
    /// Let the served values move, as a running plant's would: numbers swing
    /// around the document's value, counters count up from it, booleans toggle
    /// every few seconds, texts stay. The document itself is not changed; a number edited in it becomes
    /// the new middle, an edited text or date is served as it is.
    /// </summary>
    public bool Simulate { get; init; }

    /// <summary>
    /// Serve the types too: an element that is an instance of a UA type is
    /// served as an instance of that type, and the types come from the
    /// NodeSets of their models (the bundled ones and <see cref="Folders"/>),
    /// without the instances those declare. Off: every element is a
    /// BaseObjectType or BaseDataVariableType.
    /// </summary>
    public bool WithTypes { get; init; }

    /// <summary>Folders searched for the NodeSets of the types (<see cref="WithTypes"/>).</summary>
    public IReadOnlyList<string> Folders { get; init; } = [];

    /// <summary>How fast the values an alarm of the document watches move with <see cref="Simulate"/>.</summary>
    public SimulationPace WatchedPace { get; init; } = SimulationPace.Watched;
}

/// <summary>A client certificate the document server refused or trusts.</summary>
public sealed record ClientCertificate(string Subject, string Thumbprint, DateTime NotAfter, string File);

public sealed class AmlServerHost : IAsyncDisposable
{
    private readonly DocumentServer _server;
    private readonly CAEXDocument _document;
    private readonly string _documentNamespace;

    private AmlServerHost(DocumentServer server, string endpointUrl, int nodes, CAEXDocument document, string documentNamespace)
    {
        _server = server;
        EndpointUrl = endpointUrl;
        Nodes = nodes;
        _document = document;
        _documentNamespace = documentNamespace;
    }

    /// <summary>
    /// Elements were added or removed since the server started; a running
    /// server cannot show them, a restart does.
    /// </summary>
    public bool StructureChanged { get; private set; }

    /// <summary>
    /// Reads the Value of every served variable from its element again and
    /// publishes what changed; subscribed clients see it at once. Call it where
    /// the document may be read (the editor's UI thread). Returns how many
    /// values changed.
    /// </summary>
    public int RefreshValues()
    {
        var changed = _server.NodeManager?.Refresh() ?? 0;
        StructureChanged = ElementCount(_document) != Nodes;
        return changed;
    }

    /// <summary>What <see cref="AmlAddressSpace.Count"/> gives, counted in the XML: runs on every edit burst.</summary>
    private static int ElementCount(CAEXDocument document) =>
        document.CAEXFile.InstanceHierarchy.Sum(ih => 1 + ih.Node.Descendants(ih.Node.Name.Namespace + "InternalElement").Count());

    /// <summary>
    /// Refreshes the served values whenever the document changes: after a
    /// short quiet time, so a burst of edits refreshes once, and through
    /// <paramref name="dispatch"/>, which must run the refresh where the
    /// document may be read. Dispose the result to stop following.
    /// </summary>
    public IDisposable FollowDocument(Action<Action> dispatch, Action<int>? refreshed = null, TimeSpan? quiet = null)
    {
        var xml = _document.CAEXFile.Node.Document ?? throw new InvalidOperationException("The document has no XML root.");
        var delay = quiet ?? TimeSpan.FromMilliseconds(300);
        Timer? timer = null;
        void Fire(object? _) => dispatch(() =>
        {
            var changed = RefreshValues();
            refreshed?.Invoke(changed);
        });
        void OnChanged(object? sender, XObjectChangeEventArgs e)
        {
            timer ??= new Timer(Fire);
            timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
        xml.Changed += OnChanged;
        return new Follower(() =>
        {
            xml.Changed -= OnChanged;
            timer?.Dispose();
        });
    }

    private sealed class Follower(Action stop) : IDisposable
    {
        private Action? _stop = stop;

        public void Dispose()
        {
            _stop?.Invoke();
            _stop = null;
        }
    }

    public string EndpointUrl { get; }

    /// <summary>Number of nodes created from the document.</summary>
    public int Nodes { get; }

    public static async Task<AmlServerHost> StartAsync(CAEXDocument document, AmlServerOptions? options = null, CancellationToken ct = default)
    {
        options ??= new AmlServerOptions();
        var model = AmlAddressSpace.From(document, options.DocumentNamespace);
        var (app, url, replaced) = await ServerStartup.PrepareAsync(
            "AMLOpcUa document server", "DocumentServer", "CN=AMLOpcUa document server, O=AMLOpcUa",
            options.Port, options.Network, options.PkiRoot, ct).ConfigureAwait(false);

        var missing = new List<string>();
        var types = options.WithTypes ? NodeSetServerHost.ResolveModels(model.TypeModels, options.Folders, missing) : [];
        var server = new DocumentServer(model, options.Simulate, types, options.WithTypes, options.WatchedPace);
        try
        {
            await ServerStartup.StartAsync(app, server, options.Port).ConfigureAwait(false);
        }
        catch
        {
            server.Dispose();
            throw;
        }
        if (options.Simulate) server.NodeManager?.StartSimulation(TimeSpan.FromMilliseconds(500));
        return new AmlServerHost(server, url, model.Count, document, options.DocumentNamespace)
        {
            CertificateReplaced = replaced,
            ServedTypes = types.Select(s => s.ModelUri).ToList(),
            MissingTypes = missing,
        };
    }

    /// <summary>The models whose types are served (<see cref="AmlServerOptions.WithTypes"/>), required models included.</summary>
    public IReadOnlyList<string> ServedTypes { get; private init; } = [];

    /// <summary>Models of the document's types whose NodeSet was not found; their instances are served as plain objects.</summary>
    public IReadOnlyList<string> MissingTypes { get; private init; } = [];

    /// <summary>The server's certificate was made anew, because the old one named other addresses.</summary>
    public bool CertificateReplaced { get; private init; }

    /// <summary>
    /// The client certificates a server offered to the network refused, newest
    /// first. Trusting one (<see cref="TrustClient"/>) admits that client from
    /// its next connection on.
    /// </summary>
    public static IReadOnlyList<ClientCertificate> RejectedClients(string? pkiRoot = null) =>
        Certificates(Path.Combine(pkiRoot ?? new AmlServerOptions().PkiRoot, "rejected", "certs"));

    /// <summary>The client certificates the document server trusts.</summary>
    public static IReadOnlyList<ClientCertificate> TrustedClients(string? pkiRoot = null) =>
        Certificates(Path.Combine(pkiRoot ?? new AmlServerOptions().PkiRoot, "trusted", "certs"));

    /// <summary>Moves a refused client certificate to the trusted ones.</summary>
    public static void TrustClient(ClientCertificate certificate, string? pkiRoot = null)
    {
        var trusted = Path.Combine(pkiRoot ?? new AmlServerOptions().PkiRoot, "trusted", "certs");
        Directory.CreateDirectory(trusted);
        File.Move(certificate.File, Path.Combine(trusted, Path.GetFileName(certificate.File)), overwrite: true);
    }

    /// <summary>Removes a client certificate from the trusted ones.</summary>
    public static void DistrustClient(ClientCertificate certificate) => File.Delete(certificate.File);

    private static IReadOnlyList<ClientCertificate> Certificates(string folder)
    {
        if (!Directory.Exists(folder)) return Array.Empty<ClientCertificate>();
        var result = new List<ClientCertificate>();
        foreach (var file in Directory.EnumerateFiles(folder).OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                using var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(file);
                result.Add(new ClientCertificate(cert.Subject, cert.Thumbprint, cert.NotAfter, file));
            }
            catch (System.Security.Cryptography.CryptographicException) { /* not a certificate */ }
        }
        return result;
    }

    /// <summary>Whether the served values are simulated (<see cref="AmlServerOptions.Simulate"/>).</summary>
    public bool Simulating => _server.NodeManager?.Simulating == true;

    /// <summary>How many alarms of the document are raised (OPC 10000-9).</summary>
    public int Alarms => _server.NodeManager?.Alarms ?? 0;

    /// <summary>The alarms of the document that are not raised, each with why: served as plain objects, as before.</summary>
    public IReadOnlyList<string> SkippedAlarms => [.. _server.Model.SkippedAlarms, .. _server.NodeManager?.SkippedAlarms ?? []];

    public async ValueTask DisposeAsync()
    {
        _server.NodeManager?.StopSimulation();
        try { await _server.StopAsync().ConfigureAwait(false); }
        catch (Exception) { /* already stopped */ }
        _server.Dispose();
    }

    private sealed class DocumentServer(AmlAddressSpace model, bool simulate, IReadOnlyList<NodeSetServerHost.ServedSet> types, bool withTypes,
        SimulationPace watchedPace) : StandardServer
    {
        public DocumentNodeManager? NodeManager { get; private set; }

        public AmlAddressSpace Model => model;

        protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        {
            // The namespaces whose types are there: the base model's always, the others when loaded.
            var typed = withTypes ? types.SelectMany(s => s.Namespaces).Append(Opc.Ua.Namespaces.OpcUa).ToHashSet(StringComparer.Ordinal) : null;
            NodeManager = new DocumentNodeManager(server, configuration, model, simulate, typed, withTypes ? types : []) { WatchedPace = watchedPace };
            if (types.Count == 0) return new(server, configuration, null, NodeManager);
            var typeManager = new NodeSetServerHost.PlantNodeManager(server, configuration, types, publish: true, simulate: false, typesOnly: true);
            return new(server, configuration, null, typeManager, NodeManager);
        }
    }

    private sealed class DocumentNodeManager(IServerInternal server, ApplicationConfiguration configuration, AmlAddressSpace model, bool simulate,
        IReadOnlySet<string>? typed, IReadOnlyList<NodeSetServerHost.ServedSet> typeSets)
        : CustomNodeManager2(server, configuration, model.NamespaceUris.ToArray())
    {
        private readonly List<(BaseDataVariableState State, AmlNode Node)> _variables = new();

        /// <summary>The served types as their NodeSets declare them, read to look up instance declarations; not in the address space.</summary>
        private readonly Dictionary<NodeId, BaseTypeState> _types = new();

        // Simulation: the document's value of each variable is the middle it moves around.
        private readonly Dictionary<BaseDataVariableState, object?> _middle = new();
        private Timer? _simulation;
        private readonly DateTime _started = DateTime.UtcNow;

        // Alarms: their nodes are made after the rest, when their values have states.
        private readonly List<AmlNode> _alarmNodes = new();
        private ServedAlarms? _alarms;
        private readonly List<string> _skippedAlarms = new();

        public bool Simulating => _simulation != null;

        public SimulationPace WatchedPace { get; init; } = SimulationPace.Watched;

        public int Alarms => _alarms?.Count ?? 0;

        public IReadOnlyList<string> SkippedAlarms => _skippedAlarms;

        public void StartSimulation(TimeSpan every)
        {
            lock (Lock)
            {
                foreach (var (state, _) in _variables) _middle[state] = state.Value;
            }
            _simulation = new Timer(_ => Simulate(), null, every, every);
        }

        public void StopSimulation()
        {
            _simulation?.Dispose();
            _simulation = null;
        }

        /// <summary>Numbers on a slow sine around their middle, each with its own phase; counters up; booleans toggle; texts stay.</summary>
        private void Simulate()
        {
            var seconds = (DateTime.UtcNow - _started).TotalSeconds;
            lock (Lock)
            {
                var i = 0;
                foreach (var (state, _) in _variables)
                {
                    var phase = i++ * 0.7;
                    _middle.TryGetValue(state, out var middle);
                    var watched = _alarms?.Watches(state) == true;
                    var next = ValueSimulation.Next(state.DataType, middle, seconds, phase, state.BrowseName?.Name, watched ? WatchedPace : null);
                    if (next == null || Equals(next, state.Value)) continue;
                    state.Value = next;
                    state.StatusCode = StatusCodes.Good;
                    state.Timestamp = DateTime.UtcNow;
                    state.ClearChangeMasks(SystemContext, false);
                    if (watched) _alarms!.Evaluate(state);
                }
            }
        }

        /// <summary>The values of the served variables from their elements again; the number that changed.</summary>
        public int Refresh()
        {
            var changed = 0;
            lock (Lock)
            {
                foreach (var (state, node) in _variables)
                {
                    if (node.Source?.Attribute["Value"] is not { } attribute) continue;
                    var (_, value) = Typed(attribute.Value, attribute.AttributeDataType ?? node.DataType);
                    if (Simulating && ValueSimulation.Moves(state.DataType))
                    {
                        // An edit in the document moves the middle; the simulation writes the value.
                        // A text or a date it does not move is written below, as without simulation.
                        _middle.TryGetValue(state, out var middle);
                        if (!Equals(middle, value)) { _middle[state] = value; changed++; }
                        continue;
                    }
                    if (Equals(value, state.Value) || (value is Array a && state.Value is Array b && a.Cast<object>().SequenceEqual(b.Cast<object>()))) continue;
                    state.Value = value;
                    state.StatusCode = value == null ? StatusCodes.UncertainInitialValue : StatusCodes.Good;
                    state.Timestamp = DateTime.UtcNow;
                    state.ClearChangeMasks(SystemContext, false);
                    _alarms?.Evaluate(state);
                    changed++;
                }
            }
            return changed;
        }

        /// <summary>
        /// The alarms of the document, once every value has its state: each on
        /// the object above its value, where its element stands in the document.
        /// </summary>
        private void AddAlarms()
        {
            if (_alarmNodes.Count == 0) return;
            _alarms = new ServedAlarms(SystemContext, NamespaceIndexes[0], n => AddPredefinedNode(SystemContext, n), AddRootNotifier);
            foreach (var node in _alarmNodes)
            {
                var alarm = node.Alarm!;
                var (input, inputNode) = _variables.FirstOrDefault(v => v.Node.Source?.Node == alarm.Input?.Node);
                if (input?.Parent is not { } source)
                {
                    _skippedAlarms.Add($"'{node.Name}': its value '{alarm.Input?.Name}' is not served as a variable.");
                    continue;
                }
                var definition = new AlarmDefinition(node.Name, inputNode.Address, alarm.Kind!.Value, alarm.Limit ?? 0, alarm.Normal ?? false, alarm.Severity, alarm.Message);
                _alarms.Add(definition, source, input, Id(node.Address), asComponent: true);
            }
        }

        /// <summary>The states of the alarms still open, to every event item that asked (ConditionRefresh).</summary>
        public override ServiceResult ConditionRefresh(OperationContext context, IList<IEventMonitoredItem> monitoredItems)
        {
            if (_alarms == null) return base.ConditionRefresh(context, monitoredItems);
            var events = new List<IFilterTarget>();
            lock (Lock) _alarms.ConditionRefresh(events);
            foreach (var item in monitoredItems)
                foreach (var e in events)
                    item.QueueEvent(e);
            return ServiceResult.Good;
        }

        public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
        {
            lock (Lock)
            {
                if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var refs))
                    externalReferences[ObjectIds.ObjectsFolder] = refs = new List<IReference>();

                foreach (var set in typeSets)
                {
                    using var stream = File.OpenRead(set.Path);
                    var nodes = new NodeStateCollection();
                    // Children linked to their parents: a type's declarations are then its children.
                    Opc.Ua.Export.UANodeSet.Read(stream).Import(SystemContext, nodes, linkParentChild: true);
                    foreach (var type in nodes.OfType<BaseTypeState>()) _types[type.NodeId] = type;
                }

                foreach (var folder in model.Roots)
                {
                    var state = Create(folder, null, null);
                    state.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
                    refs.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, state.NodeId));
                    AddPredefinedNode(SystemContext, state);
                }
                AddAlarms();
            }
        }

        private NodeId Id(UaNodeAddress address)
        {
            var ns = (ushort)Server.NamespaceUris.GetIndex(address.NamespaceUri);
            return address.IdType switch
            {
                UaIdType.Numeric => new NodeId(uint.Parse(address.Identifier, CultureInfo.InvariantCulture), ns),
                UaIdType.Guid => new NodeId(Guid.Parse(address.Identifier), ns),
                UaIdType.Opaque => new NodeId(Convert.FromBase64String(address.Identifier), ns),
                _ => new NodeId(address.Identifier, ns),
            };
        }

        /// <summary>
        /// The instance declarations of a served type and its supertypes, a subtype's
        /// own first. An element named like one is that declaration in the instance,
        /// so it takes its BrowseName, in the namespace of the type's model: a client
        /// that browses by the type's BrowseNames finds it (OPC 10000-3 6.3.3).
        /// </summary>
        private List<BaseInstanceState> DeclarationsOf(NodeId? type)
        {
            var all = new List<BaseInstanceState>();
            var seen = new HashSet<NodeId>();
            for (var t = type; t != null && !NodeId.IsNull(t) && seen.Add(t) && _types.TryGetValue(t, out var state); t = state.SuperTypeId)
            {
                var children = new List<BaseInstanceState>();
                state.GetChildren(SystemContext, children);
                foreach (var c in children)
                    if (!all.Any(a => a.BrowseName?.Name == c.BrowseName?.Name)) all.Add(c);
            }
            return all;
        }

        /// <summary>The node's UA type, where it is served; null for the base types.</summary>
        private NodeId? TypeOf(AmlNode node) =>
            node.TypeDefinition is { } type && typed != null && typed.Contains(type.NamespaceUri) && Server.NamespaceUris.GetIndex(type.NamespaceUri) >= 0
                ? Id(type) : null;

        /// <summary>A node of the document; <paramref name="declared"/> the declarations its parent's type and declaration hold.</summary>
        private NodeState Create(AmlNode node, NodeState? parent, IReadOnlyList<BaseInstanceState>? declared)
        {
            // An alarm is made with the stack's condition state once its value has a node (AddAlarms).
            if (node.Alarm != null && parent != null)
            {
                _alarmNodes.Add(node);
                return parent;
            }
            var declaration = declared?.FirstOrDefault(d => d.BrowseName?.Name == node.Name);
            var ns = (ushort)Server.NamespaceUris.GetIndex(node.Address.NamespaceUri);
            var nodeId = Id(node.Address);
            BaseInstanceState state;
            if (node.IsFolder)
            {
                state = new FolderState(parent) { TypeDefinitionId = ObjectTypeIds.FolderType };
            }
            else if (node.IsVariable)
            {
                var (dataType, value) = Typed(node.Value, node.DataType);
                state = new BaseDataVariableState(parent)
                {
                    TypeDefinitionId = TypeOf(node) ?? VariableTypeIds.BaseDataVariableType,
                    DataType = dataType,
                    ValueRank = ValueRanks.Scalar,
                    AccessLevel = AccessLevels.CurrentRead,
                    UserAccessLevel = AccessLevels.CurrentRead,
                    Value = value,
                    StatusCode = value == null ? StatusCodes.UncertainInitialValue : StatusCodes.Good,
                    Timestamp = DateTime.UtcNow,
                };
                _variables.Add(((BaseDataVariableState)state, node));
                if (node.Unit != null) AddEngineeringUnits(state, node.Unit, nodeId);
            }
            else
            {
                state = new BaseObjectState(parent) { TypeDefinitionId = TypeOf(node) ?? ObjectTypeIds.BaseObjectType };
            }
            state.NodeId = nodeId;
            state.BrowseName = declaration?.BrowseName ?? new QualifiedName(node.Name, ns);
            state.DisplayName = node.Name;
            state.ReferenceTypeId = node.IsFolder ? ReferenceTypeIds.Organizes : declaration?.ReferenceTypeId ?? ReferenceTypeIds.HasComponent;
            parent?.AddChild(state);
            // What the node's own type declares, then what its declaration holds below it.
            var below = DeclarationsOf(TypeOf(node));
            if (declaration != null)
            {
                var inner = new List<BaseInstanceState>();
                declaration.GetChildren(SystemContext, inner);
                foreach (var d in inner)
                    if (!below.Any(b => b.BrowseName?.Name == d.BrowseName?.Name)) below.Add(d);
            }
            foreach (var child in node.Children) Create(child, state, below.Count > 0 ? below : null);
            return state;
        }

        /// <summary>
        /// The unit of a variable as OPC UA states it: a property EngineeringUnits
        /// (EUInformation, OPC 10000-8) below it, its NodeId the variable's with
        /// "/EngineeringUnits" for a string NodeId, otherwise one of the namespace's own.
        /// </summary>
        private void AddEngineeringUnits(BaseInstanceState variable, string unit, NodeId variableId)
        {
            var eu = EngineeringUnit.From(unit);
            var property = new PropertyState<EUInformation>(variable)
            {
                TypeDefinitionId = VariableTypeIds.PropertyType,
                ReferenceTypeId = ReferenceTypeIds.HasProperty,
                DataType = DataTypeIds.EUInformation,
                ValueRank = ValueRanks.Scalar,
                AccessLevel = AccessLevels.CurrentRead,
                UserAccessLevel = AccessLevels.CurrentRead,
                BrowseName = BrowseNames.EngineeringUnits,
                DisplayName = BrowseNames.EngineeringUnits,
                Value = new EUInformation
                {
                    NamespaceUri = eu.NamespaceUri,
                    UnitId = eu.UnitId,
                    DisplayName = new LocalizedText(eu.DisplayName),
                    Description = new LocalizedText(eu.Description),
                },
                StatusCode = StatusCodes.Good,
                Timestamp = DateTime.UtcNow,
            };
            // The same at every start: from the variable's identifier, in its namespace.
            var of = variableId.IdType switch
            {
                IdType.String => (string)variableId.Identifier,
                IdType.Opaque => "b=" + Convert.ToBase64String((byte[])variableId.Identifier),
                IdType.Guid => "g=" + variableId.Identifier,
                _ => "i=" + Convert.ToString(variableId.Identifier, CultureInfo.InvariantCulture),
            };
            property.NodeId = new NodeId(of + "/" + BrowseNames.EngineeringUnits, variableId.NamespaceIndex);
            variable.AddChild(property);
        }

        private static (NodeId DataType, object? Value) Typed(string? text, string? xsType)
        {
            var inv = CultureInfo.InvariantCulture;
            try
            {
                return xsType switch
                {
                    "xs:boolean" => (DataTypeIds.Boolean, text == null ? null : text is "true" or "1"),
                    "xs:double" or "xs:decimal" => (DataTypeIds.Double, text == null ? null : XsDouble(text)),
                    "xs:float" => (DataTypeIds.Float, text == null ? null : XsFloat(text)),
                    "xs:int" => (DataTypeIds.Int32, text == null ? null : int.Parse(text, inv)),
                    "xs:long" or "xs:integer" => (DataTypeIds.Int64, text == null ? null : long.Parse(text, inv)),
                    "xs:short" => (DataTypeIds.Int16, text == null ? null : short.Parse(text, inv)),
                    "xs:unsignedInt" => (DataTypeIds.UInt32, text == null ? null : uint.Parse(text, inv)),
                    "xs:unsignedShort" => (DataTypeIds.UInt16, text == null ? null : ushort.Parse(text, inv)),
                    "xs:unsignedByte" => (DataTypeIds.Byte, text == null ? null : byte.Parse(text, inv)),
                    "xs:dateTime" => (DataTypeIds.DateTime, text == null ? null : System.Xml.XmlConvert.ToDateTime(text, System.Xml.XmlDateTimeSerializationMode.Utc)),
                    _ => (DataTypeIds.String, text),
                };
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                // A value its type cannot hold, typed while serving: shown as the text it is.
                return (DataTypeIds.String, text);
            }
        }

        /// <summary>
        /// The XML Schema lexical form of a double, and the "Infinity" ToAml
        /// writes. No thousands separator: "3,5" is no number (a decimal comma),
        /// not 35, so it is served as text.
        /// </summary>
        private static double XsDouble(string text) => text.Trim() switch
        {
            "INF" => double.PositiveInfinity,
            "-INF" => double.NegativeInfinity,
            var t => double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture),
        };

        /// <summary>The same for a float (<see cref="XsDouble"/>).</summary>
        private static float XsFloat(string text) => text.Trim() switch
        {
            "INF" => float.PositiveInfinity,
            "-INF" => float.NegativeInfinity,
            var t => float.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture),
        };
    }
}

/// <summary>The part of a document the server exposes, independent of the stack.</summary>
public sealed record AmlNode(string Name, UaNodeAddress Address, bool IsFolder, bool IsVariable, string? Value, string? DataType,
    IReadOnlyList<AmlNode> Children)
{
    /// <summary>The element the node was made from; its Value is read again on a refresh.</summary>
    public InternalElementType? Source { get; init; }

    /// <summary>The unit CAEX gives the value (the Unit of its attribute), served as EngineeringUnits.</summary>
    public string? Unit { get; init; }

    /// <summary>The NodeId of the element's UA type (its class's Annex A NodeId), if it is an instance of one.</summary>
    public UaNodeAddress? TypeDefinition { get; init; }

    /// <summary>The alarm the element describes, which the server raises; its children are the stack's.</summary>
    public AlarmElement? Alarm { get; init; }
}

public sealed class AmlAddressSpace
{
    public List<AmlNode> Roots { get; } = new();
    public List<string> NamespaceUris { get; } = new();
    public int Count { get; private set; }

    /// <summary>The models of the UA types the elements are instances of, the base model left out.</summary>
    public HashSet<string> TypeModels { get; } = new(StringComparer.Ordinal);

    /// <summary>Alarm elements the server does not raise, each with why; they are served as plain objects.</summary>
    public List<string> SkippedAlarms { get; } = new();

    /// <summary>
    /// Whether the server makes the element a variable: an instance of a
    /// VariableType, or without a UA class an element with a Value attribute.
    /// </summary>
    public static bool IsServedVariable(InternalElementType ie)
    {
        var type = UaTypes.IsUaLibraryPath(ie.RefBaseSystemUnitPath) ? UaTypes.TypeOf(ie) : null;
        return type != null ? UaTypes.Chain(type).Any(t => t.Name == "BaseVariableType") : ie.Attribute["Value"] != null;
    }

    public static AmlAddressSpace From(CAEXDocument doc, string documentNamespace)
    {
        var space = new AmlAddressSpace();
        space.NamespaceUris.Add(documentNamespace);
        var used = new HashSet<UaNodeAddress>();
        foreach (var ih in doc.CAEXFile.InstanceHierarchy)
        {
            var address = space.Unique(new UaNodeAddress(documentNamespace, UaIdType.String, ih.Name), used);
            var children = ih.InternalElement.Select(ie => space.Build(ie, ih.Name, documentNamespace, used)).ToList();
            space.Roots.Add(new AmlNode(ih.Name, address, true, false, null, null, children));
            space.Count++;
        }
        return space;
    }

    private AmlNode Build(InternalElementType ie, string parentPath, string documentNamespace, HashSet<UaNodeAddress> used)
    {
        var path = parentPath + "/" + ie.Name;
        UaNodeAddress? address = null;
        try { address = AnnexANodeId.Of(ie); }
        catch (AddressingException) { /* alias or browse path: fall back to the path */ }
        address = address != null ? address with { ServerUri = null } : new UaNodeAddress(documentNamespace, UaIdType.String, path);
        if (!NamespaceUris.Contains(address.NamespaceUri) && address.NamespaceUri != "http://opcfoundation.org/UA/")
            NamespaceUris.Add(address.NamespaceUri);
        if (address.NamespaceUri == "http://opcfoundation.org/UA/")
            address = new UaNodeAddress(documentNamespace, UaIdType.String, path);
        address = Unique(address, used);

        if (AlarmElements.IsAlarm(ie))
        {
            var alarm = AlarmElements.Read(ie);
            if (AlarmElements.Problem(alarm) is { } problem) SkippedAlarms.Add(problem);
            else
            {
                // Its children are the condition's own, which the stack makes; counted all the same,
                // so the document and the server agree on how many elements there are.
                Count += 1 + ie.Node.Descendants(ie.Node.Name.Namespace + "InternalElement").Count();
                return new AmlNode(ie.Name, address, false, false, null, null, []) { Source = ie, Alarm = alarm };
            }
        }

        var children = ie.InternalElement.Select(c => Build(c, path, documentNamespace, used)).ToList();
        var type = UaTypes.IsUaLibraryPath(ie.RefBaseSystemUnitPath) ? UaTypes.TypeOf(ie) : null;
        // Without a UA class, a Value makes a variable, children or not: a variable
        // taken from a server whose type the document lacks keeps its EURange and value.
        var isVariable = IsServedVariable(ie);
        var value = ie.Attribute["Value"];
        UaNodeAddress? typeId = null;
        if (type != null)
        {
            try { typeId = AnnexANodeId.Of(type) is { } t ? t with { ServerUri = null } : null; }
            catch (AddressingException) { /* a class without a usable NodeId serves as a base type */ }
        }
        if (typeId != null && typeId.NamespaceUri != "http://opcfoundation.org/UA/") TypeModels.Add(typeId.NamespaceUri);
        Count++;
        return new AmlNode(ie.Name, address, false, isVariable, value?.Value, value?.AttributeDataType, children)
        {
            Source = ie,
            TypeDefinition = typeId,
            // Not where the element models EngineeringUnits as a child of its own.
            Unit = isVariable && !string.IsNullOrWhiteSpace(value?.Unit) && children.All(c => c.Name != "EngineeringUnits") ? value!.Unit : null,
        };
    }

    /// <summary>Two elements can carry the same NodeId (copied instances); each further one gets "#2", "#3" … after the identifier.</summary>
    private UaNodeAddress Unique(UaNodeAddress address, HashSet<UaNodeAddress> used)
    {
        var candidate = address;
        for (var i = 2; !used.Add(candidate); i++)
            candidate = address with { IdType = UaIdType.String, Identifier = $"{address.Identifier}#{i}" };
        return candidate;
    }
}
