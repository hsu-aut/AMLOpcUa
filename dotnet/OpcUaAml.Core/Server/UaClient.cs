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

    /// <summary>Prefer a secured endpoint (Sign or SignAndEncrypt) when the server offers one.</summary>
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
    }

    public string EndpointUrl { get; }
    public string SecurityMode { get; }

    /// <summary>The session, for the classes of this library that need more than browsing and reading.</summary>
    internal ISession Session => _session;

    /// <summary>The server's namespace table at connect time.</summary>
    public IReadOnlyList<string> NamespaceTable => _session.NamespaceUris.ToArray();

    /// <summary>The server's ApplicationUri (ServerArray[0]).</summary>
    public string? ServerUri => _session.ServerUris.Count > 0 ? _session.ServerUris.GetString(0) : null;

    public static async Task<UaClient> ConnectAsync(UaConnectOptions options, CancellationToken ct = default)
    {
        var config = await CreateConfigurationAsync(options, ct).ConfigureAwait(false);
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
                $"The server certificate of '{options.EndpointUrl}' is not trusted. Accept it once, or copy it into " +
                $"'{Path.Combine(options.PkiRoot, "trusted", "certs")}'.", ex);
        }
        catch (Exception ex)
        {
            throw new UaConnectionException($"Connecting to '{options.EndpointUrl}' failed: {ex.Message}", ex);
        }
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
    public async Task<IReadOnlyList<UaBrowseItem>> BrowseAsync(UaNodeAddress? node = null, CancellationToken ct = default)
    {
        var start = node == null ? ObjectIds.ObjectsFolder : ToNodeId(node);
        var browser = new BrowseDescription
        {
            NodeId = start,
            BrowseDirection = BrowseDirection.Forward,
            ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
            IncludeSubtypes = true,
            NodeClassMask = (uint)(NodeClass.Object | NodeClass.Variable | NodeClass.Method),
            ResultMask = (uint)BrowseResultMask.All,
        };
        var items = new List<UaBrowseItem>();
        var response = await _session.BrowseAsync(null, null, 0, new BrowseDescriptionCollection { browser }, ct).ConfigureAwait(false);
        var result = response.Results[0];
        Add(result.References);
        var continuation = result.ContinuationPoint;
        while (continuation != null && continuation.Length > 0)
        {
            var next = await _session.BrowseNextAsync(null, false, new ByteStringCollection { continuation }, ct).ConfigureAwait(false);
            Add(next.Results[0].References);
            continuation = next.Results[0].ContinuationPoint;
        }
        return items;

        void Add(ReferenceDescriptionCollection refs)
        {
            foreach (var r in refs)
            {
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
        await subscription.CreateAsync(ct).ConfigureAwait(false);
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
        try { await _session.CloseAsync(2000, true, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { /* the server may already be gone */ }
        _session.Dispose();
    }
}
