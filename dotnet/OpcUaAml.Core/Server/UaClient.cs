// A small OPC UA client for what engineering needs from a running server:
// connect, browse the address space, read values and the namespace table.
//
// Wraps the OPC Foundation .NET Standard stack. Addresses cross this boundary
// as UaNodeAddress (namespace URI, never an index), so nothing above this
// class depends on a session's namespace table.

using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using OpcUaAml.Addressing;

namespace OpcUaAml.Server;

public sealed class UaConnectOptions
{
    public required string EndpointUrl { get; init; }

    /// <summary>
    /// Connect secured (Sign or SignAndEncrypt) only; a server that offers no
    /// secured endpoint is refused rather than used unsecured. Off: the
    /// unsecured endpoint is taken, and a user name is refused, since its
    /// password would travel in clear text.
    /// </summary>
    public bool UseSecurity { get; init; } = true;

    /// <summary>Anonymous when null.</summary>
    public string? UserName { get; init; }
    public string? Password { get; init; }

    /// <summary>
    /// Trust a server certificate the client has not seen before. Off by
    /// default: the first connection to an unknown server then fails with a
    /// message naming the certificate, and the caller decides.
    /// </summary>
    public bool AcceptUntrustedServerCertificates { get; init; }

    /// <summary>Where the client keeps its own certificate and the trust lists.</summary>
    public string PkiRoot { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AMLOpcUa", "pki");

    public int OperationTimeoutMs { get; init; } = 15000;
}

public sealed class UaConnectionException : Exception
{
    public UaConnectionException(string message, Exception? inner = null) : base(message, inner) { }

    /// <summary>The server's certificate, when the connection failed because it is not trusted.</summary>
    public ServerCertificate? UntrustedCertificate { get; init; }
}

/// <summary>A server's certificate, as the user checks it before trusting it.</summary>
public sealed record ServerCertificate(string Subject, string Issuer, string Sha256, DateTime NotBefore, DateTime NotAfter, byte[] Raw)
{
    public static ServerCertificate From(byte[] raw)
    {
        using var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(raw);
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(raw));
        return new ServerCertificate(cert.Subject, cert.Issuer, string.Join(":", sha.Chunk(2).Select(c => new string(c))),
            cert.NotBefore, cert.NotAfter, raw);
    }
}

/// <summary>A node found while browsing.</summary>
public sealed record UaBrowseItem(
    UaNodeAddress Address,
    string BrowseName,
    string DisplayName,
    string NodeClass,
    UaNodeAddress? TypeDefinition,
    string ReferenceType);

/// <summary>The value of a variable as read, with its status.</summary>
public sealed record UaReadResult(UaNodeAddress Address, object? Value, string? DataType, bool Good, string Status)
{
    /// <summary>The value as AML stores it: invariant culture, arrays space separated.</summary>
    public string? ValueText => ValueFormatting.ToAml(Value);
}

public sealed class UaClient : IAsyncDisposable
{
    private readonly ISession _session;

    private UaClient(ISession session, string endpointUrl, string securityMode)
    {
        _session = session;
        EndpointUrl = endpointUrl;
        SecurityMode = securityMode;
        _session.KeepAliveInterval = 5000;
        _session.KeepAlive += OnKeepAlive;
    }

    /// <summary>
    /// Whether the server answered the last keep-alive. A session whose server
    /// went away is not ended by the stack; this says so within seconds.
    /// </summary>
    public bool Reachable { get; private set; } = true;

    /// <summary>Raised, on a thread of the stack, when <see cref="Reachable"/> changes.</summary>
    public event Action<bool>? ReachableChanged;

    private void OnKeepAlive(ISession session, KeepAliveEventArgs e)
    {
        var reachable = ServiceResult.IsGood(e.Status);
        if (reachable == Reachable) return;
        Reachable = reachable;
        ReachableChanged?.Invoke(reachable);
    }

    public string EndpointUrl { get; }
    public string SecurityMode { get; }

    /// <summary>More references of one node than any real server has: a server that sends more is not answered further.</summary>
    public const int MaxReferencesPerNode = 100000;

    /// <summary>The session, for the classes of this library that need more than browsing and reading.</summary>
    internal ISession Session => _session;

