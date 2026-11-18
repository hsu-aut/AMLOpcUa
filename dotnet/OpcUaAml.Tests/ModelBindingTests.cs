using Aml.Engine.CAEX;
using OpcUaAml.Addressing;
using OpcUaAml.Binding;

namespace OpcUaAml.Tests;

/// <summary>
/// Binding a plant model of one's own to a server's variables at once: the
/// plugin proposes a variable for each signal of the model by the words of
/// both paths, and binds what the user keeps.
/// </summary>
public class ModelBindingTests
{
    private const string Plant = "urn:plant";

    private static ModelSignal Signal(string path, string? dataType = null, UaNodeAddress? boundTo = null) =>
        new(path.Split('/'), dataType, boundTo);

    private static ServerVariable Variable(string path, string? dataType = null) =>
        new(new UaNodeAddress(Plant, UaIdType.String, path), path.Split('/'), dataType);

    private static IReadOnlyList<ServerVariable> Variables(params string[] paths) => paths.Select(p => Variable(p)).ToList();

    private static BindingProposal Of(BindingPlan plan, string path) => plan.Proposals.Single(p => p.Signal.PathText == path);

    /// <summary>Station ST30 of the practice plant, and the same variables of another station.</summary>
    private static readonly string[] PracticePlant =
    [
        .. new[]
        {
            "ST30_Processing/MD_Clamping_unit/DV_Clamp_cylinder/Moving", "ST30_Processing/MD_Clamping_unit/DV_Clamp_cylinder/Position",
            "ST30_Processing/MD_Clamping_unit/Ready", "ST30_Processing/MD_Control/CTRL_PLC/CpuLoad", "ST30_Processing/MD_Control/CTRL_PLC/Online",
            "ST30_Processing/MD_Drilling_unit/DV_Drill_spindle/Moving", "ST30_Processing/MD_Drilling_unit/DV_Drill_spindle/Position",
            "ST30_Processing/MD_Drilling_unit/DV_Drill_spindle/Speed", "ST30_Processing/MD_Drilling_unit/DV_Linear_axis_Z/Moving",
            "ST30_Processing/MD_Drilling_unit/DV_Linear_axis_Z/Position", "ST30_Processing/MD_Drilling_unit/DV_Linear_axis_Z/Speed",
            "ST30_Processing/MD_Drilling_unit/DV_Z_endpos_sensor/Signal", "ST30_Processing/MD_Drilling_unit/Ready",
            "ST30_Processing/MD_Rotary_indexing_table/DV_Index_drive/Moving", "ST30_Processing/MD_Rotary_indexing_table/DV_Index_drive/Position",
            "ST30_Processing/MD_Rotary_indexing_table/DV_Index_drive/Speed", "ST30_Processing/MD_Rotary_indexing_table/Ready",
            "ST30_Processing/ParameterSet/CycleCount", "ST30_Processing/ParameterSet/CycleTime", "ST30_Processing/ParameterSet/PartsProduced",
            "ST30_Processing/RevisionCounter", "ST30_Processing/SerialNumber", "ST30_Processing/Manufacturer",
            "ST30_Processing/StationNumber", "ST30_Processing/StationState/CurrentState", "ST30_Processing/StationState/CurrentState/Id",
        }.Select(p => "MPS500/" + p),
        "MPS500/ST10_Distributing/ParameterSet/PartsProduced", "MPS500/ST10_Distributing/ParameterSet/CycleCount",
        "MPS500/ST10_Distributing/RevisionCounter", "MPS500/ST10_Distributing/MD_Stack_magazine/DV_Ejecting_cylinder/Position",
        "MPS500/ST10_Distributing/StationNumber", "MPS500/ST10_Distributing/StationState/CurrentState",
        "MPS500/ST20_PartsDistributing/RevisionCounter", "MPS500/ST20_PartsDistributing/ParameterSet/PartsProduced",
        "MPS500/ST20_PartsDistributing/StationState/CurrentState",
    ];

