// Finite state machines (OPC 10000-5 Annex B) as Annex A leaves them in a
// document: a type that derives from FiniteStateMachineType holds its states
// and transitions as InternalElements of StateType and TransitionType, their
// numbers as a StateNumber or TransitionNumber child, and the ends of a
// transition as ExternalInterfaces (FromState, ToState, HasCause) whose
// ReferenceIds name the node they lead to.
//
// The modeler shows the same machine while it is being built; this side reads
// it back out of the document, for the documentation and for a diagram.

using Aml.Engine.CAEX;
using OpcUaAml.Addressing;

namespace OpcUaAml.Types;

/// <summary>One state of a machine, with the number the model gives it.</summary>
public sealed record MachineState(string Name, int? Number, string? NodeId);

/// <summary>One transition, with where it leads and what causes it.</summary>
public sealed record MachineTransition(string Name, int? Number, string? From, string? To, string? Cause);

/// <summary>The states and transitions of one machine type.</summary>
public sealed record StateMachine(IReadOnlyList<MachineState> States, IReadOnlyList<MachineTransition> Transitions)
{
    public bool IsEmpty => States.Count == 0 && Transitions.Count == 0;

    /// <summary>The name of the state a transition end leads to, for a table or a label.</summary>
    public string NameOf(string? nodeId) =>
        nodeId is null ? "" : States.FirstOrDefault(s => s.NodeId == nodeId)?.Name ?? "?";
}

public static class StateMachines
{
    private const string MachineType = "FiniteStateMachineType";
    private const string StateType = "StateType";
    private const string TransitionType = "TransitionType";

    /// <summary>Whether the type is a finite state machine, but not the base type itself.</summary>
    public static bool IsMachine(SystemUnitFamilyType type) =>
        type.Name != MachineType && UaTypes.Chain(type).Any(t => t.Name == MachineType);

    /// <summary>
    /// The machine a type describes, including what it inherits. A state or
    /// transition of a subtype hides the one of the same name above it, as the
    /// declarations do.
    /// </summary>
    public static StateMachine Read(SystemUnitFamilyType type)
    {
        var states = new List<MachineState>();
        var transitions = new List<MachineTransition>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var t in UaTypes.Chain(type))
        {
            foreach (var child in t.InternalElement)
            {
                if (!seen.Add(child.Name)) continue;
                if (Is(child, StateType))
                {
                    states.Add(new MachineState(child.Name, Number(child, "StateNumber"), NodeIdOf(child)));
                }
                else if (Is(child, TransitionType))
                {
                    transitions.Add(new MachineTransition(
                        child.Name,
                        Number(child, "TransitionNumber"),
                        End(child, "FromState"),
                        End(child, "ToState"),
                        CauseName(t, End(child, "HasCause"))));
                }
            }
        }

        return new StateMachine(
            states.OrderBy(s => s.Number ?? int.MaxValue).ThenBy(s => s.Name, StringComparer.Ordinal).ToList(),
            transitions.OrderBy(t => t.Number ?? int.MaxValue).ThenBy(t => t.Name, StringComparer.Ordinal).ToList());
    }

    /// <summary>Whether the element instantiates that UA type, or a type derived from it.</summary>
    private static bool Is(InternalElementType element, string typeName)
    {
        var type = UaTypes.TypeOf(element);
        return type != null && UaTypes.Chain(type).Any(t => t.Name == typeName);
    }

    /// <summary>The NodeId of an element, as Annex A writes it into the ID ("nsu=…;i=…").</summary>
    private static string? NodeIdOf(SystemUnitClassType element) =>
        UaNodeAddress.TryParse(Uri.UnescapeDataString(element.ID ?? ""), null, out var address) ? address!.ToString() : null;

    private static int? Number(InternalElementType element, string name)
    {
        var value = element.InternalElement.FirstOrDefault(c => c.Name == name)?.Attribute["Value"]?.Value;
        return int.TryParse(value, out var number) ? number : null;
    }

    /// <summary>
    /// Where one end of a transition leads: the interface of that name carries
    /// the NodeId in its ReferenceIds, the same list the other references use.
    /// </summary>
    private static string? End(InternalElementType transition, string interfaceName)
    {
        var ei = transition.ExternalInterface.FirstOrDefault(i => i.Name == interfaceName);
        var ids = ei?.Attribute["ReferenceIds"];
        var first = ids?.Attribute.FirstOrDefault()?.Value ?? ids?.Value;
        if (string.IsNullOrEmpty(first)) return null;
        // The list holds the NodeId escaped, as the ID of an element does.
        var text = Uri.UnescapeDataString(first);
        return UaNodeAddress.TryParse(text, null, out var address) ? address!.ToString() : text;
    }

    /// <summary>The name of the method that causes a transition, looked up in the type that holds it.</summary>
    private static string? CauseName(SystemUnitFamilyType type, string? cause)
    {
        if (cause is null) return null;
        foreach (var t in UaTypes.Chain(type))
        {
            foreach (var child in t.InternalElement)
            {
                if (NodeIdOf(child) == cause) return child.Name;
            }
        }
        return null;
    }
}