    /// <summary>The server's namespace table at connect time.</summary>
    public IReadOnlyList<string> NamespaceTable => _session.NamespaceUris.ToArray();

    /// <summary>The server's ApplicationUri (ServerArray[0]).</summary>
    public string? ServerUri => _session.ServerUris.Count > 0 ? _session.ServerUris.GetString(0) : null;

    public static async Task<UaClient> ConnectAsync(UaConnectOptions options, CancellationToken ct = default)
    {
        ApplicationConfiguration config;
        try
        {
            config = await CreateConfigurationAsync(options, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new UaConnectionException($"The client's certificate in '{options.PkiRoot}' could not be set up: {ex.Message}", ex);
        }
        EndpointDescription endpoint;
        try
        {
            endpoint = await CoreClientUtils.SelectEndpointAsync(config, options.EndpointUrl, options.UseSecurity,
                options.OperationTimeoutMs, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new UaConnectionException($"No endpoint at '{options.EndpointUrl}': {ex.Message}", ex);
        }
        if (options.UseSecurity && endpoint.SecurityMode == MessageSecurityMode.None)
            throw new UaConnectionException($"'{options.EndpointUrl}' offers no secured endpoint. Switch security off to connect unsecured.");
        if (!options.UseSecurity && endpoint.SecurityMode == MessageSecurityMode.None && options.UserName != null)
            throw new UaConnectionException("A user name over an unsecured connection would send its password in clear text. Switch security on, or connect anonymously.");

        var configured = new ConfiguredEndpoint(null, endpoint, EndpointConfiguration.Create(config));
        IUserIdentity identity = options.UserName == null
            ? new UserIdentity(new AnonymousIdentityToken())
            : new UserIdentity(options.UserName, System.Text.Encoding.UTF8.GetBytes(options.Password ?? ""));
        try
        {
            var session = await new DefaultSessionFactory().CreateAsync(config, configured, false, "AMLOpcUa",
                60000, identity, null, ct).ConfigureAwait(false);
            return new UaClient(session, options.EndpointUrl, $"{endpoint.SecurityMode} {SecurityPolicies.GetDisplayName(endpoint.SecurityPolicyUri)}");
        }
        catch (ServiceResultException ex) when (ex.StatusCode == StatusCodes.BadCertificateUntrusted
                                                 || ex.StatusCode == StatusCodes.BadCertificateChainIncomplete)
        {
            throw new UaConnectionException(
                $"The server certificate of '{options.EndpointUrl}' is not trusted. Trust it, or copy it into " +
                $"'{Path.Combine(options.PkiRoot, "trusted", "certs")}'.", ex)
            {
                UntrustedCertificate = endpoint.ServerCertificate is { Length: > 0 } raw ? ServerCertificate.From(raw) : null,
            };
        }
        catch (Exception ex)
        {
            throw new UaConnectionException($"Connecting to '{options.EndpointUrl}' failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Trusts exactly this server certificate from now on: it is added to the
    /// client's trusted certificates, so later connections check it again.
    /// </summary>
    public static void TrustServer(ServerCertificate certificate, string? pkiRoot = null)
    {
        var folder = Path.Combine(pkiRoot ?? new UaConnectOptions { EndpointUrl = "" }.PkiRoot, "trusted", "certs");
        Directory.CreateDirectory(folder);
        var name = new string(certificate.Subject.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '.' ? c : '_').ToArray()).Trim();
        File.WriteAllBytes(Path.Combine(folder, $"{name} [{certificate.Sha256.Replace(":", "")[..16]}].der"), certificate.Raw);
    }

    private static async Task<ApplicationConfiguration> CreateConfigurationAsync(UaConnectOptions options, CancellationToken ct)
    {
        var app = new ApplicationInstance
        {
            ApplicationName = "AMLOpcUa",
            ApplicationType = ApplicationType.Client,
        };
        var config = await app.Build("urn:" + System.Net.Dns.GetHostName() + ":AMLOpcUa", "uri:hsu-aut:AMLOpcUa")
            .AsClient()
            .AddSecurityConfiguration("CN=AMLOpcUa, O=AMLOpcUa", options.PkiRoot)
            .SetAutoAcceptUntrustedCertificates(options.AcceptUntrustedServerCertificates)
            .CreateAsync(ct).ConfigureAwait(false);
        config.ClientConfiguration.DefaultSessionTimeout = 60000;
        config.TransportQuotas.OperationTimeout = options.OperationTimeoutMs;
        await app.CheckApplicationInstanceCertificatesAsync(false, null, ct).ConfigureAwait(false);
        return config;
    }

    /// <summary>
    /// The children of a node along hierarchical references (the Objects
    /// folder when <paramref name="node"/> is null).
    /// </summary>
    public Task<IReadOnlyList<UaBrowseItem>> BrowseAsync(UaNodeAddress? node = null, CancellationToken ct = default) =>
        BrowseAsync(node, null, ct);

    /// <summary>
    /// The children of a node along hierarchical references, restricted to the
    /// references of a View when <paramref name="view"/> is given.
    /// </summary>
    public Task<IReadOnlyList<UaBrowseItem>> BrowseAsync(UaNodeAddress? node, UaNodeAddress? view, CancellationToken ct = default) =>
        BrowseCoreAsync(node == null ? ObjectIds.ObjectsFolder : ToNodeId(node), BrowseDirection.Forward,
            NodeClass.Object | NodeClass.Variable | NodeClass.Method, view, ct);

    /// <summary>The Views of the server (the Views folder's children).</summary>
    public Task<IReadOnlyList<UaBrowseItem>> ViewsAsync(CancellationToken ct = default) =>
        BrowseCoreAsync(ObjectIds.ViewsFolder, BrowseDirection.Forward, NodeClass.View, null, ct);

    /// <summary>The node itself: names, NodeClass and type definition.</summary>
    public async Task<UaBrowseItem> DescribeAsync(UaNodeAddress node, CancellationToken ct = default)
    {
        var id = ToNodeId(node);
        var request = new ReadValueIdCollection(new[] { Attributes.BrowseName, Attributes.DisplayName, Attributes.NodeClass }
            .Select(a => new ReadValueId { NodeId = id, AttributeId = a }));
        var response = await _session.ReadAsync(null, 0, TimestampsToReturn.Neither, request, ct).ConfigureAwait(false);
        if (StatusCode.IsBad(response.Results[2].StatusCode))
            throw new UaConnectionException($"The server has no node {node}.");
        var type = (await BrowseCoreAsync(id, BrowseDirection.Forward, NodeClass.Unspecified, null, ct, ReferenceTypeIds.HasTypeDefinition)
            .ConfigureAwait(false)).FirstOrDefault();
        var nodeClass = (NodeClass)(int)response.Results[2].Value;
        return new UaBrowseItem(node with { ServerUri = null }, (response.Results[0].Value as QualifiedName)?.Name ?? node.Identifier,
            (response.Results[1].Value as LocalizedText)?.Text ?? node.Identifier, nodeClass.ToString(), type?.Address, "");
    }

    /// <summary>
    /// The nodes from the Objects or Views folder down to <paramref name="node"/>,
    /// both included, along the hierarchical references a node is held by.
    /// A node that no hierarchical reference leads to is its own path.
    /// </summary>
    public async Task<IReadOnlyList<UaBrowseItem>> PathAsync(UaNodeAddress node, CancellationToken ct = default)
    {
        var self = await DescribeAsync(node, ct).ConfigureAwait(false);
        var path = new List<UaBrowseItem> { self };
        var seen = new HashSet<UaNodeAddress> { self.Address };
        var roots = new[] { FromExpanded(ObjectIds.ObjectsFolder), FromExpanded(ObjectIds.ViewsFolder) };
        var current = self;
        for (var i = 0; i < 64 && !roots.Contains(current.Address); i++)
        {
            var holders = await BrowseCoreAsync(ToNodeId(current.Address), BrowseDirection.Inverse,
                NodeClass.Object | NodeClass.Variable | NodeClass.View, null, ct).ConfigureAwait(false);
            var holder = holders.Where(h => h.ReferenceType != "HasSubtype" && !seen.Contains(h.Address))
                .OrderBy(h => Array.IndexOf(PreferredHolders, h.ReferenceType) is var p && p >= 0 ? p : PreferredHolders.Length)
                .FirstOrDefault();
            if (holder == null) break;
            // The reference that holds a node is named on the node, as a browse from its holder would name it.
            path[0] = path[0] with { ReferenceType = holder.ReferenceType };
            path.Insert(0, holder with { ReferenceType = "" });
            seen.Add(holder.Address);
            current = holder;
        }
        return path;
    }

    private static readonly string[] PreferredHolders = { "HasComponent", "HasOrderedComponent", "Organizes", "HasProperty" };

    /// <summary>A type and its subtypes.</summary>
    public async Task<IReadOnlySet<UaNodeAddress>> SubtypesAsync(UaNodeAddress type, CancellationToken ct = default)
    {
        var result = new HashSet<UaNodeAddress> { type with { ServerUri = null } };
        var frontier = new List<UaNodeAddress> { type };
        while (frontier.Count > 0)
        {
            var next = new List<UaNodeAddress>();
            foreach (var t in frontier)
            {
                var subtypes = await BrowseCoreAsync(ToNodeId(t), BrowseDirection.Forward, NodeClass.Unspecified, null, ct,
                    ReferenceTypeIds.HasSubtype).ConfigureAwait(false);
                next.AddRange(subtypes.Select(s => s.Address).Where(result.Add));
            }
            frontier = next;
        }
        return result;
    }

    /// <summary>
    /// The ObjectTypes and VariableTypes of the server, found along HasSubtype
    /// from BaseObjectType and BaseVariableType.
    /// </summary>
    public async Task<IReadOnlyList<UaBrowseItem>> TypesAsync(int maxTypes = 50000, CancellationToken ct = default)
    {
        var result = new List<UaBrowseItem>();
        // A server may, by mistake or on purpose, make HasSubtype run in a circle.
        var seen = new HashSet<UaNodeAddress>();
        foreach (var root in new[] { ObjectTypeIds.BaseObjectType, VariableTypeIds.BaseVariableType })
        {
            var frontier = new List<NodeId> { root };
            while (frontier.Count > 0 && result.Count < maxTypes)
            {
                var next = new List<NodeId>();
                foreach (var t in frontier)
                {
                    var subtypes = await BrowseCoreAsync(t, BrowseDirection.Forward, NodeClass.ObjectType | NodeClass.VariableType, null, ct,
                        ReferenceTypeIds.HasSubtype).ConfigureAwait(false);
                    foreach (var s in subtypes.Where(s => seen.Add(s.Address)))
                    {
                        result.Add(s);
                        next.Add(ToNodeId(s.Address));
                    }
                }
                frontier = next;
            }
        }
        return result;
    }

    /// <summary>
    /// Objects and variables below <paramref name="start"/> whose type is one of
    /// <paramref name="types"/>; below a match the search does not go on, the
    /// match's subtree belongs to it. The Server object is not searched.
    /// </summary>
    public async Task<IReadOnlyList<UaBrowseItem>> InstancesOfAsync(UaNodeAddress start, IReadOnlySet<UaNodeAddress> types,
        int maxNodes = 20000, CancellationToken ct = default)
    {
        var result = new List<UaBrowseItem>();
        var server = FromExpanded(ObjectIds.Server);
        var seen = new HashSet<UaNodeAddress> { start with { ServerUri = null } };
        var frontier = new List<UaNodeAddress> { start };
        while (frontier.Count > 0 && seen.Count < maxNodes)
        {
            var next = new List<UaNodeAddress>();
            foreach (var node in frontier)
            {
                if (seen.Count >= maxNodes) break;
                foreach (var child in await BrowseAsync(node, ct).ConfigureAwait(false))
                {
                    if (child.NodeClass == "Method" || child.Address == server || !seen.Add(child.Address)) continue;
                    if (child.TypeDefinition != null && types.Contains(child.TypeDefinition)) result.Add(child);
                    else next.Add(child.Address);
                }
            }
            frontier = next;
        }
        return result;
    }

    private async Task<IReadOnlyList<UaBrowseItem>> BrowseCoreAsync(NodeId start, BrowseDirection direction, NodeClass classes,
        UaNodeAddress? view, CancellationToken ct, NodeId? referenceType = null)
    {
        var browser = new BrowseDescription
        {
            NodeId = start,
            BrowseDirection = direction,
            ReferenceTypeId = referenceType ?? ReferenceTypeIds.HierarchicalReferences,
            IncludeSubtypes = true,
            NodeClassMask = (uint)classes,
            ResultMask = (uint)BrowseResultMask.All,
        };
        var viewDescription = view == null ? null : new ViewDescription { ViewId = ToNodeId(view) };
        var items = new List<UaBrowseItem>();
        var response = await _session.BrowseAsync(null, viewDescription, 0, new BrowseDescriptionCollection { browser }, ct).ConfigureAwait(false);
        var result = response.Results[0];
        if (StatusCode.IsBad(result.StatusCode)) throw new UaConnectionException($"Browsing {start} failed: {result.StatusCode}.");
        Add(result.References);
        var continuation = result.ContinuationPoint;
        while (continuation != null && continuation.Length > 0)
        {
            if (items.Count > MaxReferencesPerNode)
                throw new UaConnectionException($"Browsing {start}: the server keeps sending references (more than {MaxReferencesPerNode}).");
            var next = await _session.BrowseNextAsync(null, false, new ByteStringCollection { continuation }, ct).ConfigureAwait(false);
            Add(next.Results[0].References);
            continuation = next.Results[0].ContinuationPoint;
        }
        return items;

        void Add(ReferenceDescriptionCollection refs)
        {
            foreach (var r in refs)
            {
                // Another server's node, or one in a namespace the server's own table lacks.
                if (r.NodeId.ServerIndex != 0 || ExpandedNodeId.ToNodeId(r.NodeId, _session.NamespaceUris) == null) continue;
                items.Add(new UaBrowseItem(
                    FromExpanded(r.NodeId),
                    r.BrowseName.Name,
                    r.DisplayName.Text,
                    r.NodeClass.ToString(),
                    r.TypeDefinition != null && !NodeId.IsNull(r.TypeDefinition) ? FromExpanded(r.TypeDefinition) : null,
                    ReferenceName(r.ReferenceTypeId)));
            }
        }
    }

    /// <summary>
    /// The node a text names: a path of BrowseNames from the Objects folder
    /// when it starts with a slash ("/Plant/Pump1/Motor/Temperature"),
    /// otherwise a NodeId as the server writes it. A step of a path matches a
    /// BrowseName or a DisplayName, so nobody has to know the NodeIds of a
    /// server to read one of its values. A NodeId is never split, because both
    /// its namespace and its identifier may hold slashes.
    /// </summary>
    public async Task<UaNodeAddress> ResolveAsync(string text, CancellationToken ct = default)
    {
        if (!text.StartsWith('/')) return UaNodeAddress.Parse(text, NamespaceTable);

        UaNodeAddress? node = null;
        foreach (var step in text.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var children = await BrowseAsync(node, ct).ConfigureAwait(false);
            var hit = children.FirstOrDefault(c => c.BrowseName == step || c.DisplayName == step)
                      ?? children.FirstOrDefault(c => c.BrowseName.EndsWith(":" + step, StringComparison.Ordinal));
            node = hit?.Address ?? throw new ArgumentException(
                $"'{step}' is not below {(node == null ? "the Objects folder" : node.ToString())}.", nameof(text));
        }
        return node ?? throw new ArgumentException("A node is needed.", nameof(text));
    }

    public async Task<UaReadResult> ReadAsync(UaNodeAddress address, CancellationToken ct = default) =>
        (await ReadManyAsync(new[] { address }, ct).ConfigureAwait(false))[0];

    /// <summary>Reads the Value attribute of several variables in one request.</summary>
    public async Task<IReadOnlyList<UaReadResult>> ReadManyAsync(IReadOnlyList<UaNodeAddress> addresses, CancellationToken ct = default)
    {
        var results = new UaReadResult[addresses.Count];
        var request = new ReadValueIdCollection();
        var map = new List<int>();
        for (var i = 0; i < addresses.Count; i++)
        {
            try
            {
                request.Add(new ReadValueId { NodeId = ToNodeId(addresses[i]), AttributeId = Attributes.Value });
                map.Add(i);
            }
            catch (FormatException ex)
            {
                results[i] = new UaReadResult(addresses[i], null, null, false, ex.Message);
            }
        }
        if (request.Count > 0)
        {
            var response = await _session.ReadAsync(null, 0, TimestampsToReturn.Neither, request, ct).ConfigureAwait(false);
            for (var k = 0; k < response.Results.Count; k++)
            {
                var dv = response.Results[k];
                var i = map[k];
                results[i] = new UaReadResult(addresses[i], dv.Value, dv.WrappedValue.TypeInfo?.BuiltInType.ToString(),
                    StatusCode.IsGood(dv.StatusCode), StatusCode.LookupSymbolicId(dv.StatusCode.Code) ?? dv.StatusCode.ToString());
            }
        }
        return results;
    }

    /// <summary>
    /// Subscribes to value changes of the given variables. <paramref name="onChange"/>
    /// runs on a stack thread for each change; dispose the result to stop.
    /// </summary>
    public async Task<IAsyncDisposable> WatchAsync(IReadOnlyList<UaNodeAddress> addresses, Action<UaReadResult> onChange,
        int publishingIntervalMs = 500, CancellationToken ct = default)
    {
        var telemetry = _session.MessageContext.Telemetry;
        var subscription = new Subscription(telemetry, new SubscriptionOptions
        {
            DisplayName = "AMLOpcUa watch",
            PublishingInterval = publishingIntervalMs,
            PublishingEnabled = true,
            KeepAliveCount = 10,
            LifetimeCount = 100,
        });
        foreach (var address in addresses)
        {
            NodeId nodeId;
            try { nodeId = ToNodeId(address); }
            catch (FormatException ex)
            {
                onChange(new UaReadResult(address, null, null, false, ex.Message));
                continue;
            }
            var item = new MonitoredItem(telemetry, new MonitoredItemOptions
            {
                StartNodeId = nodeId,
                AttributeId = Attributes.Value,
                SamplingInterval = publishingIntervalMs,
                QueueSize = 1,
                DiscardOldest = true,
                DisplayName = address.Identifier,
            });
            var captured = address;
            item.Notification += (_, e) =>
            {
                if (e.NotificationValue is not MonitoredItemNotification n) return;
                var dv = n.Value;
                onChange(new UaReadResult(captured, dv.Value, dv.WrappedValue.TypeInfo?.BuiltInType.ToString(),
                    StatusCode.IsGood(dv.StatusCode), StatusCode.LookupSymbolicId(dv.StatusCode.Code) ?? dv.StatusCode.ToString()));
            };
            subscription.AddItem(item);
        }
        _session.AddSubscription(subscription);
        try
        {
            await subscription.CreateAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // Refused by the server: not left behind on the session.
            await new Watch(_session, subscription).DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return new Watch(_session, subscription);
    }

    private sealed class Watch(ISession session, Subscription subscription) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await session.RemoveSubscriptionAsync(subscription, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { /* session already closed */ }
            subscription.Dispose();
        }
    }

    /// <summary>
    /// The node an indirect NodeId names on this server: a browse path through
    /// TranslateBrowsePathsToNodeIds, an alias through FindAlias of the Aliases
    /// object (OPC 10000-17).
    /// </summary>
    /// <exception cref="AddressingException">The server does not know the path or the alias, or it is ambiguous.</exception>
    public async Task<UaNodeAddress> ResolveAsync(IndirectNodeId id, CancellationToken ct = default)
    {
        switch (id)
        {
            case BrowsePathNodeId path:
            {
                var relative = new RelativePath();
                foreach (var step in path.Steps)
                {
                    var index = step.TargetNamespace == null ? (ushort)0 : (ushort)_session.NamespaceUris.GetIndex(step.TargetNamespace);
                    if (step.TargetNamespace != null && index == ushort.MaxValue)
                        throw new AddressingException($"{path}: the server does not know the namespace {step.TargetNamespace}.");
                    relative.Elements.Add(new RelativePathElement
                    {
                        ReferenceTypeId = step.ReferenceType == null ? ReferenceTypeIds.HierarchicalReferences : ToNodeId(step.ReferenceType),
                        IsInverse = step.IsInverse,
                        IncludeSubtypes = step.IncludeSubtypes,
                        TargetName = new QualifiedName(step.TargetName, index),
                    });
                }
                var request = new BrowsePathCollection { new BrowsePath { StartingNode = ToNodeId(path.Root), RelativePath = relative } };
                var response = await _session.TranslateBrowsePathsToNodeIdsAsync(null, request, ct).ConfigureAwait(false);
                var result = response.Results[0];
                if (StatusCode.IsBad(result.StatusCode) || result.Targets.Count == 0)
                    throw new AddressingException($"{path}: {StatusCode.LookupSymbolicId(result.StatusCode.Code) ?? result.StatusCode.ToString()}.");
                if (result.Targets.Count > 1)
                    throw new AddressingException($"{path}: the path leads to {result.Targets.Count} nodes.");
                return FromExpanded(result.Targets[0].TargetId);
            }
            case AliasNodeId alias:
            {
                // Aliases objects and their FindAlias methods, found by name: servers need not use the
                // base model's NodeIds, and a server may carry the base model's Aliases object without
                // implementing it next to one that works. Each is asked until one answers.
                var candidates = (await BrowseAsync(null, ct).ConfigureAwait(false)).Where(i => i.BrowseName == "Aliases").ToList();
                if (candidates.Count == 0) throw new AddressingException($"{alias}: the server has no Aliases object (OPC 10000-17).");
                string? failure = null;
                foreach (var aliases in candidates)
                {
                    var find = (await BrowseAsync(aliases.Address, ct).ConfigureAwait(false))
                        .FirstOrDefault(i => i.BrowseName == "FindAlias" && i.NodeClass == "Method");
                    if (find == null) { failure ??= "the Aliases object has no FindAlias method"; continue; }
                    var call = new CallMethodRequestCollection
                    {
                        new CallMethodRequest
                        {
                            ObjectId = ToNodeId(aliases.Address),
                            MethodId = ToNodeId(find.Address),
                            InputArguments = new VariantCollection
                            {
                                new Variant(alias.AliasName),
                                new Variant(alias.ReferenceTypeFilter == null ? NodeId.Null : ToNodeId(alias.ReferenceTypeFilter)),
                            },
                        },
                    };
                    var response = await _session.CallAsync(null, call, ct).ConfigureAwait(false);
                    var result = response.Results[0];
                    if (StatusCode.IsBad(result.StatusCode))
                    {
                        failure = StatusCode.LookupSymbolicId(result.StatusCode.Code) ?? result.StatusCode.ToString();
                        continue;
                    }
                    var found = (result.OutputArguments.Count > 0 ? result.OutputArguments[0].Value as ExtensionObject[] : null) ?? Array.Empty<ExtensionObject>();
                    var nodes = found.Select(e => e.Body).OfType<AliasNameDataType>().SelectMany(a => a.ReferencedNodes).ToList();
                    if (nodes.Count == 0) { failure = "the server knows no such alias"; continue; }
                    if (nodes.Count > 1) throw new AddressingException($"{alias}: the alias stands for {nodes.Count} nodes.");
                    return FromExpanded(nodes[0]);
                }
                throw new AddressingException($"{alias}: {failure}.");
            }
            default:
                throw new ArgumentException($"Unknown indirect NodeId {id}.", nameof(id));
        }
    }

    private NodeId ToNodeId(UaNodeAddress address) =>
        NodeId.Parse(address.ToNodeIdString(NamespaceTable));

    private UaNodeAddress FromExpanded(ExpandedNodeId id)
    {
        var nodeId = ExpandedNodeId.ToNodeId(id, _session.NamespaceUris);
        var uri = _session.NamespaceUris.GetString(nodeId.NamespaceIndex) ?? "";
        var (type, identifier) = nodeId.IdType switch
        {
            IdType.Numeric => (UaIdType.Numeric, Convert.ToString(nodeId.Identifier, System.Globalization.CultureInfo.InvariantCulture)!),
            IdType.Guid => (UaIdType.Guid, nodeId.Identifier.ToString()!),
            IdType.Opaque => (UaIdType.Opaque, Convert.ToBase64String((byte[])nodeId.Identifier)),
            _ => (UaIdType.String, (string)nodeId.Identifier),
        };
        return new UaNodeAddress(uri, type, identifier);
    }

    private static string ReferenceName(NodeId referenceType)
    {
        if (referenceType.NamespaceIndex == 0 && referenceType.IdType == IdType.Numeric)
            return ReferenceTypes.GetBrowseName((uint)referenceType.Identifier) ?? referenceType.ToString();
        return referenceType.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        _session.KeepAlive -= OnKeepAlive;
        try { await _session.CloseAsync(2000, true, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { /* the server may already be gone */ }
        _session.Dispose();
    }
}