    [Theory]
    [InlineData("DI_FB_SensorOccupied", "di fb sensor occupied")]
    [InlineData("DV_Drill_spindle", "dv drill spindle")]
    [InlineData("PartsCounter", "parts counter")]
    [InlineData("Motor1", "motor 1")]
    [InlineData("ST30_Processing", "st 30 processing")]
    [InlineData("PLCValue", "plc value")]
    [InlineData("Füllstand Ist", "fuellstand ist")]
    public void Names_split_into_words_as_PLC_and_model_names_are_written(string name, string words) =>
        Assert.Equal(words.Split(' '), NameWords.Of(name));

    [Fact]
    public void The_drill_spindle_speed_is_proposed_for_SpindleSpeed_and_kept()
    {
        var plan = ModelBinder.Propose(
            [Signal("Plant/ProcessingStation/Drill/SpindleSpeed"), Signal("Plant/ProcessingStation/Turntable")],
            Variables(PracticePlant));

        var speed = Of(plan, "Plant/ProcessingStation/Drill/SpindleSpeed");
        Assert.Equal("MPS500/ST30_Processing/MD_Drilling_unit/DV_Drill_spindle/Speed", speed.Variable!.PathText);
        Assert.True(speed.Keep, speed + "; stands: " + string.Join(", ", plan.Stands));
        Assert.Contains("spindle", speed.Shared);
    }

    [Fact]
    public void A_signal_two_variables_fit_equally_is_proposed_but_left_to_the_user()
    {
        // PartsCounter: PartsProduced shares "parts", RevisionCounter "counter".
        var plan = ModelBinder.Propose([Signal("Plant/ProcessingStation/PartsCounter")], Variables(PracticePlant));

        var counter = Of(plan, "Plant/ProcessingStation/PartsCounter");
        Assert.NotNull(counter.Variable);
        Assert.False(counter.Keep, counter.ToString());
        Assert.Contains(counter.Candidates, v => v.PathText == "MPS500/ST30_Processing/ParameterSet/PartsProduced");
        Assert.DoesNotContain(counter.Candidates, v => v.PathText.StartsWith("MPS500/ST10", StringComparison.Ordinal));
    }

    [Fact]
    public void Flat_PLC_names_with_prefixes_find_the_signals_of_the_model()
    {
        var plan = ModelBinder.Propose(
            [Signal("Cell/Feeder/SensorOccupied", "xs:boolean"), Signal("Cell/Feeder/StartFB", "xs:boolean"), Signal("Cell/Feeder/Timer")],
            [
                Variable("Application/GVL/DI_FB_SensorOccupied", "Boolean"), Variable("Application/GVL/DI_UnityClick", "Boolean"),
                Variable("Application/GVL/DO_StartFB", "Boolean"), Variable("Application/GVL/DO_StartModulAusCoDeSys", "Boolean"),
                Variable("Application/GVL/DO_Timer", "Boolean"), Variable("Application/GVL/Internal_CoDeSys_StartSchalter", "Boolean"),
            ]);

        Assert.Equal("Application/GVL/DI_FB_SensorOccupied", Of(plan, "Cell/Feeder/SensorOccupied").Variable!.PathText);
        Assert.Equal("Application/GVL/DO_StartFB", Of(plan, "Cell/Feeder/StartFB").Variable!.PathText);
        Assert.Equal("Application/GVL/DO_Timer", Of(plan, "Cell/Feeder/Timer").Variable!.PathText);
        Assert.All(plan.Proposals, p => Assert.True(p.Keep, p.ToString()));
    }

    [Fact]
    public void A_number_in_a_name_must_agree()
    {
        var plan = ModelBinder.Propose(
            [Signal("Line/Motor1/Speed"), Signal("Line/Motor2/Speed"), Signal("Line/Motor3/Speed")],
            Variables("PLC/Motor1_Speed", "PLC/Motor2_Speed", "PLC/Motor4_Speed"));

        Assert.Equal("PLC/Motor1_Speed", Of(plan, "Line/Motor1/Speed").Variable!.PathText);
        Assert.Equal("PLC/Motor2_Speed", Of(plan, "Line/Motor2/Speed").Variable!.PathText);
        // Motor4_Speed is free, but it is not Motor3's.
        Assert.Null(Of(plan, "Line/Motor3/Speed").Variable);
    }

