// An OPC UA server whose address space is the instance hierarchies of an AML
// document, so that clients can be tested against the engineering model
// before the plant exists.
//
// Each instance hierarchy becomes a folder under Objects. Each element becomes
// an Object, or a Variable if its UA type is a VariableType or it carries a
// Value attribute and no children. An element with an Annex A NodeId keeps
// it (its namespace is registered on the server); the others get string
// NodeIds from their path in a namespace of their own. Values come from the
// Value attributes, typed by their AttributeDataType. The address space is a
// snapshot of the document at start; restart to pick up changes.

using System.Globalization;
using Aml.Engine.CAEX;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;
using OpcUaAml.Addressing;
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

    /// <summary>Offer an unsecured endpoint besides the secured ones (for local testing).</summary>
    public bool AllowUnsecured { get; init; } = true;
}

public sealed class AmlServerHost : IAsyncDisposable
{
    private readonly StandardServer _server;

    private AmlServerHost(StandardServer server, string endpointUrl, int nodes)
    {
        _server = server;
        EndpointUrl = endpointUrl;
        Nodes = nodes;
    }

    public string EndpointUrl { get; }

    /// <summary>Number of nodes created from the document.</summary>
    public int Nodes { get; }

    public static async Task<AmlServerHost> StartAsync(CAEXDocument document, AmlServerOptions? options = null, CancellationToken ct = default)
    {
        options ??= new AmlServerOptions();
        var model = AmlAddressSpace.From(document, options.DocumentNamespace);
        var url = $"opc.tcp://localhost:{options.Port}/AMLOpcUa";

        var app = new ApplicationInstance { ApplicationName = "AMLOpcUa document server", ApplicationType = ApplicationType.Server };
        var builder = app.Build("urn:" + System.Net.Dns.GetHostName() + ":AMLOpcUa:DocumentServer", "uri:hsu-aut:AMLOpcUa")
            .AsServer(new[] { url });
        var withPolicies = options.AllowUnsecured
            ? builder.AddUnsecurePolicyNone().AddSignAndEncryptPolicies()
            : builder.AddSignAndEncryptPolicies();
        await withPolicies
            .AddSecurityConfiguration("CN=AMLOpcUa document server", options.PkiRoot)
            .SetAutoAcceptUntrustedCertificates(true)
            .CreateAsync(ct).ConfigureAwait(false);
        await app.CheckApplicationInstanceCertificatesAsync(false, null, ct).ConfigureAwait(false);

        var server = new DocumentServer(model);
        await app.StartAsync(server).ConfigureAwait(false);
        return new AmlServerHost(server, url, model.Count);
    }

    public async ValueTask DisposeAsync()
    {
        try { await _server.StopAsync().ConfigureAwait(false); }
        catch (Exception) { /* already stopped */ }
        _server.Dispose();
    }

    private sealed class DocumentServer(AmlAddressSpace model) : StandardServer
    {
        protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration) =>
            new(server, configuration, null, new DocumentNodeManager(server, configuration, model));
    }

    private sealed class DocumentNodeManager(IServerInternal server, ApplicationConfiguration configuration, AmlAddressSpace model)
        : CustomNodeManager2(server, configuration, model.NamespaceUris.ToArray())
    {
        public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
        {
            lock (Lock)
            {
                if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var refs))
                    externalReferences[ObjectIds.ObjectsFolder] = refs = new List<IReference>();

                foreach (var folder in model.Roots)
                {
                    var state = Create(folder, null);
                    state.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
                    refs.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, state.NodeId));
                    AddPredefinedNode(SystemContext, state);
                }
            }
        }

        private NodeState Create(AmlNode node, NodeState? parent)
        {
            var ns = (ushort)Server.NamespaceUris.GetIndex(node.Address.NamespaceUri);
            var nodeId = node.Address.IdType switch
            {
                UaIdType.Numeric => new NodeId(uint.Parse(node.Address.Identifier, CultureInfo.InvariantCulture), ns),
                UaIdType.Guid => new NodeId(Guid.Parse(node.Address.Identifier), ns),
                UaIdType.Opaque => new NodeId(Convert.FromBase64String(node.Address.Identifier), ns),
                _ => new NodeId(node.Address.Identifier, ns),
            };
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
                    TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
                    DataType = dataType,
                    ValueRank = ValueRanks.Scalar,
                    AccessLevel = AccessLevels.CurrentRead,
                    UserAccessLevel = AccessLevels.CurrentRead,
                    Value = value,
                    StatusCode = value == null ? StatusCodes.UncertainInitialValue : StatusCodes.Good,
                    Timestamp = DateTime.UtcNow,
                };
            }
            else
            {
                state = new BaseObjectState(parent) { TypeDefinitionId = ObjectTypeIds.BaseObjectType };
            }
            state.NodeId = nodeId;
            state.BrowseName = new QualifiedName(node.Name, ns);
            state.DisplayName = node.Name;
            state.ReferenceTypeId = node.IsFolder ? ReferenceTypeIds.Organizes : ReferenceTypeIds.HasComponent;
            parent?.AddChild(state);
            foreach (var child in node.Children) Create(child, state);
            return state;
        }

        private static (NodeId DataType, object? Value) Typed(string? text, string? xsType)
        {
            var inv = CultureInfo.InvariantCulture;
            try
            {
                return xsType switch
                {
                    "xs:boolean" => (DataTypeIds.Boolean, text == null ? null : text is "true" or "1"),
                    "xs:double" or "xs:decimal" => (DataTypeIds.Double, text == null ? null : double.Parse(text, inv)),
                    "xs:float" => (DataTypeIds.Float, text == null ? null : float.Parse(text, inv)),
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
            catch (FormatException)
            {
                return (DataTypeIds.String, text);
            }
        }
    }
}

/// <summary>The part of a document the server exposes, independent of the stack.</summary>
public sealed record AmlNode(string Name, UaNodeAddress Address, bool IsFolder, bool IsVariable, string? Value, string? DataType,
    IReadOnlyList<AmlNode> Children);

public sealed class AmlAddressSpace
{
    public List<AmlNode> Roots { get; } = new();
    public List<string> NamespaceUris { get; } = new();
    public int Count { get; private set; }

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

        var children = ie.InternalElement.Select(c => Build(c, path, documentNamespace, used)).ToList();
        var type = UaTypes.IsUaLibraryPath(ie.RefBaseSystemUnitPath) ? UaTypes.TypeOf(ie) : null;
        var isVariable = type != null
            ? UaTypes.Chain(type).Any(t => t.Name == "BaseVariableType")
            : ie.Attribute["Value"] != null && children.Count == 0;
        var value = ie.Attribute["Value"];
        Count++;
        return new AmlNode(ie.Name, address, false, isVariable, value?.Value, value?.AttributeDataType, children);
    }

    /// <summary>Two elements can carry the same NodeId (copied instances); the second gets a path id.</summary>
    private UaNodeAddress Unique(UaNodeAddress address, HashSet<UaNodeAddress> used)
    {
        var candidate = address;
        for (var i = 2; !used.Add(candidate); i++)
            candidate = address with { IdType = UaIdType.String, Identifier = $"{address.Identifier}#{i}" };
        return candidate;
    }
}
