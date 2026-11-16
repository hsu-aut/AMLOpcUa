using Aml.Editor.Plugin.OpcUa;
using Aml.Engine.CAEX;
using OpcUaAml.Addressing;
using OpcUaAml.Binding;

namespace PluginTests;

/// <summary>
/// Bind model… shows a row for each value of the model with the variable
/// proposed for it: the sure ones ticked, the others for the user to check or
/// to pick another candidate; what is ticked is bound.
/// </summary>
public class ModelBindRowsTests
{
    private static ServerVariable Variable(string path, string? type = null) =>
        new(new UaNodeAddress("urn:plant", UaIdType.String, path), path.Split('/'), type);

    private static BindingPlan Plan() => ModelBinder.Propose(
        [
            new ModelSignal("Plant/Pump/Pressure".Split('/'), null, null),
            new ModelSignal("Plant/Station/PartsCounter".Split('/'), null, null),
            new ModelSignal("Plant/Turntable".Split('/'), null, null),
            new ModelSignal("Plant/Valve/Open".Split('/'), null, Variable("PLC/Valve/Open").Address),
        ],
        [Variable("PLC/Pump/Pressure"), Variable("PLC/Station/PartsProduced"), Variable("PLC/Station/RevisionCounter"), Variable("PLC/Valve/Open"), Variable("PLC/Door/Closed")]);

    private static ModelBindRow Row(IReadOnlyList<ModelBindRow> rows, string path) => rows.Single(r => r.ModelPath == path);

    [Fact]
    public void A_sure_proposal_is_ticked_and_one_to_check_is_not()
    {
        var rows = ModelBindRows.From(Plan());

        Assert.True(Row(rows, "Plant/Pump/Pressure").Keep);
        var counter = Row(rows, "Plant/Station/PartsCounter");
        Assert.False(counter.Keep);
        Assert.True(counter.CanKeep);
        Assert.Contains(counter.Choices, c => c.Variable?.PathText == "PLC/Station/PartsProduced");
        Assert.Null(counter.Choices[^1].Variable);
    }

    [Fact]
    public void A_value_without_a_variable_and_one_bound_already_cannot_be_ticked()
    {
        var rows = ModelBindRows.From(Plan());

        Assert.False(Row(rows, "Plant/Turntable").CanKeep);
        Assert.False(Row(rows, "Plant/Valve/Open").CanKeep);
        Assert.False(Row(rows, "Plant/Valve/Open").Keep);
    }

    [Fact]
    public void Picking_another_candidate_ticks_the_row_and_binds_that_one()
    {
        var rows = ModelBindRows.From(Plan());
        var counter = Row(rows, "Plant/Station/PartsCounter");
        var other = counter.Choices.First(c => c.Variable != null && c != counter.Chosen);

        counter.Chosen = other;

        Assert.True(counter.Keep);
        var kept = ModelBindRows.Kept(rows);
        Assert.Contains(kept, p => p.Signal.PathText == "Plant/Station/PartsCounter" && p.Variable == other.Variable);
        Assert.Contains(kept, p => p.Signal.PathText == "Plant/Pump/Pressure");
        Assert.DoesNotContain(kept, p => p.Signal.PathText == "Plant/Valve/Open");
    }

    [Fact]
    public void Picking_none_unticks_the_row()
    {
        var rows = ModelBindRows.From(Plan());
        var pressure = Row(rows, "Plant/Pump/Pressure");

        pressure.Chosen = pressure.Choices[^1];

        Assert.False(pressure.Keep);
        Assert.False(pressure.CanKeep);
        Assert.DoesNotContain(ModelBindRows.Kept(rows), p => p.Signal.PathText == "Plant/Pump/Pressure");
    }

    [Fact]
    public void A_screen_reader_hears_the_value_its_variable_and_the_match()
    {
        var rows = ModelBindRows.From(Plan());

        Assert.Equal("Plant/Pump/Pressure: PLC/Pump/Pressure, " + Row(rows, "Plant/Pump/Pressure").Match, Row(rows, "Plant/Pump/Pressure").ToString());
    }

    [Fact]
    public void The_report_lists_every_value_and_the_variables_no_value_got()
    {
        var plan = Plan();
        var rows = ModelBindRows.From(plan);

        var report = ModelBindRows.Report(plan, rows).Split(Environment.NewLine);

        // A NodeId has a semicolon of its own, so it is quoted.
        Assert.Contains(report, l => l.StartsWith("Plant/Pump/Pressure;PLC/Pump/Pressure;\"nsu=urn:plant;s=PLC/Pump/Pressure\";", StringComparison.Ordinal));
        Assert.Contains(report, l => l.StartsWith("Plant/Turntable;;;", StringComparison.Ordinal));
        Assert.Contains(report, l => l.StartsWith("PLC/Door/Closed;", StringComparison.Ordinal));
    }

    [Fact]
    public void Only_elements_of_ones_own_are_models_to_bind()
    {
        var doc = CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX3_0);
        var own = doc.CAEXFile.InstanceHierarchy.Append("Plant");
        own.InternalElement.Append("Pump").InternalElement.Append("Pressure");
        var taken = doc.CAEXFile.InstanceHierarchy.Append("Taken");
        var server = taken.InternalElement.Append("OpcUaServer");
        server.Attribute.Append(OpcUaAml.Server.AddressSpaceMirror.EndpointAttribute).Value = "opc.tcp://localhost:4840";
        server.InternalElement.Append("Speed");
        doc.CAEXFile.InstanceHierarchy.Append("Empty");

        var models = ModelBindRows.Models(doc);

        Assert.Equal(["Plant"], models.Select(m => m.Name));
        Assert.True(ModelBindRows.IsOwn(own.InternalElement["Pump"]!));
        Assert.False(ModelBindRows.IsOwn(server.InternalElement["Speed"]!));
    }
}
