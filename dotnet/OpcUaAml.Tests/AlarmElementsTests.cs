using Aml.Engine.CAEX;
using OpcUaAml.Addressing;
using OpcUaAml.Alarms;
using OpcUaAml.Checks;
using OpcUaAml.Server;

namespace OpcUaAml.Tests;

/// <summary>
/// An alarm in the document is an instance of its class from OPC 10000-9, as
/// the UA base library holds it (Annex A), beside the value it watches: no
/// format of its own. It names its value by the value's ID, which stays when
/// the value is renamed or moved.
/// </summary>
public class AlarmElementsTests(DiDocument di) : IClassFixture<DiDocument>
{
    /// <summary>A drill with a spindle speed and a magazine sensor, in a hierarchy of its own.</summary>
    private (InternalElementType Drill, InternalElementType Speed, InternalElementType Empty) Plant(string name)
    {
        var ih = di.Hierarchy(name);
        var drill = ih.InternalElement.Append("Drill");
        var speed = drill.InternalElement.Append("SpindleSpeed");
        var v = speed.Attribute.Append("Value");
        v.AttributeDataType = "xs:double";
        v.Value = "1080";
        var empty = drill.InternalElement.Append("MagazineEmpty");
        var e = empty.Attribute.Append("Value");
        e.AttributeDataType = "xs:boolean";
        e.Value = "false";
        return (drill, speed, empty);
    }

    private static string? ValueOf(InternalElementType alarm, string child) => alarm.InternalElement[child]?.Attribute["Value"]?.Value;

    [Fact]
    public void A_limit_alarm_is_an_instance_of_its_class_beside_the_value()
    {
        var (drill, speed, _) = Plant("LimitAlarm");

        var alarm = AlarmElements.Create(speed, AlarmKind.High, 1200, null, 800, "Spindle overloaded", "SpindleSpeedHigh");

        Assert.Same(drill.Node, alarm.Node.Parent);
        Assert.EndsWith("[ExclusiveLevelAlarmType]", alarm.RefBaseSystemUnitPath);
        Assert.Equal("1200", ValueOf(alarm, "HighLimit"));
        Assert.Null(alarm.InternalElement["LowLimit"]);
        Assert.Equal("800", ValueOf(alarm, "Severity"));
        Assert.Equal("Spindle overloaded", ValueOf(alarm, "Message"));
        Assert.Equal("SpindleSpeedHigh", ValueOf(alarm, "ConditionName"));
        Assert.Equal(speed.ID, alarm.InternalElement["InputNode"]!.Attribute[AlarmElements.RefNode]!.Value);
        Assert.DoesNotContain(di.FindingsIn((InstanceHierarchyType)drill.CAEXParent!), f => f.ElementPath.Contains("SpindleSpeedHigh") && f.Rule == Rules.MissingMandatory);

        var read = AlarmElements.Read(alarm);
        Assert.Equal(AlarmKind.High, read.Kind);
        Assert.Equal(1200, read.Limit);
        Assert.Same(speed.Node, read.Input!.Node);
        Assert.Equal(800, read.Severity);
        Assert.Equal("Spindle overloaded", read.Message);
    }

    [Fact]
    public void A_low_limit_alarm_has_a_low_limit()
    {
        var (_, speed, _) = Plant("LowAlarm");

        var alarm = AlarmElements.Create(speed, AlarmKind.Low, 900, null, 500, "Spindle too slow", "SpindleSpeedLow");

        Assert.Equal("900", ValueOf(alarm, "LowLimit"));
        Assert.Null(alarm.InternalElement["HighLimit"]);
        Assert.Equal(AlarmKind.Low, AlarmElements.Read(alarm).Kind);
    }

    [Fact]
    public void A_state_alarm_names_its_normal_value()
    {
        var (_, _, empty) = Plant("StateAlarm");

        var alarm = AlarmElements.Create(empty, AlarmKind.State, null, false, 500, "Magazine empty", "MagazineEmptyAlarm");

        Assert.EndsWith("[OffNormalAlarmType]", alarm.RefBaseSystemUnitPath);
        var normal = alarm.InternalElement["NormalValue"]!;
        Assert.Equal("false", normal.Attribute["Value"]!.Value);
        Assert.Equal("xs:boolean", normal.Attribute["Value"]!.AttributeDataType);
        Assert.Equal(normal.ID, alarm.InternalElement["NormalState"]!.Attribute[AlarmElements.RefNode]!.Value);
        var read = AlarmElements.Read(alarm);
        Assert.Equal(AlarmKind.State, read.Kind);
        Assert.False(read.Normal);
    }

