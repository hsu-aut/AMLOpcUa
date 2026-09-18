// Takes a part of a running server's address space into an AML document:
// objects and variables become InternalElements carrying their Annex A NodeId,
// typed by the UA type's SystemUnitClass when the document holds it. This is
// the brownfield case: record an existing installation instead of modelling it.
//
// When the document already models a node (an element with the same NodeId,
// the planned object), the mirrored element becomes an aspect of it: a
// refBaseObj of the AutomationML object reference types names the planned
// element as base. The planned model is not changed.

using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using OpcUaAml.Addressing;
using OpcUaAml.Links;

namespace OpcUaAml.Server;

public sealed class MirrorOptions
{
    /// <summary>Levels below the start node; 1 takes only its children.</summary>
    public int Depth { get; init; } = 3;

    /// <summary>Read the current value of every variable into a Value attribute.</summary>
    public bool ReadValues { get; init; } = true;

    /// <summary>Upper bound on nodes, against mirroring a whole server by accident.</summary>
    public int MaxNodes { get; init; } = 2000;

    /// <summary>Reference the planned element of a node, found by NodeId, with refBaseObj.</summary>
    public bool LinkToPlanned { get; init; } = true;

    /// <summary>
    /// Where the planned model is (an InstanceHierarchy or element). Null
    /// searches the whole document, where an earlier mirror without links
    /// counts as planned too.
    /// </summary>
    public IInternalElementContainer? PlannedIn { get; init; }
}

public sealed record MirrorResult(InternalElementType Root, int Nodes, int Typed, bool Truncated)
{
    /// <summary>Mirrored elements that reference their planned element with refBaseObj.</summary>
    public int Linked { get; init; }

