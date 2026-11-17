// The bound values of a document as they stand: every element or attribute
// that reading values fills (the kinds ValueSnapshot knows: an Annex A NodeId
// with a Value attribute, the older aml-opcua-variable, BPR DataVariables),
// with what it is bound to. Keeping values live compares a fresh set with the
// one it started from, so an element bound, copied or deleted while values
// are live is followed or dropped without switching live off and on.
//
// Nothing here asks a server: a binding is compared by what the document
// says, before a browse path or an alias is resolved. Bindings to another
// server are in the set too; a change among them costs one needless restart.

using System.Xml.Linq;
using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using OpcUaAml.Addressing;

namespace OpcUaAml.Server;

public sealed class BindingSet
{
    private readonly Dictionary<XElement, (string What, string Binding)> _entries;

    private BindingSet(Dictionary<XElement, (string What, string Binding)> entries) => _entries = entries;

    /// <summary>How many values are bound.</summary>
    public int Count => _entries.Count;

    /// <summary>The bound values of the document's instance hierarchies.</summary>
    public static BindingSet Of(CAEXDocument doc)
    {
        var entries = new Dictionary<XElement, (string, string)>();
        foreach (var ie in doc.CAEXFile.InstanceHierarchy.SelectMany(ih => ih.Descendants<InternalElementType>()))
        {
            if (ie.Attribute["Value"] != null && Binding(() => AnnexANodeId.IndirectOf(ie)?.ToString() ?? AnnexANodeId.Of(ie)?.ToString()) is { } nodeId)
                entries[ie.Node] = (ie.Name, nodeId);
            foreach (var legacy in AllAttributes(ie).Where(LegacyOpcUaVariable.IsBound))
                entries[legacy.Node] = ($"{ie.Name}/{legacy.Name}", Binding(() => LegacyOpcUaVariable.Read(legacy).ToString()) ?? "");
            foreach (var dv in ie.Attribute.Where(BprDataVariable.IsDataVariable))
                entries[dv.Node] = ($"{ie.Name}/{dv.Name}", Binding(() => BprDataVariable.Read(dv, doc).ToString()) ?? "");
        }
        return new BindingSet(entries);
    }

    /// <summary>A binding as text; one the document holds but cannot be read is a binding all the same.</summary>
    private static string? Binding(Func<string?> read)
    {
        try { return read(); }
        catch (Exception ex) when (ex is AddressingException or FormatException) { return "unreadable: " + ex.Message; }
    }

    /// <summary>Whether the same values are bound to the same nodes.</summary>
    public bool SameAs(BindingSet other) =>
        Count == other.Count && _entries.All(e => other._entries.TryGetValue(e.Key, out var o) && o.Binding == e.Value.Binding);

    /// <summary>What is bound now and was not, or to another node, before; by name.</summary>
    public IReadOnlyList<string> AddedSince(BindingSet before) =>
        _entries.Where(e => !before._entries.TryGetValue(e.Key, out var b) || b.Binding != e.Value.Binding).Select(e => e.Value.What).ToList();

    /// <summary>What was bound before and is not now, or is bound to another node now; by name.</summary>
    public IReadOnlyList<string> RemovedSince(BindingSet before) => before.AddedSince(this);

    private static IEnumerable<AttributeType> AllAttributes(IObjectWithAttributes owner) =>
        owner.Attribute.SelectMany(a => new[] { a }.Concat(AllAttributes(a)));
}