    [Fact]
    public void The_reference_survives_renaming_and_moving()
    {
        var (drill, speed, _) = Plant("Moved");
        var alarm = AlarmElements.Create(speed, AlarmKind.High, 1200, null, 800, "Spindle overloaded", "SpindleSpeedHigh");

        speed.Name = "Speed";
        var elsewhere = ((InstanceHierarchyType)drill.CAEXParent!).InternalElement.Append("Elsewhere");
        elsewhere.InternalElement.Insert(speed, asFirst: false);

        Assert.Equal("Speed", AlarmElements.Read(alarm).Input!.Name);
    }

    [Fact]
    public void A_bound_value_keeps_its_node_id_in_the_input()
    {
        var (_, speed, _) = Plant("Bound");
        var address = new UaNodeAddress("http://example.org/Plant/", UaIdType.Numeric, "6001");
        AnnexANodeId.Write(speed, address);
        var before = BindingSet.Of(di.Document).Count;

        var alarm = AlarmElements.Create(speed, AlarmKind.High, 1200, null, 800, "Spindle overloaded", "SpindleSpeedHigh");

        Assert.Equal(address, AnnexANodeId.Read(alarm.InternalElement["InputNode"]!.Attribute["Value"]!));
        Assert.Equal(before, BindingSet.Of(di.Document).Count);
    }

    [Fact]
    public void The_name_proposed_is_free_under_the_parent()
    {
        var (_, speed, _) = Plant("Names");
        Assert.Equal("SpindleSpeedHigh", AlarmElements.SuggestName(speed, AlarmKind.High));

        AlarmElements.Create(speed, AlarmKind.High, 1200, null, 800, "Spindle overloaded", "SpindleSpeedHigh");

        Assert.Equal("SpindleSpeedHigh2", AlarmElements.SuggestName(speed, AlarmKind.High));
        Assert.Equal("SpindleSpeedLow", AlarmElements.SuggestName(speed, AlarmKind.Low));
    }

    [Theory]
    [InlineData("state on a number", AlarmProblem.NotBoolean)]
    [InlineData("limit on a boolean", AlarmProblem.NotANumber)]
    [InlineData("no limit", AlarmProblem.NotANumber)]
    [InlineData("severity 0", AlarmProblem.BadSeverity)]
    [InlineData("severity 1001", AlarmProblem.BadSeverity)]
    [InlineData("name taken", AlarmProblem.NameTaken)]
    [InlineData("no value", AlarmProblem.NotAValue)]
    public void Wrong_requests_are_refused(string request, AlarmProblem problem)
    {
        var (drill, speed, empty) = Plant("Wrong" + problem + request.Length);

        var ex = Assert.Throws<AlarmException>(() => _ = request switch
        {
            "state on a number" => AlarmElements.Create(speed, AlarmKind.State, null, false, 500, "x", "A"),
            "limit on a boolean" => AlarmElements.Create(empty, AlarmKind.High, 1, null, 500, "x", "A"),
            "no limit" => AlarmElements.Create(speed, AlarmKind.High, null, null, 500, "x", "A"),
            "severity 0" => AlarmElements.Create(speed, AlarmKind.High, 1, null, 0, "x", "A"),
            "severity 1001" => AlarmElements.Create(speed, AlarmKind.High, 1, null, 1001, "x", "A"),
            "name taken" => AlarmElements.Create(speed, AlarmKind.High, 1, null, 500, "x", "MagazineEmpty"),
            _ => AlarmElements.Create(drill, AlarmKind.High, 1, null, 500, "x", "A"),
        });

        Assert.Equal(problem, ex.Problem);
    }

    [Fact]
    public void A_document_without_the_base_library_is_told()
    {
        var doc = CAEXDocument.New_CAEXDocument();
        var speed = doc.CAEXFile.InstanceHierarchy.Append("Plant").InternalElement.Append("Drill").InternalElement.Append("Speed");
        var v = speed.Attribute.Append("Value");
        v.AttributeDataType = "xs:double";
        v.Value = "1";

        Assert.False(AlarmElements.HasBaseLibrary(doc));
        Assert.Equal(AlarmProblem.NoBaseLibrary,
            Assert.Throws<AlarmException>(() => AlarmElements.Create(speed, AlarmKind.High, 2, null, 500, "x", "A")).Problem);
        Assert.True(AlarmElements.HasBaseLibrary(di.Document));
    }

    [Fact]
    public void An_alarm_of_another_class_is_read_without_a_kind()
    {
        var ih = di.Hierarchy("Discrete");
        var discrete = Types.TypeInstantiator.Instantiate(di.UaType("DiscreteAlarmType"), "Door").Instance;
        ih.InternalElement.Insert(discrete, asFirst: false);

        var read = AlarmElements.Read(ih.InternalElement["Door"]!);

        Assert.True(AlarmElements.IsAlarm(ih.InternalElement["Door"]!));
        Assert.Null(read.Kind);
        Assert.Equal("DiscreteAlarmType", read.ClassName);
    }
}
