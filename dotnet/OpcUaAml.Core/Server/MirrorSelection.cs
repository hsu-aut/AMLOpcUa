// What of a server's address space goes into a document: selected nodes with
// how much of what they hold, all instances of a type below a node, Views, and
// filters over all of it. A selection is kept in the document, at the element
// that stands for the server, so the same part can be mirrored again.
//
// Planning is separate from writing: MirrorPlan browses the server and gives
// the tree of nodes a selection covers, which a preview counts and the mirror
// writes into the document.

using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using OpcUaAml.Addressing;

namespace OpcUaAml.Server;

public enum MirrorScope
{
    /// <summary>The node alone.</summary>
    Node,
    /// <summary>The node and what it holds directly.</summary>
    Children,
    /// <summary>The node and everything below it, down to the selection's depth.</summary>
    Subtree,
    /// <summary>Every instance of a type below the node, each with its subtree.</summary>
    InstancesOf,
}

/// <summary>
/// One part of a selection. For <see cref="MirrorScope.InstancesOf"/>,
/// <see cref="Type"/> is the type; <see cref="View"/> restricts browsing to
/// the references of a View.
/// </summary>
public sealed record MirrorItem(UaNodeAddress Node, MirrorScope Scope, UaNodeAddress? Type = null, UaNodeAddress? View = null);

public sealed record MirrorFilter
{
    /// <summary>Leave out nodes held by HasProperty.</summary>
    public bool SkipProperties { get; init; }

    /// <summary>Leave out variables; objects only.</summary>
    public bool ObjectsOnly { get; init; }

    /// <summary>Leave out the Server object with its diagnostics.</summary>
    public bool HideServer { get; init; } = true;

    /// <summary>Only nodes of these namespaces, with what they hold; empty takes all.</summary>
    public IReadOnlyList<string> Namespaces { get; init; } = Array.Empty<string>();

    private static readonly UaNodeAddress Server = new("http://opcfoundation.org/UA/", UaIdType.Numeric, "2253");

    /// <summary>Whether a node found below a selected one is taken.</summary>
    public bool Admits(UaBrowseItem node) =>
        node.NodeClass != "Method"
        && !(HideServer && node.Address with { ServerUri = null } == Server)
        && !(SkipProperties && node.ReferenceType == "HasProperty")
        && !(ObjectsOnly && node.NodeClass == "Variable")
        && (Namespaces.Count == 0 || Namespaces.Contains(node.Address.NamespaceUri));
}

public sealed class MirrorSelection
{
    public List<MirrorItem> Items { get; init; } = new();
    public MirrorFilter Filter { get; init; } = new();

    /// <summary>Levels below a node selected with its subtree; 0 has no limit but the node limit.</summary>
    public int Depth { get; init; } = 3;

    /// <summary>Nodes left out with what they hold, such as instances of a type the user deselected.</summary>
    public HashSet<UaNodeAddress> Excluded { get; init; } = new();

    public const string AttributeName = "MirrorSelection";

    /// <summary>Writes the selection as an attribute of <paramref name="owner"/>, replacing an earlier one.</summary>
    public void WriteTo(IObjectWithAttributes owner)
    {
        owner.Attribute[AttributeName]?.Remove();
        var a = owner.Attribute.Append(AttributeName);
        a.Description = "The part of the server's address space mirrored here (AMLOpcUa), for mirroring it again.";
        Value(a, "Depth", "xs:int", Depth.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Value(a, "SkipProperties", "xs:boolean", Filter.SkipProperties ? "true" : "false");
        Value(a, "ObjectsOnly", "xs:boolean", Filter.ObjectsOnly ? "true" : "false");
        Value(a, "HideServer", "xs:boolean", Filter.HideServer ? "true" : "false");
        var namespaces = a.Attribute.Append("Namespaces");
        for (var i = 0; i < Filter.Namespaces.Count; i++) Value(namespaces, $"Namespace{i + 1}", "xs:anyURI", Filter.Namespaces[i]);
        var items = a.Attribute.Append("Items");
        for (var i = 0; i < Items.Count; i++)
        {
            var item = items.Attribute.Append($"Item{i + 1}");
            Value(item, "Node", "xs:string", Items[i].Node.ToString());
            Value(item, "Scope", "xs:string", Items[i].Scope.ToString());
            if (Items[i].Type is { } type) Value(item, "Type", "xs:string", type.ToString());
            if (Items[i].View is { } view) Value(item, "View", "xs:string", view.ToString());
        }
        var excluded = a.Attribute.Append("Excluded");
        var n = 0;
        foreach (var e in Excluded) Value(excluded, $"Node{++n}", "xs:string", e.ToString());
    }

    /// <summary>The selection kept at <paramref name="owner"/>, or null.</summary>
    public static MirrorSelection? ReadFrom(IObjectWithAttributes owner)
    {
        if (owner.Attribute[AttributeName] is not { } a) return null;
        string? Text(AttributeType parent, string name) => parent.Attribute[name]?.Value;
        UaNodeAddress? Address(string? text) => string.IsNullOrEmpty(text) ? null : UaNodeAddress.Parse(text);
        var items = new List<MirrorItem>();
        foreach (var item in a.Attribute["Items"]?.Attribute ?? Enumerable.Empty<AttributeType>())
        {
            if (Address(Text(item, "Node")) is not { } node || !Enum.TryParse<MirrorScope>(Text(item, "Scope"), out var scope)) continue;
            items.Add(new MirrorItem(node, scope, Address(Text(item, "Type")), Address(Text(item, "View"))));
        }
        return new MirrorSelection
        {
            Items = items,
            Depth = int.TryParse(Text(a, "Depth"), out var depth) ? depth : 3,
            Filter = new MirrorFilter
            {
                SkipProperties = Text(a, "SkipProperties") == "true",
                ObjectsOnly = Text(a, "ObjectsOnly") == "true",
                HideServer = Text(a, "HideServer") != "false",
                Namespaces = (a.Attribute["Namespaces"]?.Attribute ?? Enumerable.Empty<AttributeType>()).Select(x => x.Value).Where(v => !string.IsNullOrEmpty(v)).ToList(),
            },
            Excluded = (a.Attribute["Excluded"]?.Attribute ?? Enumerable.Empty<AttributeType>())
                .Select(x => Address(x.Value)).OfType<UaNodeAddress>().ToHashSet(),
        };
    }

    private static void Value(AttributeType parent, string name, string type, string value)
    {
        var a = parent.Attribute.Append(name);
        a.AttributeDataType = type;
        a.Value = value;
    }
}

/// <summary>A node of a plan: what the mirror takes, and what the server holds below it where that was browsed.</summary>
public sealed class MirrorPlanNode
{
    public MirrorPlanNode(UaBrowseItem item) => Item = item;

