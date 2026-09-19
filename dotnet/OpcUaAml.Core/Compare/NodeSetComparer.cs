// Structural comparison of two UANodeSets as graphs.
//
// Two NodeSets are equal when they declare the same namespaces, aliases and
// models and the same nodes with the same attributes and references. What
// does not change the graph is ignored: comments, whitespace, the order of
// nodes and references, alias names versus the NodeIds they stand for, and
// PublicationDate unless asked for. Used for the export conformance tests
// against AML-UA-XSLT and for AML/UA round trips.

using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OpcUaAml.Compare;

public sealed class NodeSetComparer
{
    private static readonly XNamespace Ua = "http://opcfoundation.org/UA/2011/03/UANodeSet.xsd";

    /// <summary>Compare the PublicationDate of models and required models.</summary>
    public bool ComparePublicationDates { get; init; }

    /// <summary>
    /// Write NodeIds and BrowseNames with namespace URIs instead of indexes
    /// ("nsu=uri;s=x", "{uri}name"), so two documents with differently ordered
    /// namespace tables still compare their nodes. The tables themselves are
    /// compared in order either way.
    /// </summary>
    public bool ResolveNamespaceIndexes { get; init; } = true;

    public IReadOnlyList<Difference> Compare(XDocument left, XDocument right)
    {
        var l = Flatten(left);
        var r = Flatten(right);
        var diffs = new List<Difference>();
        foreach (var (path, lv) in l)
        {
            if (!r.TryGetValue(path, out var rv)) diffs.Add(new Difference(DifferenceKind.OnlyLeft, path, lv));
            else if (lv != rv) diffs.Add(new Difference(DifferenceKind.Changed, path, $"'{lv}' vs '{rv}'"));
        }
        foreach (var (path, rv) in r)
        {
            if (!l.ContainsKey(path)) diffs.Add(new Difference(DifferenceKind.OnlyRight, path, rv));
        }
        return diffs.OrderBy(d => d.Path, StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<Difference> Compare(string leftPath, string rightPath) =>
        Compare(XDocument.Load(leftPath), XDocument.Load(rightPath));

    /// <summary>
    /// Every comparable fact of a NodeSet as path to value, e.g.
    /// <c>node:nsu=...;s=CAEXFile/@BrowseName</c> or
    /// <c>node:nsu=...;s=CAEXFile/ref:i=46&gt;nsu=...;s=CAEXFile_FileName</c>.
    /// A NodeId that occurs twice gets a "#2" suffix, a repeated reference too.
    /// </summary>
    public Dictionary<string, string> Flatten(XDocument doc)
    {
        var root = doc.Root ?? throw new ArgumentException("The document has no root element.", nameof(doc));
        var facts = new Dictionary<string, string>(StringComparer.Ordinal);
        var uris = root.Element(Ua + "NamespaceUris")?.Elements(Ua + "Uri").Select(u => u.Value.Trim()).ToList() ?? new List<string>();
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var alias in root.Element(Ua + "Aliases")?.Elements(Ua + "Alias") ?? Enumerable.Empty<XElement>())
            aliases.TryAdd((string?)alias.Attribute("Alias") ?? "", alias.Value.Trim());
        var ctx = new Context(uris, aliases, ResolveNamespaceIndexes);

        for (var i = 0; i < uris.Count; i++) facts[$"namespace[{i + 1}]"] = uris[i];
        foreach (var (alias, target) in aliases) facts[$"alias:{alias}"] = ctx.NodeId(target);

        foreach (var model in root.Element(Ua + "Models")?.Elements(Ua + "Model") ?? Enumerable.Empty<XElement>())
        {
            var p = Unique(facts, $"model:{(string?)model.Attribute("ModelUri")}");
            facts[p] = $"version={(string?)model.Attribute("Version")}{Date(model)}";
            foreach (var req in model.Elements(Ua + "RequiredModel"))
                facts[Unique(facts, $"{p}/requires:{(string?)req.Attribute("ModelUri")}")] = $"version={(string?)req.Attribute("Version")}{Date(req)}";
        }

        // NodeIds inside values (an ExtensionObject's TypeId, an Argument's
        // DataType) compare like NodeId attributes. A TypeId names an encoding
        // node; it compares as the DataType it encodes and the encoding's name,
        // since two files may number the encodings differently.
        var nodes = root.Elements().Where(e => e.Name.Namespace == Ua && e.Name.LocalName.StartsWith("UA", StringComparison.Ordinal)).ToList();
        var encodings = new Dictionary<string, string>(StringComparer.Ordinal);
        var browseNames = nodes.GroupBy(n => ctx.NodeId((string?)n.Attribute("NodeId") ?? ""))
            .ToDictionary(g => g.Key, g => ctx.BrowseName((string?)g.First().Attribute("BrowseName")) ?? "", StringComparer.Ordinal);
        foreach (var node in nodes)
            foreach (var r in node.Element(Ua + "References")?.Elements(Ua + "Reference") ?? Enumerable.Empty<XElement>())
            {
                if (ctx.NodeId((string?)r.Attribute("ReferenceType") ?? "") != "i=38") continue;
                var forward = !string.Equals((string?)r.Attribute("IsForward"), "false", StringComparison.OrdinalIgnoreCase);
                var self = ctx.NodeId((string?)node.Attribute("NodeId") ?? "");
                var other = ctx.NodeId(r.Value);
                var (dataType, encoding) = forward ? (self, other) : (other, self);
                encodings.TryAdd(encoding, $"encoding of {dataType}: {browseNames.GetValueOrDefault(encoding)}");
            }
        string ValueNodeId(string raw)
        {
            var id = ctx.NodeId(raw);
            return encodings.TryGetValue(id, out var encoding) ? encoding : id;
        }

        foreach (var node in nodes)
        {
            var p = Unique(facts, "node:" + ctx.NodeId((string?)node.Attribute("NodeId") ?? ""));
            facts[p] = node.Name.LocalName;
            Fact(facts, p, "BrowseName", ctx.BrowseName((string?)node.Attribute("BrowseName")));
            Fact(facts, p, "ParentNodeId", ctx.NodeIdOrNull((string?)node.Attribute("ParentNodeId")));
            Fact(facts, p, "DataType", ctx.NodeIdOrNull((string?)node.Attribute("DataType")));
            Fact(facts, p, "MethodDeclarationId", ctx.NodeIdOrNull((string?)node.Attribute("MethodDeclarationId")));
            foreach (var attribute in new[] { "ValueRank", "ArrayDimensions", "IsAbstract", "Symmetric", "EventNotifier" })
                Fact(facts, p, attribute, (string?)node.Attribute(attribute));
            Fact(facts, p, "DisplayName", Texts(node, "DisplayName"));
            Fact(facts, p, "Description", Texts(node, "Description"));
            Fact(facts, p, "Documentation", Texts(node, "Documentation"));
            Fact(facts, p, "InverseName", Texts(node, "InverseName"));
            if (node.Element(Ua + "Value") is { } value) facts[p + "/@Value"] = NormalizeValue(value, ValueNodeId);
            if (node.Element(Ua + "Definition") is { } definition)
            {
                facts[p + "/definition"] = string.Join(" ", new[] { ctx.BrowseName((string?)definition.Attribute("Name")) }
                    .Concat(new[] { "IsUnion", "IsOptionSet" }.Where(a => (string?)definition.Attribute(a) == "true")));
                var i = 0;
                foreach (var field in definition.Elements(Ua + "Field"))
                {
                    var dataType = (string?)field.Attribute("DataType");
                    facts[$"{p}/definition/field[{i++}]"] = string.Join(" ", new[]
                    {
                        (string?)field.Attribute("Name"),
                        dataType == null ? null : "type=" + ctx.NodeId(dataType),
                        Named(field, "ValueRank", "-1"), Named(field, "ArrayDimensions", "0"), Named(field, "Value", null),
                        Named(field, "IsOptional", "false"),
                        Texts(field, "Description") is { } d ? "description=" + d : null,
                    }.Where(s => s != null));
                }
            }

            foreach (var reference in node.Element(Ua + "References")?.Elements(Ua + "Reference") ?? Enumerable.Empty<XElement>())
            {
                var type = ctx.NodeId((string?)reference.Attribute("ReferenceType") ?? "");
                var forward = !string.Equals((string?)reference.Attribute("IsForward"), "false", StringComparison.OrdinalIgnoreCase);
                var target = ctx.NodeId(reference.Value);
                facts[Unique(facts, $"{p}/ref:{type}{(forward ? ">" : "<")}{target}")] = "";
            }
        }
        return facts;

        string Date(XElement model) => ComparePublicationDates ? $" date={(string?)model.Attribute("PublicationDate")}" : "";
    }

    private static void Fact(Dictionary<string, string> facts, string node, string name, string? value)
    {
        if (value != null) facts[$"{node}/@{name}"] = value;
    }

    /// <summary>"Name=value" of an attribute, or null when it is missing or has its default.</summary>
    private static string? Named(XElement e, string name, string? defaultValue) =>
        (string?)e.Attribute(name) is { } v && v != defaultValue ? $"{name}={v}" : null;

    private static string Unique(Dictionary<string, string> facts, string path)
    {
        if (!facts.ContainsKey(path)) return path;
        for (var n = 2; ; n++)
            if (!facts.ContainsKey($"{path}#{n}")) return $"{path}#{n}";
    }

    private static string? Texts(XElement node, string name)
    {
        var elements = node.Elements(Ua + name).ToList();
        return elements.Count == 0 ? null : string.Join(" | ", elements.Select(e => e.Value));
    }

    /// <summary>
    /// A Value as "Type:text". Text that is XML (AdditionalInformation and
    /// the like) is compared structurally: namespaces, attribute order and
    /// whitespace between elements do not count.
    /// </summary>
    public static string NormalizeValue(XElement value, Func<string, string>? nodeId = null)
    {
        var sb = new StringBuilder();
        foreach (var item in value.Elements())
        {
            sb.Append(item.Name.LocalName).Append(':');
            if (item.HasElements) sb.Append(Canonical(item, nodeId));
            else sb.Append(Number(item.Name.LocalName, item.Value) ?? CanonicalText(item.Value));
            sb.Append(';');
        }
        return sb.ToString();
    }

    private static readonly HashSet<string> Numeric = new(StringComparer.Ordinal)
    {
        "SByte", "Byte", "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64", "Float", "Double",
    };

    /// <summary>A number in one spelling ("0.0" and "0" are the same Float); null for anything else.</summary>
    private static string? Number(string element, string text) =>
        Numeric.Contains(element) && double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? d.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
            : null;

    private static string CanonicalText(string text)
    {
        if (!text.TrimStart().StartsWith('<')) return text;
        try
        {
            return Canonical(XElement.Parse(text, LoadOptions.None), null);
        }
        catch (System.Xml.XmlException)
        {
            return text;
        }
    }

    private static string Canonical(XElement e, Func<string, string>? nodeId)
    {
        var sb = new StringBuilder("<").Append(e.Name.LocalName);
        foreach (var a in e.Attributes().Where(a => !a.IsNamespaceDeclaration).OrderBy(a => a.Name.LocalName, StringComparer.Ordinal))
            sb.Append(' ').Append(a.Name.LocalName).Append("=\"").Append(a.Value).Append('"');
        sb.Append('>');
        var text = string.Concat(e.Nodes().OfType<XText>().Select(t => t.Value.Trim()));
        // NodeIds and namespace indexes of QualifiedNames compare by namespace URI, numbers by value.
        if (nodeId != null && text.Length > 0 && e.Name.LocalName == "Identifier") text = nodeId(text);
        else if (nodeId != null && text.Length > 0 && e.Name.LocalName == "NamespaceIndex" && e.Parent?.Name.LocalName == "QualifiedName")
            text = nodeId($"ns={text};i=0");
        else text = Number(e.Name.LocalName, text) ?? text;
        sb.Append(text);
        // An empty Description is a null LocalizedText, the same as none.
        foreach (var c in e.Elements().Where(c => !(c.Name.LocalName == "Description" && !c.HasElements && c.Value.Length == 0 && !c.HasAttributes)))
            sb.Append(Canonical(c, nodeId));
        return sb.Append("</>").ToString();
    }

    private sealed class Context
    {
        private static readonly Regex NsPrefix = new(@"^ns=(\d+);(.*)$", RegexOptions.Singleline | RegexOptions.CultureInvariant);
        private static readonly Regex QualifiedPrefix = new(@"^(\d+):(.*)$", RegexOptions.Singleline | RegexOptions.CultureInvariant);

        private readonly List<string> _uris;
        private readonly Dictionary<string, string> _aliases;
        private readonly bool _resolve;

        public Context(List<string> uris, Dictionary<string, string> aliases, bool resolve)
        {
            _uris = uris;
            _aliases = aliases;
            _resolve = resolve;
        }

        /// <summary>A NodeId or alias in canonical form: alias resolved, ns=0 dropped, index replaced by URI.</summary>
        public string NodeId(string raw)
        {
            var id = raw.Trim();
            if (_aliases.TryGetValue(id, out var target)) id = target;
            var m = NsPrefix.Match(id);
            if (!m.Success) return id;
            var index = int.Parse(m.Groups[1].Value);
            if (index == 0) return m.Groups[2].Value;
            return _resolve && index <= _uris.Count ? $"nsu={_uris[index - 1]};{m.Groups[2].Value}" : id;
        }

        public string? NodeIdOrNull(string? raw) => raw == null ? null : NodeId(raw);

        public string? BrowseName(string? raw)
        {
            if (raw == null) return null;
            var m = QualifiedPrefix.Match(raw);
            if (!m.Success || !_resolve) return raw;
            var index = int.Parse(m.Groups[1].Value);
            if (index == 0) return m.Groups[2].Value;
            return index <= _uris.Count ? $"{{{_uris[index - 1]}}}{m.Groups[2].Value}" : raw;
        }
    }
}