    /// <summary>What prevented or qualifies a link: several planned elements, a different type.</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public static class AddressSpaceMirror
{
    /// <summary>
    /// Creates an element for <paramref name="start"/> under <paramref name="parent"/>
    /// and fills it with the subtree below it.
    /// </summary>
    public static async Task<MirrorResult> MirrorAsync(UaClient client, UaBrowseItem start,
        IInternalElementContainer parent, MirrorOptions? options = null, CancellationToken ct = default)
    {
        options ??= new MirrorOptions();
        var doc = ((CAEXBasicObject)parent).CAEXDocument;
        var types = TypeIndex(doc);
        var state = new State(options) { Planned = options.LinkToPlanned ? PlannedIndex(doc, parent, options.PlannedIn) : new() };

        var root = Create(parent, start, types, client, state);
        await Walk(client, start, root, 1, types, state, ct).ConfigureAwait(false);
        if (options.ReadValues) await ReadValues(client, state, ct).ConfigureAwait(false);
        return new MirrorResult(root, state.Nodes, state.Typed, state.Truncated) { Linked = state.Linked, Notes = state.Notes };
    }

    private sealed class State(MirrorOptions options)
    {
        public MirrorOptions Options { get; } = options;
        public int Nodes;
        public int Typed;
        public bool Truncated;
        public int Linked;
        public List<string> Notes { get; } = new();
        public List<(InternalElementType Element, UaNodeAddress Address)> Variables { get; } = new();
        public Dictionary<UaNodeAddress, List<InternalElementType>> Planned { get; init; } = new();
    }

    private static async Task Walk(UaClient client, UaBrowseItem node, InternalElementType element, int level,
        Dictionary<UaNodeAddress, string> types, State state, CancellationToken ct)
    {
        if (level > state.Options.Depth) return;
        foreach (var child in await client.BrowseAsync(node.Address, ct).ConfigureAwait(false))
        {
            if (child.NodeClass == "Method") continue;
            if (state.Nodes >= state.Options.MaxNodes) { state.Truncated = true; return; }
            var created = Create(element, child, types, client, state);
            await Walk(client, child, created, level + 1, types, state, ct).ConfigureAwait(false);
        }
    }

    private static InternalElementType Create(IInternalElementContainer parent, UaBrowseItem node,
        Dictionary<UaNodeAddress, string> types, UaClient client, State state)
    {
        var element = parent.InternalElement.Append(UniqueName(parent, node.BrowseName));
        state.Nodes++;
        if (node.TypeDefinition != null && types.TryGetValue(node.TypeDefinition, out var path))
        {
            element.RefBaseSystemUnitPath = path;
            state.Typed++;
        }
        AnnexANodeId.Write(element, node.Address with { ServerUri = client.ServerUri });
        if (node.NodeClass == "Variable") state.Variables.Add((element, node.Address));
        if (state.Planned.TryGetValue(node.Address with { ServerUri = null }, out var planned)) LinkToPlanned(element, planned, state);
        return element;
    }

    private static void LinkToPlanned(InternalElementType element, List<InternalElementType> planned, State state)
    {
        if (planned.Count > 1)
        {
            state.Notes.Add($"{element.Name}: {planned.Count} planned elements carry this NodeId ({string.Join(", ", planned.Select(p => p.Name))}); not linked.");
            return;
        }
        var plan = planned[0];
        ObjectReferences.SetBase(element, plan);
        state.Linked++;
        // refBaseObj expects the same type; a difference is worth knowing, not a reason to drop the link.
        if (!string.IsNullOrEmpty(plan.RefBaseSystemUnitPath) && !string.IsNullOrEmpty(element.RefBaseSystemUnitPath)
            && plan.RefBaseSystemUnitPath != element.RefBaseSystemUnitPath)
            state.Notes.Add($"{element.Name}: the server says {element.RefBaseSystemUnitPath}, the planned element '{plan.Name}' is {plan.RefBaseSystemUnitPath}.");
    }

    /// <summary>
    /// Elements that model a node, by NodeId: everything with an Annex A NodeId
    /// in the scope and outside the target container, except elements that are
    /// aspects themselves (they carry a refBaseObj, as linked mirrors do).
    /// </summary>
    private static Dictionary<UaNodeAddress, List<InternalElementType>> PlannedIndex(CAEXDocument doc,
        IInternalElementContainer target, IInternalElementContainer? scope)
    {
        var excluded = ((CAEXBasicObject)target).Node;
        var candidates = scope switch
        {
            null => doc.CAEXFile.InstanceHierarchy.SelectMany(ih => ih.Descendants<InternalElementType>()),
            InternalElementType e => e.Descendants<InternalElementType>().Prepend(e),
            InstanceHierarchyType ih => ih.Descendants<InternalElementType>(),
            _ => ((SystemUnitClassType)scope).Descendants<InternalElementType>(),
        };
        var index = new Dictionary<UaNodeAddress, List<InternalElementType>>();
        foreach (var ie in candidates)
        {
            if (ie.Node.AncestorsAndSelf().Contains(excluded) || ObjectReferences.BaseOf(ie) != null) continue;
            UaNodeAddress? address;
            try { address = AnnexANodeId.Of(ie); }
            catch (AddressingException) { continue; }
            if (address == null) continue;
            var key = address with { ServerUri = null };
            if (!index.TryGetValue(key, out var list)) index[key] = list = new();
            list.Add(ie);
        }
        return index;
    }

    private static async Task ReadValues(UaClient client, State state, CancellationToken ct)
    {
        if (state.Variables.Count == 0) return;
        var results = await client.ReadManyAsync(state.Variables.Select(v => v.Address).ToList(), ct).ConfigureAwait(false);
        for (var i = 0; i < results.Count; i++)
        {
            if (!results[i].Good) continue;
            ValueSnapshot.SetValue(state.Variables[i].Element, results[i]);
        }
    }

    /// <summary>
    /// UA type NodeId → SystemUnitClass path, from the Annex A NodeId attribute
    /// every generated class carries.
    /// </summary>
    public static Dictionary<UaNodeAddress, string> TypeIndex(CAEXDocument doc)
    {
        var index = new Dictionary<UaNodeAddress, string>();
        foreach (var (path, type) in Types.UaTypes.AllTypes(doc))
        {
            try
            {
                if (AnnexANodeId.Of(type) is { } address) index.TryAdd(address with { ServerUri = null }, path);
            }
            catch (AddressingException) { /* a class without a usable NodeId is not addressable */ }
        }
        return index;
    }

    private static string UniqueName(IInternalElementContainer parent, string name)
    {
        var taken = parent.InternalElement.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        if (!taken.Contains(name)) return name;
        for (var i = 2; ; i++)
            if (!taken.Contains($"{name}_{i}")) return $"{name}_{i}";
    }
}
