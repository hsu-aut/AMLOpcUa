using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using OpcUaAml.Diagram;
using OpcUaAml.Import;
using OpcUaAml.NodeSets;
using OpcUaAml.Types;
using Xunit.Abstractions;

namespace OpcUaAml.Tests;

/// <summary>
/// The state machine fixture converted once: a small NodeSet, but the
/// conversion reads the whole base model.
/// </summary>
public sealed class MachineDocument
{
    public CAEXDocument Document { get; }

    public MachineDocument()
    {
        var catalog = NodeSetCatalog.Create(Array.Empty<string>());
        var result = NodeSetImporter.Convert(Fixtures.Path("modeldesign", "Machine.NodeSet2.xml"), catalog);
        Document = CAEXDocument.New_CAEXDocument();
        LibraryMerger.Merge(Document, result.Document);
    }

    public SystemUnitFamilyType Type(string name) =>
        (SystemUnitFamilyType)Document.FindByPath($"[SUC_http://example.org/Pump/]/[{name}]")!;
}

public class StateMachineTests(MachineDocument machine, ITestOutputHelper output) : IClassFixture<MachineDocument>
{
    [Fact]
    public void A_machine_type_is_read_out_of_the_document_with_its_states_and_ends()
    {
        var type = machine.Type("PumpStateMachineType");
        Assert.True(StateMachines.IsMachine(type));
        Assert.False(StateMachines.IsMachine(machine.Type("PumpType")));

        var read = StateMachines.Read(type);
        Assert.Equal(["Idle", "Running", "Fault"], read.States.Select(s => s.Name));
        Assert.Equal([1, 2, 3], read.States.Select(s => s.Number));

        var transition = read.Transitions.Single(t => t.Name == "IdleToRunning");
        output.WriteLine($"from={transition.From} to={transition.To} states={string.Join(", ", read.States.Select(s => s.NodeId))}");
        Assert.Equal("Idle", read.NameOf(transition.From));
        Assert.Equal("Running", read.NameOf(transition.To));
        Assert.Equal("Start", transition.Cause);
        Assert.Equal(1, transition.Number);
    }

    [Fact]
    public void The_machine_is_drawn_as_a_state_chart()
    {
        var read = StateMachines.Read(machine.Type("PumpStateMachineType"));
        var svg = StateChart.Svg(read);

        Assert.StartsWith("<svg", svg);
        Assert.Contains(">Idle<", svg);
        Assert.Contains(">Running<", svg);
        Assert.Contains("IdleToRunning / Start()", svg);
        // Three states, three transitions between different states: three lines.
        Assert.Equal(3, svg.Split("<line ").Length - 1);
    }

    [Fact]
    public void A_type_that_is_no_machine_gives_an_empty_machine()
    {
        var read = StateMachines.Read(machine.Type("PumpType"));
        Assert.True(read.IsEmpty);
        Assert.Equal("", StateChart.Svg(read));
    }

    [Fact]
    public void The_documentation_shows_the_machine()
    {
        var html = OpcUaAml.Documentation.ModelDocumentation.Html(machine.Document, "http://example.org/Pump/");

        Assert.Contains("State machine (OPC 10000-5 Annex B)", html);
        Assert.Contains("<th>Transition</th>", html);
        Assert.Contains("<td>IdleToRunning</td>", html);
        Assert.Contains("<summary>State chart</summary>", html);
    }
}
