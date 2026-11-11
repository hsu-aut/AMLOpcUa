using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using OpcUaAml.Diagram;
using OpcUaAml.Import;
using OpcUaAml.NodeSets;
using OpcUaAml.Types;
using System.Xml.Linq;
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
    public void A_state_with_a_machine_of_its_own_names_it_and_its_type()
    {
        var read = StateMachines.Read(machine.Type("PumpStateMachineType"));

        // The sub-machine is a component of the machine type, not a state of it.
        Assert.Equal(["Idle", "Running", "Fault"], read.States.Select(s => s.Name));
        var fault = read.States.Single(s => s.Name == "Fault");
        Assert.Equal(new SubMachine("FaultHandling", "FaultHandlingStateMachineType"), fault.SubMachine);
        Assert.Null(read.States.Single(s => s.Name == "Idle").SubMachine);
    }

    [Fact]
    public void A_state_with_a_machine_of_its_own_is_drawn_with_a_second_outline_and_the_type()
    {
        var svg = StateChart.Svg(StateMachines.Read(machine.Type("PumpStateMachineType")));

        Assert.Equal(1, svg.Split("rx=\"12\"").Length - 1);
        Assert.Contains(">FaultHandlingStateMachineType<", svg);
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
    public void Two_transitions_between_the_same_states_are_drawn_apart()
    {
        // A machine usually has a way there and a way back. On one line they
        // cover each other, and one name hides the other.
        var there = new MachineTransition("IdleToRunning", 1, "i=1", "i=2", null);
        var back = new MachineTransition("RunningToIdle", 2, "i=2", "i=1", null);
        var read = new StateMachine(
            new[] { new MachineState("Idle", 1, "i=1"), new MachineState("Running", 2, "i=2") },
            new[] { there, back });

        var svg = StateChart.Svg(read);

        Assert.Equal(0, svg.Split("<line ").Length - 1);          // no straight line
        Assert.Equal(2, svg.Split(" Q ").Length - 1);             // two bows, the arrow marker aside
        var places = System.Text.RegularExpressions.Regex.Matches(svg,
            @"<text x=""([-\d.]+)"" y=""([-\d.]+)""[^>]*>(IdleToRunning|RunningToIdle)<")
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();
        Assert.Equal(2, places.Count);
        Assert.Equal(2, places.Distinct().Count());
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
        Assert.Contains("<th>Sub-state machine</th>", html);
        Assert.Contains("<td>FaultHandling (FaultHandlingStateMachineType)</td>", html);
    }

    /// <summary>
    /// The fixture with string NodeIds that hold spaces, which Opc2Aml writes
    /// into IDs as '+': Idle is "Idle State", and Start, moved to PumpType, is
    /// "Start Pump", so the cause of a transition lies outside the machine type
    /// and is looked for in the whole document.
    /// </summary>
    internal static string MachineWithSpaces(string folder)
    {
        XNamespace ua = "http://opcfoundation.org/UA/2011/03/UANodeSet.xsd";
        var doc = XDocument.Load(Fixtures.Path("modeldesign", "Machine.NodeSet2.xml"));
        XElement Node(string id) => doc.Root!.Elements().Single(e => (string?)e.Attribute("NodeId") == id);
        Node("ns=1;i=1002").Element(ua + "References")!.Elements()
            .Single(r => r.Value == "ns=1;i=1003" && (string?)r.Attribute("ReferenceType") == "HasComponent").Remove();
        Node("ns=1;i=1000").Element(ua + "References")!.Add(new XElement(ua + "Reference", new XAttribute("ReferenceType", "HasComponent"), "ns=1;i=1003"));
        var start = Node("ns=1;i=1003");
        start.SetAttributeValue("ParentNodeId", "ns=1;i=1000");
        start.Element(ua + "References")!.Elements().Single(r => (string?)r.Attribute("ReferenceType") == "HasComponent").Value = "ns=1;i=1000";
        var path = Path.Combine(folder, "Machine.NodeSet2.xml");
        File.WriteAllText(path, doc.ToString().Replace("ns=1;i=1003", "ns=1;s=Start Pump").Replace("ns=1;i=1004", "ns=1;s=Idle State"));
        return path;
    }

    [Fact]
    public void NodeIds_with_spaces_are_read_as_they_are()
    {
        var folder = Directory.CreateTempSubdirectory("opcuaaml-spaces-").FullName;
        try
        {
            var converted = NodeSetImporter.Convert(MachineWithSpaces(folder), NodeSetCatalog.Create(Array.Empty<string>()));
            var document = CAEXDocument.New_CAEXDocument();
            LibraryMerger.Merge(document, converted.Document);
            var type = (SystemUnitFamilyType)document.FindByPath("[SUC_http://example.org/Pump/]/[PumpStateMachineType]")!;

            var read = StateMachines.Read(type);

            var idle = read.States.Single(s => s.Name == "Idle");
            Assert.Equal("nsu=http://example.org/Pump/;s=Idle State", idle.NodeId);
            var transition = read.Transitions.Single(t => t.Name == "IdleToRunning");
            Assert.Equal(idle.NodeId, transition.From);
            Assert.Equal("Start", transition.Cause);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
