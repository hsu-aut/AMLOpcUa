// Finite state machines (OPC 10000-5 Annex B) as Annex A leaves them in a
// document: a type that derives from FiniteStateMachineType holds its states
// and transitions as InternalElements of StateType and TransitionType, their
// numbers as a StateNumber or TransitionNumber child, and the ends of a
// transition as ExternalInterfaces (FromState, ToState, HasCause) whose
// ReferenceIds name the node they lead to.
//
// The modeler shows the same machine while it is being built; this side reads
// it back out of the document, for the documentation and for a diagram.

using System.Net;
using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using OpcUaAml.Addressing;

namespace OpcUaAml.Types;

/// <summary>The machine a state holds of its own (HasSubStateMachine), and the type of that machine.</summary>
public sealed record SubMachine(string Name, string? TypeName);

/// <summary>One state of a machine, with the number the model gives it and a machine of its own, if any.</summary>
public sealed record MachineState(string Name, int? Number, string? NodeId, SubMachine? SubMachine = null);

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
    // The base types by NodeId, not by name: a model of its own may well hold
    // a type called StateType, and only the one of the UA namespace is meant
    // (OPC 10000-5 Annex B).
    private const string UaNamespace = "http://opcfoundation.org/UA/";
    private const string MachineType = "nsu=" + UaNamespace + ";i=2771";
    private const string StateType = "nsu=" + UaNamespace + ";i=2307";
    private const string TransitionType = "nsu=" + UaNamespace + ";i=2310";

    /// <summary>Whether the type is a finite state machine, but not the base type itself.</summary>
    public static bool IsMachine(SystemUnitFamilyType type) =>
        NodeIdOf(type) != MachineType && UaTypes.Chain(type).Any(t => NodeIdOf(t) == MachineType);

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
                    states.Add(new MachineState(child.Name, Number(child, "StateNumber"), NodeIdOf(child),
                        SubMachineOf(t, End(child, "HasSubStateMachine"))));
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
    private static bool Is(InternalElementType element, string typeNodeId)
    {
        var type = UaTypes.TypeOf(element);
        return type != null && UaTypes.Chain(type).Any(t => NodeIdOf(t) == typeNodeId);
    }

    /// <summary>
    /// The NodeId of an element, as Annex A writes it into the ID ("nsu=…;i=…"),
    /// encoded with WebUtility.UrlEncode: a space is '+'.
    /// </summary>
    private static string? NodeIdOf(SystemUnitClassType element) =>
        UaNodeAddress.TryParse(WebUtility.UrlDecode(element.ID ?? ""), null, out var address) ? address!.ToString() : null;

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
        var text = WebUtility.UrlDecode(first);
        return UaNodeAddress.TryParse(text, null, out var address) ? address!.ToString() : text;
    }

    /// <summary>
    /// The name of the method that causes a transition. It usually sits in the
    /// machine type, but may sit anywhere the model puts it, so the document
    /// is asked for the node itself when the type does not hold it.
    /// </summary>
    private static string? CauseName(SystemUnitFamilyType type, string? cause) => (Node(type, cause) as CAEXObject)?.Name;

    /// <summary>
    /// The machine a state holds of its own: a component of the machine type,
    /// which the state names through HasSubStateMachine (OPC 10000-5 Annex B).
    /// </summary>
    private static SubMachine? SubMachineOf(SystemUnitFamilyType type, string? machine) =>
        Node(type, machine) is InternalElementType element
            ? new SubMachine(element.Name, UaTypes.TypeOf(element)?.Name)
            : null;

    /// <summary>The node a reference leads to, in the type first, else anywhere in the document.</summary>
    private static CAEXBasicObject? Node(SystemUnitFamilyType type, string? nodeId)
    {
        if (nodeId is null) return null;
        foreach (var t in UaTypes.Chain(type))
        {
            foreach (var child in t.InternalElement)
            {
                if (NodeIdOf(child) == nodeId) return child;
            }
        }
        return type.CAEXDocument?.FindByID(WebUtility.UrlEncode(nodeId), true, null) as CAEXBasicObject;
    }
}
