// Chain A: an OPC UA NodeSet through OPC 10000-83 Annex A into AML and back
// out through the AML-UA-XSLT rules. The second NodeSet does not describe the
// UA model any more but the AML document that describes it: every AML class
// is an ObjectType in a namespace per AML library. The analysis therefore
// matches by name and asks, per UA concept, whether it can still be found and
// whether it is still expressed the way OPC UA expresses it.

namespace OpcUaAml.Roundtrip;

public static class UaAmlUaAnalysis
{
    public const string AmlNamespace = "http://opcfoundation.org/UA/AML/";

    public static IReadOnlyList<Criterion> Analyze(UaGraph original, UaGraph roundtrip, string modelUri,
        IEnumerable<UaGraph>? requiredModels = null)
    {
        // Names of nodes that neither document contains: the UA base model and
        // the models the original requires, such as DI.
        var names = new Dictionary<string, string>(BaseNames.Value, StringComparer.Ordinal);
        foreach (var model in requiredModels ?? Enumerable.Empty<UaGraph>())
            foreach (var n in model.Nodes.Values) names.TryAdd(n.Id, n.BrowseName);

        var sucNs = AmlNamespace + "SUC_" + modelUri;
        var atlNs = AmlNamespace + "ATL_" + modelUri;
        var iclNs = AmlNamespace + "ICL_" + modelUri;

        var rtTypes = roundtrip.Nodes.Values
            .Where(n => n.NamespaceUri == sucNs && (n.NodeClass is "UAObjectType" or "UAVariableType"))
            .GroupBy(n => n.BrowseName).ToDictionary(g => g.Key, g => g.First());

        var types = original.Nodes.Values
            .Where(n => n.NamespaceUri == modelUri && (n.NodeClass is "UAObjectType" or "UAVariableType"))
            .OrderBy(n => n.BrowseName, StringComparer.Ordinal).ToList();

        var present = new Tally("Types present, by name");
        var nodeClass = new Tally("Type keeps its node class");
        var supertype = new Tally("Supertype kept");
        var nodeId = new Tally("Original NodeId recoverable", "NodeId attribute");
        var declarations = new Tally("Instance declarations present, by name");
        var declClass = new Tally("Declaration keeps its node class");
        var declType = new Tally("Declaration keeps its type definition");
        var ruleNative = new Tally("ModellingRule as HasModellingRule");
        var ruleAttr = new Tally("ModellingRule recoverable", "ModellingRule attribute");
        var methods = new Tally("Methods stay Methods");

        foreach (var t in types)
        {
            rtTypes.TryGetValue(t.BrowseName, out var rt);
            present.Add(rt != null, t.BrowseName);
            if (rt == null) continue;

            nodeClass.Add(rt.NodeClass == t.NodeClass, $"{t.BrowseName} ({t.NodeClass[2..]} -> {rt.NodeClass[2..]})");
            var oSuper = original.Supertype(t);
            var rSuper = roundtrip.Supertype(rt);
            supertype.Add(oSuper != null && rSuper != null && NameOf(names, original, oSuper) == NameOf(names, roundtrip, rSuper),
                $"{t.BrowseName} ({(oSuper == null ? "-" : NameOf(names, original, oSuper))} -> {(rSuper == null ? "-" : NameOf(names, roundtrip, rSuper))})");
            nodeId.Add(RecoveredNodeId(roundtrip, rt) == Identifier(t.Id), t.BrowseName);

            var rtChildren = roundtrip.Children(rt, "HasComponent", "HasProperty", "HasOrderedComponent")
                .Where(c => !c.BrowseName.Contains(';'))
                // The export names the properties it adds for the AML side
                // "<owner>_<name>" (AML_ID, Version, ...); a declaration of the
                // same name, such as FX CM's Version, is the other node.
                .GroupBy(c => c.BrowseName)
                .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Id.EndsWith("_" + c.BrowseName, StringComparison.Ordinal)).First());
            foreach (var d in original.Children(t, "HasComponent", "HasProperty", "HasOrderedComponent")
                         .Where(d => d.NamespaceUri == modelUri))
            {
                var path = $"{t.BrowseName}/{d.BrowseName}";
                rtChildren.TryGetValue(d.BrowseName, out var rd);
                declarations.Add(rd != null, path);
                if (d.NodeClass == "UAMethod") methods.Add(rd?.NodeClass == "UAMethod", path);
                if (rd == null) continue;

                declClass.Add(rd.NodeClass == d.NodeClass, $"{path} ({d.NodeClass[2..]} -> {rd.NodeClass[2..]})");
                var oType = TypeDefinitionName(names, original, d);
                var rType = TypeDefinitionName(names, roundtrip, rd);
                if (oType != null) declType.Add(oType == rType, $"{path} ({oType} -> {rType ?? "-"})");

                var rule = original.ModellingRuleOf(d);
                if (rule != null)
                {
                    ruleNative.Add(roundtrip.ModellingRuleOf(rd) == rule, path);
                    ruleAttr.Add(RecoveredRule(roundtrip, rd) == rule, $"{path} ({rule})");
                }
            }
        }

        var dataTypes = new Tally("DataTypes present, by name");
        var dataTypesNative = new Tally("DataTypes stay DataTypes");
        var rtAtl = roundtrip.Nodes.Values.Where(n => n.NamespaceUri == atlNs).GroupBy(n => n.BrowseName).ToDictionary(g => g.Key, g => g.First());
        foreach (var dt in original.Nodes.Values.Where(n => n.NamespaceUri == modelUri && n.NodeClass == "UADataType"))
        {
            rtAtl.TryGetValue(dt.BrowseName, out var r);
            dataTypes.Add(r != null, dt.BrowseName);
            dataTypesNative.Add(r?.NodeClass == "UADataType", dt.BrowseName);
        }

        var refTypes = new Tally("ReferenceTypes present, by name");
        var refTypesNative = new Tally("ReferenceTypes stay ReferenceTypes");
        var rtIcl = roundtrip.Nodes.Values.Where(n => n.NamespaceUri == iclNs).GroupBy(n => n.BrowseName).ToDictionary(g => g.Key, g => g.First());
        foreach (var rtp in original.Nodes.Values.Where(n => n.NamespaceUri == modelUri && n.NodeClass == "UAReferenceType"))
        {
            rtIcl.TryGetValue(rtp.BrowseName, out var r);
            refTypes.Add(r != null, rtp.BrowseName);
            refTypesNative.Add(r?.NodeClass == "UAReferenceType", rtp.BrowseName);
        }

        return new[] { present, nodeClass, supertype, nodeId, declarations, declClass, declType, ruleNative, ruleAttr, methods,
                       dataTypes, dataTypesNative, refTypes, refTypesNative }.Select(t => t.ToCriterion()).ToList();
    }

    /// <summary>A node's name; for nodes the document does not contain, its name in the base or a required model.</summary>
    private static string NameOf(IReadOnlyDictionary<string, string> names, UaGraph g, UaGraphNode n) =>
        n.NodeClass != "?" ? n.BrowseName : names.GetValueOrDefault(n.Id) ?? n.Id;

    private static string? TypeDefinitionName(IReadOnlyDictionary<string, string> names, UaGraph g, UaGraphNode n)
    {
        var target = n.References.FirstOrDefault(r => r.Type == "HasTypeDefinition" && r.IsForward)?.Target;
        if (target == null) return null;
        if (g.Get(target) is { } t) return t.BrowseName;
        return names.GetValueOrDefault(target) ?? target[(target.IndexOf('|') + 1)..].Replace("s=", "");
    }

    /// <summary>BrowseNames of the UA base model, from the bundled NodeSet, by graph id.</summary>
    private static readonly Lazy<Dictionary<string, string>> BaseNames = new(() =>
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var file = Path.Combine(NodeSets.NodeSetCatalog.BundledFolder, "Opc.Ua.NodeSet2.xml");
        if (!File.Exists(file)) return names;
        using var reader = System.Xml.XmlReader.Create(file, new System.Xml.XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true });
        while (reader.Read())
        {
            if (reader.NodeType != System.Xml.XmlNodeType.Element || !reader.LocalName.StartsWith("UA", StringComparison.Ordinal)) continue;
            var nodeId = reader.GetAttribute("NodeId");
            var browse = reader.GetAttribute("BrowseName");
            if (nodeId != null && browse != null) names[UaGraph.Ua + "|" + nodeId] = browse;
        }
        return names;
    });

    private static string Identifier(string id) => id[(id.IndexOf('|') + 1)..];

    /// <summary>The identifier kept in the NodeId attribute: NodeId/RootNodeId/(Numeric|String|Guid|Opaque)Id.</summary>
    private static string? RecoveredNodeId(UaGraph g, UaGraphNode node)
    {
        var root = g.Children(node, "HasComponent", "HasProperty").FirstOrDefault(c => c.BrowseName == "NodeId") is { } nid
            ? g.Children(nid, "HasComponent", "HasProperty").FirstOrDefault(c => c.BrowseName == "RootNodeId") : null;
        if (root == null) return null;
        foreach (var (name, prefix) in new[] { ("NumericId", "i="), ("StringId", "s="), ("GuidId", "g="), ("OpaqueId", "b=") })
            if (g.Children(root, "HasComponent", "HasProperty").FirstOrDefault(c => c.BrowseName == name)?.Value is { Length: > 0 } v)
                return prefix + v;
        return null;
    }

    /// <summary>The ModellingRule attribute Annex A puts on the child's end of its parent reference.</summary>
    private static string? RecoveredRule(UaGraph g, UaGraphNode declaration)
    {
        foreach (var iface in g.Children(declaration, "HasComponent"))
        {
            var rule = g.Children(iface, "HasComponent", "HasProperty").FirstOrDefault(c => c.BrowseName == "ModellingRule");
            if (rule?.Value is { Length: > 0 } v) return v;
        }
        return null;
    }

    internal sealed class Tally(string name, string? note = null)
    {
        private int _total;
        private int _kept;
        private readonly List<string> _lost = new();

        public void Add(bool kept, string item)
        {
            _total++;
            if (kept) _kept++;
            else _lost.Add(item);
        }

        public Criterion ToCriterion() => new(name, _total, _kept, _lost, note);
    }
}
