// A NodeSet read as a graph for the round trip analysis: nodes by
// namespace-qualified NodeId, their class, BrowseName and value, and their
// references with standard reference types resolved to names. Deliberately
// independent of the OPC UA stack: the analysis compares documents, it does
// not need a server's view of them.

using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OpcUaAml.Roundtrip;

public sealed record UaRef(string Type, bool IsForward, string Target);

public sealed class UaGraphNode
{
    public required string Id { get; init; }
    public required string NamespaceUri { get; init; }
    public required string NodeClass { get; init; }
    public required string BrowseName { get; init; }
    public string? Value { get; init; }
    public List<UaRef> References { get; } = new();
}

public sealed class UaGraph
{
    public const string Ua = "http://opcfoundation.org/UA/";

    private static readonly Dictionary<string, string> StandardReferences = new()
    {
        ["i=45"] = "HasSubtype", ["i=40"] = "HasTypeDefinition", ["i=46"] = "HasProperty",
        ["i=47"] = "HasComponent", ["i=35"] = "Organizes", ["i=37"] = "HasModellingRule",
        ["i=33"] = "HierarchicalReferences", ["i=32"] = "NonHierarchicalReferences",
        ["i=49"] = "HasOrderedComponent", ["i=17603"] = "HasInterface", ["i=41"] = "GeneratesEvent",
    };

    public static readonly Dictionary<string, string> ModellingRules = new()
    {
        [Ua + "|i=78"] = "Mandatory", [Ua + "|i=80"] = "Optional", [Ua + "|i=11508"] = "OptionalPlaceholder",
        [Ua + "|i=11510"] = "MandatoryPlaceholder", [Ua + "|i=83"] = "ExposesItsArray",
    };

    public Dictionary<string, UaGraphNode> Nodes { get; } = new(StringComparer.Ordinal);
    public List<string> Models { get; } = new();

    public static UaGraph Load(string path) => Load(SafeXml.Load(path));

    public static UaGraph Load(XDocument doc)
    {
        var root = doc.Root ?? throw new InvalidDataException("Empty NodeSet.");
        XNamespace ns = root.Name.Namespace;
        var uris = new List<string> { Ua };
        uris.AddRange(root.Element(ns + "NamespaceUris")?.Elements(ns + "Uri").Select(u => u.Value.Trim()) ?? Enumerable.Empty<string>());
        var aliases = root.Element(ns + "Aliases")?.Elements(ns + "Alias")
            .ToDictionary(a => (string)a.Attribute("Alias")!, a => a.Value.Trim()) ?? new();

        var g = new UaGraph();
        g.Models.AddRange(root.Element(ns + "Models")?.Elements(ns + "Model").Select(m => (string?)m.Attribute("ModelUri") ?? "") ?? Enumerable.Empty<string>());

        string Norm(string raw)
        {
            raw = raw.Trim();
            if (aliases.TryGetValue(raw, out var a)) raw = a;
            var m = Regex.Match(raw, @"^ns=(\d+);(.*)$");
            if (m.Success)
            {
                var i = int.Parse(m.Groups[1].Value);
                return (i < uris.Count ? uris[i] : "ns" + i) + "|" + m.Groups[2].Value;
            }
            var u = Regex.Match(raw, @"^nsu=(.*?);([isgb]=.*)$");
            return u.Success ? u.Groups[1].Value + "|" + u.Groups[2].Value : Ua + "|" + raw;
        }

        // Reference types outside the standard set are named by their alias.
        var aliasNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var a in aliases) aliasNames.TryAdd(Norm(a.Key), a.Key);

        foreach (var e in root.Elements().Where(e => e.Name.LocalName.StartsWith("UA", StringComparison.Ordinal)))
        {
            var id = Norm((string)e.Attribute("NodeId")!);
            var browse = (string?)e.Attribute("BrowseName") ?? "";
            var colon = browse.IndexOf(':');
            if (colon > 0 && browse[..colon].All(char.IsDigit)) browse = browse[(colon + 1)..];
            var node = new UaGraphNode
            {
                Id = id,
                NamespaceUri = id[..id.IndexOf('|')],
                NodeClass = e.Name.LocalName,
                BrowseName = browse,
                Value = e.Element(ns + "Value")?.Value.Trim(),
            };
            foreach (var r in e.Element(ns + "References")?.Elements(ns + "Reference") ?? Enumerable.Empty<XElement>())
            {
                var type = Norm((string)r.Attribute("ReferenceType")!);
                var name = type.StartsWith(Ua + "|", StringComparison.Ordinal) && StandardReferences.TryGetValue(type[(Ua.Length + 1)..], out var n)
                    ? n : aliasNames.TryGetValue(type, out var alias) ? alias : (string)r.Attribute("ReferenceType")!;
                var forward = !string.Equals((string?)r.Attribute("IsForward"), "false", StringComparison.OrdinalIgnoreCase);
                node.References.Add(new UaRef(name, forward, Norm(r.Value)));
            }
            g.Nodes[id] = node;
        }
        return g;
    }

    public UaGraphNode? Get(string id) => Nodes.TryGetValue(id, out var n) ? n : null;

    private Dictionary<string, List<(string Source, string Type)>>? _incoming;

    /// <summary>
    /// For each node, the nodes that point at it with an inverse reference (a
    /// source S with an inverse reference of type T to X means X -T-> S).
    /// Built once: the analysis asks for children of many nodes.
    /// </summary>
    private Dictionary<string, List<(string Source, string Type)>> Incoming()
    {
        if (_incoming != null) return _incoming;
        _incoming = new(StringComparer.Ordinal);
        foreach (var n in Nodes.Values)
            foreach (var r in n.References)
            {
                var key = r.IsForward ? "<" + r.Target : r.Target;
                if (!_incoming.TryGetValue(key, out var list)) _incoming[key] = list = new();
                list.Add((n.Id, r.Type));
            }
        return _incoming;
    }

    /// <summary>Targets of forward references of the given types, plus sources of inverse ones pointing here.</summary>
    public IEnumerable<UaGraphNode> Children(UaGraphNode node, params string[] types)
    {
        var ids = node.References.Where(r => r.IsForward && types.Contains(r.Type)).Select(r => r.Target).ToList();
        if (Incoming().TryGetValue(node.Id, out var inverse))
            ids.AddRange(inverse.Where(x => types.Contains(x.Type)).Select(x => x.Source));
        return ids.Distinct().Select(Get).Where(n => n != null)!;
    }

    /// <summary>The supertype, from an inverse HasSubtype on the node or a forward one on the supertype.</summary>
    public UaGraphNode? Supertype(UaGraphNode node)
    {
        var inverse = node.References.FirstOrDefault(r => r.Type == "HasSubtype" && !r.IsForward);
        if (inverse != null) return Get(inverse.Target) ?? new UaGraphNode { Id = inverse.Target, NamespaceUri = "", NodeClass = "?", BrowseName = inverse.Target };
        return Incoming().TryGetValue("<" + node.Id, out var fwd) && fwd.FirstOrDefault(x => x.Type == "HasSubtype") is { Source: not null } s
            ? Get(s.Source) : null;
    }

    public string? ModellingRuleOf(UaGraphNode node)
    {
        var r = node.References.FirstOrDefault(x => x.Type == "HasModellingRule" && x.IsForward);
        return r == null ? null : ModellingRules.TryGetValue(r.Target, out var name) ? name : r.Target;
    }
}