    [Fact]
    public void A_value_of_another_kind_is_not_proposed()
    {
        var plan = ModelBinder.Propose(
            [Signal("Line/Pump/Running", "xs:boolean"), Signal("Line/Pump/Pressure", "xs:double")],
            [Variable("PLC/Pump/Running", "Double"), Variable("PLC/Pump/Pressure", "Float")]);

        Assert.Null(Of(plan, "Line/Pump/Running").Variable);
        Assert.Equal("PLC/Pump/Pressure", Of(plan, "Line/Pump/Pressure").Variable!.PathText);
    }

    [Fact]
    public void Names_of_the_plant_above_the_part_searched_do_not_weaken_a_match()
    {
        // Searched below ST30_Processing: neither station nor plant is in a variable's path.
        const string st30 = "MPS500/ST30_Processing/";
        var below = PracticePlant.Where(p => p.StartsWith(st30, StringComparison.Ordinal)).Select(p => p[st30.Length..]).ToArray();

        var plan = ModelBinder.Propose([Signal("Plant/ProcessingStation/Drill/SpindleSpeed")], Variables(below));

        var speed = Of(plan, "Plant/ProcessingStation/Drill/SpindleSpeed");
        Assert.Equal("MD_Drilling_unit/DV_Drill_spindle/Speed", speed.Variable!.PathText);
        Assert.True(speed.Keep, speed + "; stands: " + string.Join(", ", plan.Stands));
    }

    [Fact]
    public void Each_variable_is_proposed_for_one_signal_only()
    {
        var plan = ModelBinder.Propose(
            [Signal("Line/Speed"), Signal("Line/Conveyor/Speed")],
            Variables("PLC/Line/Conveyor/Speed"));

        Assert.Equal("PLC/Line/Conveyor/Speed", Of(plan, "Line/Conveyor/Speed").Variable!.PathText);
        Assert.Null(Of(plan, "Line/Speed").Variable);
    }

    [Fact]
    public void Abbreviations_and_German_names_meet_their_variables()
    {
        var plan = ModelBinder.Propose(
            [Signal("Oven/Temp"), Signal("Band/Drehzahl")],
            Variables("PLC/Oven/Temperature", "PLC/Oven/Door", "PLC/Band/Speed", "PLC/Band/Length"));

        Assert.Equal("PLC/Oven/Temperature", Of(plan, "Oven/Temp").Variable!.PathText);
        Assert.Equal("PLC/Band/Speed", Of(plan, "Band/Drehzahl").Variable!.PathText);
    }

    [Fact]
    public void A_signal_bound_already_keeps_its_variable_and_no_other_signal_gets_it()
    {
        var taken = Variable("PLC/Pump/Pressure");
        var plan = ModelBinder.Propose(
            [Signal("Line/Pump/Pressure", boundTo: taken.Address), Signal("Line/Pump2/Pressure")],
            [taken]);

        var bound = Of(plan, "Line/Pump/Pressure");
        Assert.True(bound.BoundAlready);
        Assert.Equal(taken, bound.Variable);
        Assert.Null(Of(plan, "Line/Pump2/Pressure").Variable);
        Assert.Empty(plan.Unmatched);
    }

    [Fact]
    public void The_variables_no_signal_got_are_listed()
    {
        var plan = ModelBinder.Propose([Signal("Line/Pump/Pressure")], Variables("PLC/Pump/Pressure", "PLC/Pump/Running"));

        Assert.Equal(["PLC/Pump/Running"], plan.Unmatched.Select(v => v.PathText));
    }

