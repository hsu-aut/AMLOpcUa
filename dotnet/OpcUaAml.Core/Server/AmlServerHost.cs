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
        // The stack listens on every address for a host name, on that address alone for an IP address.
        var host = options.Network ? System.Net.Dns.GetHostName() : "127.0.0.1";
        var url = $"opc.tcp://{host}:{options.Port}/AMLOpcUa";

        var app = new ApplicationInstance { ApplicationName = "AMLOpcUa document server", ApplicationType = ApplicationType.Server };
        var builder = app.Build("urn:" + System.Net.Dns.GetHostName() + ":AMLOpcUa:DocumentServer", "uri:hsu-aut:AMLOpcUa")
            .AsServer(new[] { url });
        var withPolicies = options.Network
            ? builder.AddSignAndEncryptPolicies()
            : builder.AddUnsecurePolicyNone().AddSignAndEncryptPolicies();
        await withPolicies
            .AddSecurityConfiguration("CN=AMLOpcUa document server", options.PkiRoot)
            .SetAutoAcceptUntrustedCertificates(!options.Network)
            .CreateAsync(ct).ConfigureAwait(false);
        var replaced = false;
        try
        {
            await app.CheckApplicationInstanceCertificatesAsync(false, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The certificate names the addresses it was made for (the computer's name, or
            // 127.0.0.1); served under another, the stack refuses it. It is the server's own,
            // so a new one is made. Clients that trusted the old one are asked again.
            foreach (var folder in new[] { "certs", "private" }.Select(f => Path.Combine(options.PkiRoot, "own", f)).Where(Directory.Exists))
                foreach (var file in Directory.GetFiles(folder)) File.Delete(file);
            app.ApplicationConfiguration.SecurityConfiguration.ApplicationCertificate.Certificate = null;
            await app.CheckApplicationInstanceCertificatesAsync(false, null, ct).ConfigureAwait(false);
            replaced = true;
        }

        var server = new DocumentServer(model);
        try
        {
            await app.StartAsync(server).ConfigureAwait(false);
        }
        catch
        {
            server.Dispose();
            throw;
        }
        return new AmlServerHost(server, url, model.Count, document, options.DocumentNamespace) { CertificateReplaced = replaced };
    }

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

    public async ValueTask DisposeAsync()
    {
        try { await _server.StopAsync().ConfigureAwait(false); }
        catch (Exception) { /* already stopped */ }
        _server.Dispose();
    }

    private sealed class DocumentServer(AmlAddressSpace model) : StandardServer
    {
        public DocumentNodeManager? NodeManager { get; private set; }

        protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        {
            NodeManager = new DocumentNodeManager(server, configuration, model);
            return new(server, configuration, null, NodeManager);
        }
    }

    private sealed class DocumentNodeManager(IServerInternal server, ApplicationConfiguration configuration, AmlAddressSpace model)
        : CustomNodeManager2(server, configuration, model.NamespaceUris.ToArray())
    {
        private readonly List<(BaseDataVariableState State, AmlNode Node)> _variables = new();

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
                    if (Equals(value, state.Value) || (value is Array a && state.Value is Array b && a.Cast<object>().SequenceEqual(b.Cast<object>()))) continue;
                    state.Value = value;
                    state.StatusCode = value == null ? StatusCodes.UncertainInitialValue : StatusCodes.Good;
                    state.Timestamp = DateTime.UtcNow;
                    state.ClearChangeMasks(SystemContext, false);
                    changed++;
                }
            }
            return changed;
        }

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
                _variables.Add(((BaseDataVariableState)state, node));
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
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                // A value its type cannot hold, typed while serving: shown as the text it is.
                return (DataTypeIds.String, text);
            }
        }
    }
}

/// <summary>The part of a document the server exposes, independent of the stack.</summary>
public sealed record AmlNode(string Name, UaNodeAddress Address, bool IsFolder, bool IsVariable, string? Value, string? DataType,
    IReadOnlyList<AmlNode> Children)
{
    /// <summary>The element the node was made from; its Value is read again on a refresh.</summary>
    public InternalElementType? Source { get; init; }
}

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
        return new AmlNode(ie.Name, address, false, isVariable, value?.Value, value?.AttributeDataType, children) { Source = ie };
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
