using Aml.Engine.CAEX;
using OpcUaAml.Alarms;
using OpcUaAml.Checks;
using OpcUaAml.Server;

namespace OpcUaAml.Tests;

/// <summary>
/// The check of the Check tab finds an alarm the servers could not raise: one
/// that watches no value, whose limit is no number, or a state alarm on a
/// value that is no boolean.
/// </summary>
public class AlarmCheckTests(DiDocument di) : IClassFixture<DiDocument>
{
    private (InstanceHierarchyType Ih, InternalElementType Drill, InternalElementType Speed, InternalElementType Empty) Plant(string name)
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
        return (ih, drill, speed, empty);
    }

    private static readonly string[] AlarmRules = [Rules.AlarmWithoutInput, Rules.AlarmLimitNotNumber, Rules.StateAlarmNotBoolean];

    [Fact]
    public void A_sound_alarm_has_no_finding()
    {
        var (ih, _, speed, empty) = Plant("SoundAlarms");
        AlarmElements.Create(speed, AlarmKind.High, 1200, null, 800, "Spindle overloaded", "SpindleSpeedHigh");
        AlarmElements.Create(empty, AlarmKind.State, null, false, 500, "Magazine empty", "MagazineEmptyAlarm");

        Assert.DoesNotContain(di.FindingsIn(ih), f => AlarmRules.Contains(f.Rule));
    }

    [Fact]
    public void An_alarm_whose_value_is_gone_watches_nothing()
    {
        var (ih, drill, speed, _) = Plant("GoneValue");
        AlarmElements.Create(speed, AlarmKind.High, 1200, null, 800, "Spindle overloaded", "SpindleSpeedHigh");
        drill.InternalElement.RemoveElement(speed);

        var finding = Assert.Single(di.FindingsIn(ih), f => f.Rule == Rules.AlarmWithoutInput);
        Assert.Equal(Severity.Error, finding.Severity);
        Assert.EndsWith("SpindleSpeedHigh", finding.ElementPath);
    }

    [Fact]
    public void A_limit_that_is_no_number_is_found()
    {
        var (ih, _, speed, _) = Plant("TextLimit");
        var alarm = AlarmElements.Create(speed, AlarmKind.High, 1200, null, 800, "Spindle overloaded", "SpindleSpeedHigh");
        alarm.InternalElement["HighLimit"]!.Attribute["Value"]!.Value = "3,5";

        var finding = Assert.Single(di.FindingsIn(ih), f => f.Rule == Rules.AlarmLimitNotNumber);
        Assert.Equal(Severity.Error, finding.Severity);
        Assert.Contains("3,5", finding.Message);
    }

    [Fact]
    public void A_state_alarm_on_a_number_is_found()
    {
        var (ih, _, _, empty) = Plant("StateOnNumber");
        AlarmElements.Create(empty, AlarmKind.State, null, false, 500, "Magazine empty", "MagazineEmptyAlarm");
        empty.Attribute["Value"]!.AttributeDataType = "xs:double";

        var finding = Assert.Single(di.FindingsIn(ih), f => f.Rule == Rules.StateAlarmNotBoolean);
        Assert.Equal(Severity.Error, finding.Severity);
        Assert.Contains("MagazineEmpty", finding.Message);
    }
}