    public UaBrowseItem Item { get; }
    public List<MirrorPlanNode> Children { get; } = new();

    /// <summary>
    /// Every node the server holds below this one, taken or not, when its
    /// children were browsed; a document element below it that is not among
    /// them has vanished from the server. Null for nodes only on a path.
    /// </summary>
    public HashSet<UaNodeAddress>? OnServer { get; set; }

    internal int ExpandedLevels = -1;
}

/// <summary>The nodes a selection covers, from the Objects and Views folders down.</summary>
public sealed class MirrorPlan
{
    public List<MirrorPlanNode> Roots { get; } = new();
    public int Nodes => _index.Count;
    public bool Truncated { get; private set; }

    /// <summary>Instances found for InstancesOf items, before exclusions, for showing them to the user.</summary>
    public List<UaBrowseItem> Found { get; } = new();

    private readonly Dictionary<UaNodeAddress, MirrorPlanNode> _index = new();

    public static async Task<MirrorPlan> BuildAsync(UaClient client, MirrorSelection selection, int maxNodes = 2000, CancellationToken ct = default)
    {
        var plan = new MirrorPlan();
        foreach (var item in selection.Items)
        {
            if (plan.Truncated) break;
            var targets = new List<(UaNodeAddress Node, int Levels)>();
            if (item.Scope == MirrorScope.InstancesOf)
            {
                if (item.Type == null) continue;
                var types = await client.SubtypesAsync(item.Type, ct).ConfigureAwait(false);
                var found = await client.InstancesOfAsync(item.Node, types, ct: ct).ConfigureAwait(false);
                plan.Found.AddRange(found);
                targets.AddRange(found.Where(f => !selection.Excluded.Contains(f.Address)).Select(f => (f.Address, LevelsOf(MirrorScope.Subtree, selection))));
            }
            else if (!selection.Excluded.Contains(item.Node with { ServerUri = null }))
            {
                targets.Add((item.Node, LevelsOf(item.Scope, selection)));
            }

            foreach (var (node, levels) in targets)
            {
                var path = await client.PathAsync(node, ct).ConfigureAwait(false);
                MirrorPlanNode? parent = null;
                foreach (var p in path)
                {
                    parent = plan.GetOrAdd(parent, p);
                    if (parent == null) break;
                }
                if (parent != null) await plan.ExpandAsync(client, parent, levels, item.View, selection, maxNodes, ct).ConfigureAwait(false);
            }
        }
        return plan;
    }

    private static int LevelsOf(MirrorScope scope, MirrorSelection selection) => scope switch
    {
        MirrorScope.Node => 0,
        MirrorScope.Children => 1,
        _ => selection.Depth <= 0 ? int.MaxValue : selection.Depth,
    };

    private MirrorPlanNode? GetOrAdd(MirrorPlanNode? parent, UaBrowseItem item)
    {
        var key = item.Address with { ServerUri = null };
        if (_index.TryGetValue(key, out var existing)) return existing;
        var node = new MirrorPlanNode(item);
        _index[key] = node;
        (parent?.Children ?? Roots).Add(node);
        return node;
    }

    private async Task ExpandAsync(UaClient client, MirrorPlanNode node, int levels, UaNodeAddress? view, MirrorSelection selection,
        int maxNodes, CancellationToken ct)
    {
        if (levels <= 0 || node.ExpandedLevels >= levels || node.Item.NodeClass == "Method") return;
        node.ExpandedLevels = levels;
        // A View organizes its nodes by its own references; below them, browsing keeps to the View.
        var isView = view != null && node.Item.Address with { ServerUri = null } == view with { ServerUri = null };
        var children = await client.BrowseAsync(node.Item.Address, isView ? null : view, ct).ConfigureAwait(false);
        node.OnServer = children.Where(c => c.NodeClass != "Method").Select(c => c.Address with { ServerUri = null }).ToHashSet();
        foreach (var child in children)
        {
            if (!selection.Filter.Admits(child) || selection.Excluded.Contains(child.Address with { ServerUri = null })) continue;
            if (!_index.ContainsKey(child.Address with { ServerUri = null }) && _index.Count >= maxNodes)
            {
                Truncated = true;
                return;
            }
            var added = GetOrAdd(node, child)!;
            await ExpandAsync(client, added, levels == int.MaxValue ? levels : levels - 1, view, selection, maxNodes, ct).ConfigureAwait(false);
        }
    }
}
