// Alarms a server of this build raises (OPC 10000-9): a limit alarm
// (ExclusiveLevelAlarmType, above or below a limit) or a state alarm
// (OffNormalAlarmType, a boolean away from its normal value) on a variable it
// serves. The stack's condition states keep the state machine, acknowledge,
// and the snapshot a ConditionRefresh sends; this class creates them, decides
// when a value change makes an alarm active or inactive, and reports each
// change. The object above the variable is the event source: its events reach
// the Server object as a root notifier, so a client that subscribes there sees
// every alarm. Both servers are read only; only their own values move alarms.

using System.Globalization;
using Opc.Ua;
using Opc.Ua.Server;
using OpcUaAml.Addressing;

namespace OpcUaAml.Server;

/// <summary>Above a limit, below a limit, or a boolean away from its normal value.</summary>
public enum AlarmKind { High, Low, State }

/// <summary>
/// An alarm on the variable <paramref name="Input"/>: for <see cref="AlarmKind.High"/>
/// and <see cref="AlarmKind.Low"/> beyond <paramref name="Limit"/>, for
/// <see cref="AlarmKind.State"/> while the value is not <paramref name="Normal"/>.
/// </summary>
public sealed record AlarmDefinition(string Name, UaNodeAddress Input, AlarmKind Kind, double Limit, bool Normal, ushort Severity, string Message);

internal sealed class ServedAlarms(ServerSystemContext context, ushort namespaceIndex, Action<NodeState> addNode, Action<NodeState> addRootNotifier)
{
    private sealed class Served(AlarmDefinition definition, AlarmConditionState state, BaseDataVariableState input)
    {
        public AlarmDefinition Definition { get; } = definition;
        public AlarmConditionState State { get; } = state;
        public BaseDataVariableState Input { get; } = input;
        public bool Active { get; set; }
    }