    [Fact]
    public void The_signals_of_a_model_are_its_leaves_and_its_elements_with_a_value()
    {
        var doc = CAEXDocument.New_CAEXDocument();
        var ih = doc.CAEXFile.InstanceHierarchy.Append("Plant");
        var station = ih.InternalElement.Append("ProcessingStation");
        var drill = station.InternalElement.Append("Drill");
        drill.InternalElement.Append("SpindleSpeed");
        var counter = station.InternalElement.Append("PartsCounter");
        var value = counter.Attribute.Append("Value");
        value.AttributeDataType = "xs:int";
        var motor = station.InternalElement.Append("Motor");
        motor.Attribute.Append("Value").AttributeDataType = "xs:double";
        motor.InternalElement.Append("Temperature");

        var signals = ModelBinder.Signals(ih);

        Assert.Equal(
            ["Plant/ProcessingStation/Drill/SpindleSpeed", "Plant/ProcessingStation/PartsCounter", "Plant/ProcessingStation/Motor", "Plant/ProcessingStation/Motor/Temperature"],
            signals.Select(s => s.PathText));
        Assert.Equal("xs:int", signals[1].DataType);
        Assert.Same(counter.Node, signals[1].Element!.Node);
    }

    [Fact]
    public void Binding_gives_each_kept_element_its_NodeId_and_a_Value()
    {
        var doc = CAEXDocument.New_CAEXDocument();
        var ih = doc.CAEXFile.InstanceHierarchy.Append("Plant");
        var speed = ih.InternalElement.Append("Drill").InternalElement.Append("SpindleSpeed");
        var plan = ModelBinder.Propose(ModelBinder.Signals(ih), Variables("MPS500/ST30_Processing/MD_Drilling_unit/DV_Drill_spindle/Speed"));

        var bound = ModelBinder.Apply(plan.Proposals.Where(p => p.Keep), "urn:practice-plant");

        Assert.Equal(1, bound);
        Assert.Equal(new UaNodeAddress(Plant, UaIdType.String, "MPS500/ST30_Processing/MD_Drilling_unit/DV_Drill_spindle/Speed", "urn:practice-plant"),
            AnnexANodeId.Of(speed));
        Assert.NotNull(speed.Attribute["Value"]);
    }

    // Review of 2026-11-18: an element bound by a browse path (or an alias, a BPR
    // DataVariable, the older aml-opcua-variable) counted as unbound; bind --apply
    // gave it a NodeId of another variable and the working binding was gone.
    [Theory]
    [InlineData("browse path")]
    [InlineData("aml-opcua-variable")]
    [InlineData("BPR DataVariable")]
    public void An_element_bound_another_way_is_bound_and_stays_as_it_is(string kind)
    {
        var doc = CAEXDocument.New_CAEXDocument();
        var speed = doc.CAEXFile.InstanceHierarchy.Append("Plant").InternalElement.Append("Station").InternalElement.Append("Speed");
        speed.Attribute.Append("Value");
        switch (kind)
        {
            case "browse path":
                var nodeId = speed.Attribute.Append("NodeId");
                var root = nodeId.Attribute.Append("RootNodeId");
                root.Attribute.Append("NamespaceUri").Value = Plant;
                root.Attribute.Append("StringId").Value = "Plant";
                var target = nodeId.Attribute.Append("BrowsePath").Attribute.Append("Elements").Attribute.Append("0").Attribute.Append("TargetName");
                target.Attribute.Append("NamespaceUri").Value = Plant;
                target.Attribute.Append("Name").Value = "Speed";
                break;
            case "aml-opcua-variable":
                speed.Attribute["Value"]!.Attribute.Append(LegacyOpcUaVariable.SubAttribute);
                break;
            default:
                var dv = speed.Attribute.Append("Speed");
                dv.Attribute.Append("NodeId");
                dv.Attribute.Append("RefDataSource");
                break;
        }
        var before = doc.CAEXFile.Node.ToString();

        var signal = Assert.Single(ModelBinder.OwnSignals(ModelBinder.Models(doc).First()));
        var plan = ModelBinder.Propose(new[] { signal }, new[] { Variable("Line2/Station/Speed") });
        var applied = ModelBinder.Apply(plan.Proposals.Where(p => p.Keep), "urn:server");

        Assert.True(signal.BoundOtherwise);
        Assert.True(Assert.Single(plan.Proposals).BoundAlready);
        Assert.Equal(0, applied);
        Assert.Equal(before, doc.CAEXFile.Node.ToString());
    }
}
