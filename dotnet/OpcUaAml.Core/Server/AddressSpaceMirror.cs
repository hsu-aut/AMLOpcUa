// Takes a part of a running server's address space into an AML document:
// objects and variables become InternalElements carrying their Annex A NodeId,
// typed by the UA type's SystemUnitClass when the document holds it. This is
// the brownfield case: record an existing installation instead of modelling it.
//
// When the document already models a node (an element with the same NodeId,
// the planned object), the mirrored element becomes an aspect of it: a
// refBaseObj of the AutomationML object reference types names the planned
// element as base. The planned model is not changed.
//
// The document is written between awaits, so the awaits here resume on the
// caller's context (no ConfigureAwait(false)): called from the editor's UI
// thread, every write happens there, as Aml.Engine and the editor's tree need.

using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using OpcUaAml.Addressing;
using OpcUaAml.Links;

namespace OpcUaAml.Server;

/// <summary>What mirroring again does with elements whose node the server no longer has.</summary>
public enum VanishedNodes
{
    /// <summary>Keep them and report them.</summary>
    Report,

    /// <summary>Keep them, report them and mark them with the attribute NotOnServer; the mark goes when the node is back.</summary>
    Mark,

    /// <summary>Report them and remove them from the document.</summary>
    Remove,
}

public sealed class MirrorOptions
{
    /// <summary>Levels below the start node; 1 takes only its children.</summary>
    public int Depth { get; init; } = 3;

    /// <summary>Read the current value of every variable into a Value attribute.</summary>
    public bool ReadValues { get; init; } = true;

    /// <summary>Upper bound on nodes, against mirroring a whole server by accident.</summary>
    public int MaxNodes { get; init; } = DefaultMaxNodes;

    public const int DefaultMaxNodes = 2000;

    /// <summary>Told the number of nodes found so far while the selection is read from the server.</summary>
    public IProgress<int>? Progress { get; init; }

    /// <summary>What mirroring again does with elements whose node the server no longer has.</summary>
    public VanishedNodes Vanished { get; init; } = VanishedNodes.Report;