    private readonly List<Served> _alarms = new();
    private readonly Dictionary<BaseDataVariableState, List<Served>> _byInput = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
    private readonly HashSet<NodeState> _roots = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);

    public int Count => _alarms.Count;

    /// <summary>Whether an alarm watches the variable: the simulation moves it at the watched pace.</summary>
    public bool Watches(BaseDataVariableState variable) => _byInput.ContainsKey(variable);

    /// <summary>
    /// Creates the alarm on <paramref name="input"/>, with <paramref name="source"/>
    /// as its event source. As a component it is a child of the source (its
    /// element stands there in the document); otherwise only HasCondition
    /// leads to it, and the source's hierarchy stays as its model has it.
    /// </summary>
    public AlarmConditionState Add(AlarmDefinition d, NodeState source, BaseDataVariableState input, NodeId alarmId, bool asComponent)
    {
        var parent = asComponent ? source : null;
        AlarmConditionState alarm;
        if (d.Kind == AlarmKind.State)
        {
            alarm = new OffNormalAlarmState(parent);
        }
        else
        {
            var level = new ExclusiveLevelAlarmState(parent);
            if (d.Kind == AlarmKind.High) level.HighLimit = new PropertyState<double>(level);
            else level.LowLimit = new PropertyState<double>(level);
            alarm = level;
        }
        alarm.Create(context, alarmId, new QualifiedName(d.Name, namespaceIndex), new LocalizedText(d.Name), false);
        alarm.SymbolicName = d.Name;
        if (parent != null)
        {
            alarm.ReferenceTypeId = ReferenceTypeIds.HasComponent;
            parent.AddChild(alarm);
        }
        AssignIds(alarm, Text(alarmId));

        alarm.EventType.Value = alarm.TypeDefinitionId;
        alarm.SourceNode.Value = source.NodeId;
        alarm.SourceName.Value = source.BrowseName?.Name ?? source.NodeId.ToString();
        alarm.ConditionName.Value = d.Name;
        alarm.ConditionClassId.Value = ObjectTypeIds.ProcessConditionClassType;
        alarm.ConditionClassName.Value = new LocalizedText("Process");
        alarm.InputNode.Value = input.NodeId;
        alarm.Message.Value = new LocalizedText(d.Message);
        alarm.BranchId.Value = NodeId.Null;
        alarm.Quality.Value = StatusCodes.Good;
        alarm.SetSeverity(context, (EventSeverity)d.Severity);
        alarm.SetEnableState(context, true);
        alarm.SetActiveState(context, false);
        alarm.SetAcknowledgedState(context, true);
        alarm.Retain.Value = false;
        if (alarm is ExclusiveLevelAlarmState limit)
        {
            if (limit.HighLimit != null) limit.HighLimit.Value = d.Limit;
            if (limit.LowLimit != null) limit.LowLimit.Value = d.Limit;
            limit.SetLimitState(context, LimitAlarmStates.Inactive);
        }
        if (alarm is OffNormalAlarmState offNormal)
        {
            // NormalState names a variable that holds the normal value (OPC 10000-9 5.8.23).
            var normal = new PropertyState<bool>(alarm)
            {
                NodeId = new NodeId(Text(alarmId) + "/NormalValue", namespaceIndex),
                BrowseName = new QualifiedName("NormalValue", namespaceIndex),
                DisplayName = "NormalValue",
                TypeDefinitionId = VariableTypeIds.PropertyType,
                ReferenceTypeId = ReferenceTypeIds.HasProperty,
                DataType = DataTypeIds.Boolean,
                ValueRank = ValueRanks.Scalar,
                AccessLevel = AccessLevels.CurrentRead,
                UserAccessLevel = AccessLevels.CurrentRead,
                Value = d.Normal,
            };
            alarm.AddChild(normal);
            offNormal.NormalState.Value = normal.NodeId;
        }

        var served = new Served(d, alarm, input);
        alarm.OnAcknowledge = (_, _, eventId, comment) => Acknowledge(served, eventId, comment);
        // Acknowledge reports by itself; a value change is reported below.
        alarm.AutoReportStateChanges = true;

        // The events reach the Server object once: a component through its parents up to
        // the topmost node, which is the root notifier; otherwise through the source,
        // which is then the root notifier itself. Two root notifiers on one path would
        // send each event twice.
        source.AddReference(ReferenceTypeIds.HasCondition, false, alarm.NodeId);
        alarm.AddReference(ReferenceTypeIds.HasCondition, true, source.NodeId);
        if (!asComponent) alarm.AddNotifier(context, ReferenceTypeIds.HasCondition, true, source);
        var root = source;
        for (var node = source; node != null; node = asComponent ? (node as BaseInstanceState)?.Parent : null)
        {
            if (node is BaseObjectState notifier) notifier.EventNotifier |= EventNotifiers.SubscribeToEvents;
            if ((node as BaseInstanceState)?.Parent is { } up && asComponent && !up.ReferenceExists(ReferenceTypeIds.HasNotifier, false, node.NodeId))
            {
                up.AddReference(ReferenceTypeIds.HasNotifier, false, node.NodeId);
                node.AddReference(ReferenceTypeIds.HasNotifier, true, up.NodeId);
            }
            root = node;
        }
        if (_roots.Add(root)) addRootNotifier(root);
        addNode(alarm);

        _alarms.Add(served);
        if (!_byInput.TryGetValue(input, out var list)) _byInput[input] = list = new List<Served>();
        list.Add(served);
        Evaluate(input);
        return alarm;
    }

    /// <summary>
    /// The alarms on a variable after its value changed: an alarm that becomes
    /// active, or inactive, reports it. Call it under the node manager's lock.
    /// </summary>
    public void Evaluate(BaseDataVariableState input)
    {
        if (!_byInput.TryGetValue(input, out var alarms)) return;
        foreach (var served in alarms)
        {
            if (Beyond(served.Definition, input.Value) is not { } active || active == served.Active) continue;
            served.Active = active;
            var alarm = served.State;
            alarm.SetActiveState(context, active);
            if (alarm is ExclusiveLevelAlarmState limit)
                limit.SetLimitState(context, !active ? LimitAlarmStates.Inactive
                    : served.Definition.Kind == AlarmKind.High ? LimitAlarmStates.High : LimitAlarmStates.Low);
            if (active) alarm.SetAcknowledgedState(context, false);
            alarm.Retain.Value = active || !alarm.AckedState.Id.Value;
            Report(alarm);
        }
    }

    /// <summary>Whether the value is beyond the alarm's limit or away from normal; null for a value that cannot tell (no number, no boolean).</summary>
    internal static bool? Beyond(AlarmDefinition d, object? value)
    {
        if (d.Kind == AlarmKind.State) return value is bool b ? b != d.Normal : null;
        double number;
        try
        {
            if (value is null or string or bool) return null;
            number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException) { return null; }
        if (double.IsNaN(number)) return null;
        return d.Kind == AlarmKind.High ? number > d.Limit : number < d.Limit;
    }

    /// <summary>
    /// The client acknowledged with <paramref name="eventId"/>: only the alarm's
    /// newest event may be acknowledged; an older one has been overtaken.
    /// </summary>
    private ServiceResult Acknowledge(Served served, byte[] eventId, LocalizedText comment)
    {
        var alarm = served.State;
        if (eventId == null || alarm.EventId.Value == null || !eventId.AsSpan().SequenceEqual(alarm.EventId.Value))
            return StatusCodes.BadEventIdUnknown;
        if (alarm.AckedState.Id.Value) return StatusCodes.BadConditionBranchAlreadyAcked;
        // The stack then sets the acknowledged state, the comment and Retain, and reports.
        return ServiceResult.Good;
    }

    /// <summary>A new event of the alarm: a new EventId and time, sent to the subscribers.</summary>
    private void Report(AlarmConditionState alarm)
    {
        alarm.EventId.Value = Guid.NewGuid().ToByteArray();
        alarm.Time.Value = DateTime.UtcNow;
        alarm.ReceiveTime.Value = alarm.Time.Value;
        alarm.ClearChangeMasks(context, true);
        var snapshot = new InstanceStateSnapshot();
        snapshot.Initialize(context, alarm);
        alarm.ReportEvent(context, snapshot);
    }

    /// <summary>The state of every alarm the server retains, for a ConditionRefresh.</summary>
    public void ConditionRefresh(List<IFilterTarget> events)
    {
        foreach (var served in _alarms) served.State.ConditionRefresh(context, events, true);
    }

    /// <summary>Gives every node below the alarm a NodeId of the manager, named after its path, as the stack gives the type's own.</summary>
    private void AssignIds(NodeState node, string prefix)
    {
        var children = new List<BaseInstanceState>();
        node.GetChildren(context, children);
        foreach (var child in children)
        {
            var path = prefix + "/" + child.BrowseName.Name;
            child.NodeId = new NodeId(path, namespaceIndex);
            AssignIds(child, path);
        }
    }

    /// <summary>A NodeId's identifier as text, the same at every start.</summary>
    internal static string Text(NodeId id) => id.IdType switch
    {
        IdType.String => (string)id.Identifier,
        IdType.Opaque => "b=" + Convert.ToBase64String((byte[])id.Identifier),
        IdType.Guid => "g=" + id.Identifier,
        _ => "i=" + Convert.ToString(id.Identifier, CultureInfo.InvariantCulture),
    };

    /// <summary>A UA node address as a NodeId of the server; null when the server does not know its namespace.</summary>
    internal static NodeId? ToNodeId(UaNodeAddress address, NamespaceTable table)
    {
        var index = table.GetIndex(address.NamespaceUri);
        if (index < 0) return null;
        var ns = (ushort)index;
        return address.IdType switch
        {
            UaIdType.Numeric => new NodeId(uint.Parse(address.Identifier, CultureInfo.InvariantCulture), ns),
            UaIdType.Guid => new NodeId(Guid.Parse(address.Identifier), ns),
            UaIdType.Opaque => new NodeId(Convert.FromBase64String(address.Identifier), ns),
            _ => new NodeId(address.Identifier, ns),
        };
    }
}
