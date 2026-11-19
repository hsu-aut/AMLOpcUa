// Events and alarms of a server (OPC 10000-4 Events, OPC 10000-9 Alarms and
// Conditions): one subscription with an event item at the Server object,
// which every event of the server reaches through its notifiers. The filter
// selects the fields of BaseEventType and, for conditions, those of
// AlarmConditionType. After it is set up the client asks for a
// ConditionRefresh, so alarms that were active before arrive at once.

using Opc.Ua;
using Opc.Ua.Client;
using OpcUaAml.Addressing;

namespace OpcUaAml.Server;

/// <summary>The server does not send events: it refused the event subscription at its Server object.</summary>
public sealed class UaEventsUnavailableException(string status)
    : Exception($"The server sends no events ({status}).")
{
    public string Status { get; } = status;
}

public sealed partial class UaClient
{
    /// <summary>
    /// The server's application name, as its endpoint describes it; its URI
    /// when it has none. Never its address: a history saved with it may be
    /// passed on without naming computers.
    /// </summary>
    public string ServerName =>
        _session.Endpoint?.Server?.ApplicationName?.Text is { Length: > 0 } name ? name
        : ServerUri is { Length: > 0 } uri ? uri
        : "server";

    /// <summary>The fields the event filter selects, in the order <see cref="ToEvent"/> reads them.</summary>
    internal static readonly string[] EventFields =
    [
        "EventId", "EventType", "SourceNode", "SourceName", "Time", "Message", "Severity",
        "ConditionId", "ConditionName", "ActiveState/Id", "AckedState/Id", "Retain", "Comment", "ClientUserId",
    ];

    private static EventFilter Filter()
    {
        static SimpleAttributeOperand Field(NodeId type, string path) => new()
        {
            TypeDefinitionId = type,
            BrowsePath = new QualifiedNameCollection(path.Split('/').Select(p => new QualifiedName(p))),
            AttributeId = Attributes.Value,
        };
        var filter = new EventFilter();
        foreach (var name in EventFields)
        {
            filter.SelectClauses.Add(name switch
            {
                // The condition is the node the event is about: its NodeId, not a value.
                "ConditionId" => new SimpleAttributeOperand
                {
                    TypeDefinitionId = ObjectTypeIds.ConditionType,
                    BrowsePath = new QualifiedNameCollection(),
                    AttributeId = Attributes.NodeId,
                },
                "ConditionName" or "Retain" or "Comment" or "ClientUserId" => Field(ObjectTypeIds.ConditionType, name),
                "ActiveState/Id" => Field(ObjectTypeIds.AlarmConditionType, name),
                "AckedState/Id" => Field(ObjectTypeIds.AcknowledgeableConditionType, name),
                _ => Field(ObjectTypeIds.BaseEventType, name),
            });
        }
        return filter;
    }

    /// <summary>
    /// Receives the server's events until the result is disposed.
    /// <paramref name="onEvent"/> runs on a stack thread for each one; the
    /// markers around a ConditionRefresh are not passed on.
    /// </summary>
    /// <exception cref="UaEventsUnavailableException">The server refused the subscription.</exception>
    public async Task<IAsyncDisposable> SubscribeEventsAsync(Action<UaEvent> onEvent, CancellationToken ct = default)
    {
        var telemetry = _session.MessageContext.Telemetry;
        var subscription = new Subscription(telemetry, new SubscriptionOptions
        {
            DisplayName = "AMLOpcUa events",
            PublishingInterval = 500,
            PublishingEnabled = true,
            KeepAliveCount = 10,
            LifetimeCount = 100,
        });
        var item = new MonitoredItem(telemetry, new MonitoredItemOptions
        {
            StartNodeId = ObjectIds.Server,
            AttributeId = Attributes.EventNotifier,
            SamplingInterval = 0,
            QueueSize = 1000,
            DiscardOldest = true,
            Filter = Filter(),
            DisplayName = "Server events",
        });
        item.Notification += (_, e) =>
        {
            if (e.NotificationValue is EventFieldList list && ToEvent(list.EventFields, _session.NamespaceUris) is { } received)
                onEvent(received);
        };
        subscription.AddItem(item);
        _session.AddSubscription(subscription);
        var watch = new Watch(_session, subscription);
        try
        {
            await subscription.CreateAsync(ct).ConfigureAwait(false);
        }
        catch (ServiceResultException ex)
        {
            await watch.DisposeAsync().ConfigureAwait(false);
            throw new UaEventsUnavailableException(Symbol(ex.StatusCode));
        }
        catch
        {
            await watch.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        if (item.Status.Error is { } error && ServiceResult.IsBad(error))
        {
            await watch.DisposeAsync().ConfigureAwait(false);
            throw new UaEventsUnavailableException(Symbol(error.StatusCode));
        }

        // A server without conditions may refuse the refresh; its events come all the same.
        try
        {
            var refresh = new CallMethodRequestCollection
            {
                new CallMethodRequest
                {
                    ObjectId = ObjectTypeIds.ConditionType,
                    MethodId = MethodIds.ConditionType_ConditionRefresh,
                    InputArguments = new VariantCollection { new Variant(subscription.Id) },
                },
            };
            await _session.CallAsync(null, refresh, ct).ConfigureAwait(false);
        }
        catch (ServiceResultException) { /* no conditions to refresh */ }
        return watch;
    }

    /// <summary>
    /// The selected fields of an event (<see cref="EventFields"/>) as a
    /// <see cref="UaEvent"/>; null for the markers of a ConditionRefresh and
    /// for an event without an EventId.
    /// </summary>
    internal static UaEvent? ToEvent(IList<Variant> fields, NamespaceTable table)
    {
        object? At(int i) => i < fields.Count ? fields[i].Value : null;
        if (At(0) is not byte[] eventId || eventId.Length == 0) return null;
        var eventType = At(1) as NodeId;
        if (eventType == ObjectTypeIds.RefreshStartEventType || eventType == ObjectTypeIds.RefreshEndEventType) return null;
        UaNodeAddress? Node(int i) => At(i) is NodeId id && !NodeId.IsNull(id) ? Address(id, table) : null;
        static string? Text(object? v) => v switch
        {
            LocalizedText t => string.IsNullOrEmpty(t.Text) ? null : t.Text,
            string s => s.Length == 0 ? null : s,
            _ => null,
        };
        var time = At(4) is DateTime t ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : DateTime.MinValue;
        return new UaEvent(
            eventId,
            eventType == null || NodeId.IsNull(eventType) ? null : Address(eventType, table),
            Node(2),
            Text(At(3)) ?? "",
            time,
            Text(At(5)) ?? "",
            At(6) is ushort severity ? severity : (ushort)0,
            Node(7),
            Text(At(8)),
            At(9) as bool?,
            At(10) as bool?,
            At(11) as bool?,
            Text(At(12)),
            Text(At(13)));
    }
}