    /// <summary>The attribute that marks an element whose node the server no longer has (<see cref="VanishedNodes.Mark"/>).</summary>
    public const string NotOnServerAttribute = "NotOnServer";

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

/// <summary>What mirroring a selection did.</summary>
public sealed record SelectionMirrorResult(
    InternalElementType Server,
    int Nodes,
    int Created,
    int Updated,
    int Typed,
    int Linked,
    bool Truncated,
    IReadOnlyList<string> Vanished,
    IReadOnlyList<string> Notes);

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
        await Walk(client, start, root, 1, types, state, ct);
        if (options.ReadValues) await ReadValues(client, state, ct);
        return new MirrorResult(root, state.Nodes, state.Typed, state.Truncated) { Linked = state.Linked, Notes = state.Notes };
    }

    public const string ServerUriAttribute = "ServerUri";
    public const string EndpointAttribute = "EndpointUrl";

    /// <summary>
    /// The element of <paramref name="ih"/> that stands for the server, below
    /// which a selection was mirrored; null when the hierarchy holds no mirror of it.
    /// </summary>
    public static InternalElementType? MirroredServer(InstanceHierarchyType ih, UaClient client) =>
        ih.InternalElement.FirstOrDefault(e => e.Attribute[MirrorSelection.AttributeName] != null
            && (client.ServerUri != null ? e.Attribute[ServerUriAttribute]?.Value == client.ServerUri
                                         : e.Attribute[EndpointAttribute]?.Value == client.EndpointUrl));

    /// <summary>
    /// Takes what <paramref name="selection"/> covers into <paramref name="ih"/>:
    /// below an element for the server, each selected part with the nodes on its
    /// way from the Objects or Views folder. Nodes already mirrored there are
    /// found by NodeId and updated (type, value) instead of added again; nodes
    /// of the document the server no longer holds are reported, and kept,
    /// marked or removed as <see cref="MirrorOptions.Vanished"/> says.
    /// The selection is kept at the server element.
    /// </summary>
    public static async Task<SelectionMirrorResult> MirrorSelectionAsync(UaClient client, MirrorSelection selection,
        InstanceHierarchyType ih, MirrorOptions? options = null, CancellationToken ct = default)
    {
        options ??= new MirrorOptions();
        var plan = await MirrorPlan.BuildAsync(client, selection, options.MaxNodes, ct, options.Progress);
        var doc = ih.CAEXDocument;
        var types = TypeIndex(doc);
        var server = MirroredServer(ih, client) ?? ih.InternalElement.Append(UniqueName(ih, ServerName(client)));
        SetText(server, ServerUriAttribute, "xs:anyURI", client.ServerUri);
        SetText(server, EndpointAttribute, "xs:anyURI", client.EndpointUrl);
        var state = new State(options) { Planned = options.LinkToPlanned ? PlannedIndex(doc, ih, options.PlannedIn) : new() };
        var apply = new Apply(client, types, state);
        foreach (var root in plan.Roots) apply.Node(root, server);
        selection.WriteTo(server);
        if (options.ReadValues) await ReadValues(client, state, ct);
        return new SelectionMirrorResult(server, plan.Nodes, apply.Created, apply.Updated, state.Typed, state.Linked, plan.Truncated,
            apply.Vanished, state.Notes);
    }

    /// <summary>How many nodes a selection covers, without changing the document.</summary>
    public static async Task<(int Nodes, bool Truncated)> PreviewAsync(UaClient client, MirrorSelection selection, int maxNodes = 2000,
        CancellationToken ct = default, IProgress<int>? progress = null)
    {
        var plan = await MirrorPlan.BuildAsync(client, selection, maxNodes, ct, progress);
        return (plan.Nodes, plan.Truncated);
    }

    private sealed class Apply(UaClient client, Dictionary<UaNodeAddress, string> types, State state)
    {
        public int Created;
        public int Updated;
        public List<string> Vanished { get; } = new();

        public void Node(MirrorPlanNode node, InternalElementType parent)
        {
            var address = node.Item.Address with { ServerUri = null };
            var element = parent.InternalElement.FirstOrDefault(e => AddressOf(e) == address);
            if (element == null)
            {
                element = Create(parent, node.Item, types, client, state);
                Created++;
            }
            else
            {
                state.Nodes++;
                Updated++;
                // Back on the server: an earlier mark goes.
                if (element.Attribute[MirrorOptions.NotOnServerAttribute] is { } mark) element.Attribute.RemoveElement(mark);
                if (node.Item.TypeDefinition != null && types.TryGetValue(node.Item.TypeDefinition with { ServerUri = null }, out var path))
                {
                    element.RefBaseSystemUnitPath = path;
                    state.Typed++;
                }
                if (node.Item.NodeClass == "Variable") state.Variables.Add((element, node.Item.Address));
            }
            foreach (var child in node.Children) Node(child, element);
            if (node.OnServer == null) return;
            foreach (var child in element.InternalElement.ToList())
            {
                if (AddressOf(child) is not { } a || node.OnServer.Contains(a)) continue;
                Vanished.Add($"{PathOf(child)} ({a})");
                switch (state.Options.Vanished)
                {
                    case VanishedNodes.Mark when child.Attribute[MirrorOptions.NotOnServerAttribute] == null:
                        var mark = child.Attribute.Append(MirrorOptions.NotOnServerAttribute);
                        mark.AttributeDataType = "xs:boolean";
                        mark.Value = "true";
                        mark.Description = "The server no longer had this node when the mirror was last updated.";
                        break;
                    case VanishedNodes.Remove:
                        element.InternalElement.RemoveElement(child);
                        break;
                }
            }
        }

        private static string PathOf(InternalElementType e)
        {
            var parts = new List<string>();
            for (CAEXBasicObject? o = e; o is InternalElementType ie; o = ie.CAEXParent as CAEXBasicObject) parts.Insert(0, ie.Name);
            return string.Join("/", parts);
        }
    }

    private static UaNodeAddress? AddressOf(InternalElementType e)
    {
        try { return AnnexANodeId.Of(e) is { } a ? a with { ServerUri = null } : null; }
        catch (AddressingException) { return null; }
    }

    /// <summary>A readable name for the server: the last part of its ApplicationUri, or the endpoint's host.</summary>
    private static string ServerName(UaClient client)
    {
        var uri = client.ServerUri;
        var last = uri?.Split(':', '/').LastOrDefault(s => s.Length > 0);
        if (!string.IsNullOrEmpty(last)) return last;
        return Uri.TryCreate(client.EndpointUrl, UriKind.Absolute, out var endpoint) ? endpoint.Host : "Server";
    }

    private static void SetText(InternalElementType element, string name, string type, string? value)
    {
        if (value == null) return;
        var a = element.Attribute[name] ?? element.Attribute.Append(name);
        a.AttributeDataType = type;
        a.Value = value;
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
        foreach (var child in await client.BrowseAsync(node.Address, ct))
        {
            if (child.NodeClass == "Method") continue;
            if (state.Nodes >= state.Options.MaxNodes) { state.Truncated = true; return; }
            var created = Create(element, child, types, client, state);
            await Walk(client, child, created, level + 1, types, state, ct);
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
    /// aspects themselves (they carry a refBaseObj, as linked mirrors do) and
    /// elements of a mirrored selection.
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
            if (ie.Node.AncestorsAndSelf().Contains(excluded) || ObjectReferences.BaseOf(ie) != null || InMirror(ie)) continue;
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

    /// <summary>Whether an element is part of a selection mirrored earlier: that records the server, it plans nothing.</summary>
    private static bool InMirror(InternalElementType ie)
    {
        for (CAEXBasicObject? o = ie; o is InternalElementType e; o = e.CAEXParent as CAEXBasicObject)
            if (e.Attribute[MirrorSelection.AttributeName] != null) return true;
        return false;
    }

    private static async Task ReadValues(UaClient client, State state, CancellationToken ct)
    {
        if (state.Variables.Count == 0) return;
        var results = await client.ReadManyAsync(state.Variables.Select(v => v.Address).ToList(), ct);
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

    private static string UniqueName(InstanceHierarchyType ih, string name)
    {
        var taken = ih.InternalElement.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        if (!taken.Contains(name)) return name;
        for (var i = 2; ; i++)
            if (!taken.Contains($"{name}_{i}")) return $"{name}_{i}";
    }

    private static string UniqueName(IInternalElementContainer parent, string name)
    {
        var taken = parent.InternalElement.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        if (!taken.Contains(name)) return name;
        for (var i = 2; ; i++)
            if (!taken.Contains($"{name}_{i}")) return $"{name}_{i}";
    }
}
