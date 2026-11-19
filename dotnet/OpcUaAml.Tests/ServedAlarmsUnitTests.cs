using Opc.Ua;
using OpcUaAml.Addressing;
using OpcUaAml.Server;

namespace OpcUaAml.Tests;

/// <summary>
/// The condition states of a served alarm, without a server: whether a
/// client subscribed at the source gets the alarm's own events (an
/// acknowledgement), whether a ConditionRefresh hands out a state that cannot
/// change under it, and whether an acknowledgement waits for a value change
/// in progress.
/// </summary>
public class ServedAlarmsUnitTests
{
    private sealed record Rig(ServedAlarms Alarms, BaseObjectState Source, BaseDataVariableState Input, ISystemContext Context, object Gate);

    private static Rig Make(object value, NodeId dataType)
    {
        var context = new SystemContext(DefaultTelemetry.Create(_ => { })) { NamespaceUris = new NamespaceTable(), ServerUris = new StringTable() };
        context.NamespaceUris.Append("urn:test");
        var source = new BaseObjectState(null) { NodeId = new NodeId("Drill", 1), BrowseName = new QualifiedName("Drill", 1), DisplayName = "Drill" };
        var input = new BaseDataVariableState(source)
        {
            NodeId = new NodeId("Drill/Signal", 1), BrowseName = new QualifiedName("Signal", 1), DisplayName = "Signal",
            DataType = dataType, ValueRank = ValueRanks.Scalar, Value = value,
        };
        source.AddChild(input);
        var gate = new object();
        return new Rig(new ServedAlarms(context, 1, _ => { }, _ => { }, gate), source, input, context, gate);
    }

    private static AlarmConditionState AddStateAlarm(Rig rig, bool asComponent) =>
        rig.Alarms.Add(new AlarmDefinition("MagazineEmpty", new UaNodeAddress("urn:test", UaIdType.String, "Drill/Signal"), AlarmKind.State, 0, false, 500, "Magazine empty"),
            rig.Source, rig.Input, new NodeId("Alarms/MagazineEmpty", 1), asComponent);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_client_subscribed_at_the_source_gets_the_alarms_own_events(bool asComponent)
    {
        var rig = Make(true, DataTypeIds.Boolean);
        var alarm = AddStateAlarm(rig, asComponent);

        // What the stack does when a client subscribes to events at the source.
        rig.Source.SetAreEventsMonitored(rig.Context, true, true);

        Assert.True(alarm.AreEventsMonitored);
    }

    [Fact]
    public void Condition_refresh_hands_out_a_snapshot_not_the_live_condition()
    {
        var rig = Make(true, DataTypeIds.Boolean);
        var alarm = AddStateAlarm(rig, asComponent: false);
        Assert.True(alarm.Retain.Value);

        var events = new List<IFilterTarget>();
        rig.Alarms.ConditionRefresh(events);

        var refreshed = Assert.Single(events);
        Assert.IsType<InstanceStateSnapshot>(refreshed);
    }

    [Fact]
    public void An_acknowledgement_waits_for_a_value_change_in_progress()
    {
        var rig = Make(true, DataTypeIds.Boolean);
        var alarm = AddStateAlarm(rig, asComponent: false);
        var eventId = alarm.EventId.Value;

        Task<ServiceResult> call;
        lock (rig.Gate)
        {
            call = Task.Run(() => alarm.Acknowledge.OnCall(rig.Context, alarm.Acknowledge, alarm.NodeId, eventId, new LocalizedText("checked")));
            Assert.False(call.Wait(300), "the acknowledgement ran while the node manager's lock was held");
        }

        Assert.True(call.Wait(5000));
        Assert.True(ServiceResult.IsGood(call.Result), call.Result.ToString());
        Assert.True(alarm.AckedState.Id.Value);
    }
}
