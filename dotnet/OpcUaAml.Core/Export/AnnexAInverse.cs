// AML to OPC UA for content that came from OPC UA: the inverse of OPC 10000-83
// Annex A. No standard defines this direction; the AML-UA-XSLT rules map any
// AML document into OPC UA and turn every InternalElement into an Object,
// every AttributeType into a VariableType, every InterfaceClass into an
// ObjectType. For the libraries Annex A generated that loses the OPC UA meta
// model (docs/roundtrip.md). This export reads what Annex A wrote and writes
// the nodes back as what they were:
//
//   SUC_<ns> classes          ObjectTypes, VariableTypes (chain to BaseVariableType)
//   their InternalElements    Objects, Variables, Methods (UaMethodNodeClass)
//   ATL_<ns> AttributeTypes   DataTypes with their Definition and EnumStrings,
//                             EnumValues, OptionSetValues (ListOf* are array helpers)
//   ICL_<ns> InterfaceClasses ReferenceTypes with InverseName and Symmetric
//
//   InstanceHierarchy          the namespace's Objects and Variables, found below
//                              the folders Opc2Aml writes (Objects, Server …)
//
// NodeIds come from the elements' IDs and NodeId attributes, so an unchanged
// model comes back with its NodeIds. Elements added in AML without a NodeId
// get the next free numeric one. Below an Object or Variable, Opc2Aml repeats
// the declarations of its type with their NodeIds; a child whose NodeId is a
// declaration of its parent's type (or a supertype) is such a copy and is
// left out.

using System.Globalization;
using System.Xml.Linq;
using OpcUaAml.Addressing;
using OpcUaAml.NodeSets;

namespace OpcUaAml.Export;

public sealed class AnnexAInverseOptions
{
    /// <summary>The namespace to export; its ATL_, ICL_ and SUC_ libraries must be in the document.</summary>
    public required string NamespaceUri { get; init; }

    /// <summary>Model version and publication date; from the libraries when not given.</summary>
    public string? Version { get; init; }
    public DateTime? PublicationDate { get; init; }
}

public static class AnnexAInverse
{
    private static readonly XNamespace Ua = NodeSetExporter.UaNodeSetNamespace;
    private static readonly XNamespace Types = NodeSetExporter.UaTypesNamespace;
    private const string UaUri = "http://opcfoundation.org/UA/";
    private const string MethodClass = "[SUC_OpcAmlMetaModel]/[UaMethodNodeClass]";

    private static readonly Dictionary<string, uint> ModellingRules = new()
    {
        ["Mandatory"] = 78, ["Optional"] = 80, ["ExposesItsArray"] = 83,
        ["OptionalPlaceholder"] = 11508, ["MandatoryPlaceholder"] = 11510,
    };

    private static readonly HashSet<string> BuiltIn = new(StringComparer.Ordinal)
    {
        "Boolean", "SByte", "Byte", "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64", "Float", "Double",
        "String", "DateTime", "Guid", "ByteString", "LocalizedText", "QualifiedName", "Duration", "UtcTime",
        "NodeId", "ExpandedNodeId",
    };

    public static XDocument Export(XDocument caex, AnnexAInverseOptions options) => new Writer(caex.Root!, options).Write();

    /// <summary>
    /// The Default XML encodings of the bundled models' DataTypes (UA base
    /// model, DI), by DataType NodeId. Annex A keeps no encodings; for these
    /// models they are fixed, so values of their structures can be written.
    /// </summary>
    private static readonly Lazy<Dictionary<string, UaNodeAddress>> BundledEncodings = new(() =>
    {
        var result = new Dictionary<string, UaNodeAddress>(StringComparer.Ordinal);
        if (!Directory.Exists(NodeSetCatalog.BundledFolder)) return result;
        foreach (var file in Directory.EnumerateFiles(NodeSetCatalog.BundledFolder, "*.xml"))
        {
            try
            {
                var root = XDocument.Load(file).Root!;
                var uris = new List<string> { UaUri };
                uris.AddRange(root.Element(Ua + "NamespaceUris")?.Elements(Ua + "Uri").Select(u => u.Value.Trim()) ?? Enumerable.Empty<string>());
                string Global(string raw)
                {
                    var id = raw.Trim();
                    if (!id.StartsWith("ns=", StringComparison.Ordinal)) return $"nsu={UaUri};{id}";
                    var semicolon = id.IndexOf(';');
                    return int.TryParse(id[3..semicolon], out var index) && index < uris.Count ? $"nsu={uris[index]};{id[(semicolon + 1)..]}" : id;
                }
                foreach (var encoding in root.Elements(Ua + "UAObject").Where(o => ((string?)o.Attribute("BrowseName"))?.EndsWith("Default XML", StringComparison.Ordinal) == true))
                {
                    var of = encoding.Element(Ua + "References")?.Elements(Ua + "Reference")
                        .FirstOrDefault(r => (string?)r.Attribute("ReferenceType") is "HasEncoding" or "i=38" && (string?)r.Attribute("IsForward") == "false");
                    if (of != null && UaNodeAddress.TryParse(Global((string)encoding.Attribute("NodeId")!), null, out var encodingId))
                        result.TryAdd(Global(of.Value), encodingId);
                }
            }
            catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
            {
                // A broken file only costs the values of its structures.
            }
        }
        return result;
    });

    /// <summary>The NodeIds of all nodes an Annex A document carries as elements or classes.</summary>
    public static IReadOnlySet<string> NodeIdsIn(XElement caexRoot) =>
        caexRoot.DescendantsAndSelf()
            .Where(e => e.Name.LocalName is "InternalElement" or "SystemUnitClass" or "AttributeType" or "InterfaceClass" or "RoleClass")
            .Select(Writer.NodeIdOf).Where(id => id != null).Select(id => id!.ToString())
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The references an Annex A document carries at all, as "node|referenceType"
    /// for every ExternalInterface: a reference type that never appears on
    /// either end of a reference was not carried into AML. NodeIds of the base
    /// model are written without their namespace, as NodeSetComparer writes them.
    /// </summary>
    public static IReadOnlySet<string> CarriedReferences(XElement caexRoot)
    {
        var caex = caexRoot.Name.Namespace;
        var classes = new Dictionary<string, XElement>(StringComparer.Ordinal);
        void Index(XElement c, string path)
        {
            classes[path] = c;
            foreach (var nested in c.Elements(caex + "InterfaceClass")) Index(nested, $"{path}/[{(string?)nested.Attribute("Name")}]");
        }
        foreach (var lib in caexRoot.Elements(caex + "InterfaceClassLib"))
            foreach (var c in lib.Elements(caex + "InterfaceClass"))
                Index(c, $"[{(string?)lib.Attribute("Name")}]/[{(string?)c.Attribute("Name")}]");
        static string Short(string id) => id.StartsWith("nsu=" + UaUri + ";", StringComparison.Ordinal) ? id[(UaUri.Length + 5)..] : id;

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ei in caexRoot.Descendants(caex + "ExternalInterface"))
        {
            if (ei.Parent == null || Writer.NodeIdOf(ei.Parent) is not { } owner) continue;
            var path = (string?)ei.Attribute("RefBaseClassPath") ?? "";
            if (!classes.TryGetValue(path, out var refClass) || Writer.NodeIdOf(refClass) is not { } type) continue;
            result.Add($"{Short(owner.ToString())}|{Short(type.ToString())}");
        }
        return result;
    }

    /// <summary>
    /// The nodes whose Value an Annex A document carries with content: a value,
    /// or fields, items or a locale below it. Opc2Aml writes an empty Value
    /// attribute for no value, an empty one, and a matrix alike.
    /// </summary>
    public static IReadOnlySet<string> NodesWithValues(XElement caexRoot)
    {
        var caex = caexRoot.Name.Namespace;
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in caexRoot.Descendants(caex + "Attribute").Where(a => (string?)a.Attribute("Name") == "Value"))
        {
            if (value.Parent == null || value.Parent.Name.LocalName is "Attribute") continue;
            if (!value.Elements(caex + "Value").Any(v => v.Value.Length > 0) && !value.Elements(caex + "Attribute").Any()) continue;
            if (Writer.NodeIdOf(value.Parent) is { } id)
                result.Add(id.NamespaceUri == UaUri ? id.ToString()[(UaUri.Length + 5)..] : id.ToString());
        }
        return result;
    }

    /// <summary>The nodes that have a Value attribute at all; a VariableType without one has no DataType in AML.</summary>
    public static IReadOnlySet<string> NodesWithValueAttributes(XElement caexRoot) => NodesWithAttribute(caexRoot, "Value");

    /// <summary>
    /// The nodes whose element or class has one of the named attributes directly:
    /// "Description", or the field definitions of a DataType. What Annex A does
    /// not write there cannot come back.
    /// </summary>
    public static IReadOnlySet<string> NodesWithAttribute(XElement caexRoot, params string[] names)
    {
        var caex = caexRoot.Name.Namespace;
        var wanted = new HashSet<string>(names, StringComparer.Ordinal);
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attribute in caexRoot.Descendants(caex + "Attribute").Where(a => wanted.Contains((string?)a.Attribute("Name") ?? "")))
            if (attribute.Parent is { } owner && owner.Name.LocalName != "Attribute" && Writer.NodeIdOf(owner) is { } id)
                result.Add(id.NamespaceUri == UaUri ? id.ToString()[(UaUri.Length + 5)..] : id.ToString());
        return result;
    }

    /// <summary>The namespaces a document holds Annex A libraries of, besides the UA base model's.</summary>
    public static IReadOnlyList<string> Namespaces(XElement caexRoot) =>
        caexRoot.Elements()
            .Where(e => e.Name.LocalName is "SystemUnitClassLib" or "AttributeTypeLib" or "InterfaceClassLib")
            .Select(e => (string?)e.Attribute("Name") ?? "")
            .Where(n => n.Length > 4 && n[3] == '_' && n[4..] != UaUri && Uri.IsWellFormedUriString(n[4..], UriKind.Absolute))
            .Select(n => n[4..]).Distinct().ToList();

    /// <summary>The one namespace to export when none is named.</summary>
    public static string SingleNamespace(XElement caexRoot)
    {
        var namespaces = Namespaces(caexRoot);
        return namespaces.Count switch
        {
            1 => namespaces[0],
            0 => throw new ArgumentException("The document holds no Annex A libraries besides the UA base model's."),
            _ => throw new ArgumentException($"The document holds several namespaces; name one of: {string.Join(", ", namespaces)}."),
        };
    }

    private sealed class Writer
    {
        private readonly XElement _root;
        private readonly XNamespace _caex;
        private readonly AnnexAInverseOptions _options;
        private readonly string _ns;
        private readonly Dictionary<string, XElement> _byPath = new(StringComparer.Ordinal);
        private readonly Dictionary<XElement, string> _libraryOf = new();
        private readonly List<string> _table = new();
        private readonly Dictionary<string, XElement> _nodes = new(StringComparer.Ordinal);
        private readonly List<XElement> _order = new();
        private readonly Dictionary<XElement, UaNodeAddress> _assigned = new();
        private readonly Dictionary<string, UaNodeAddress> _byInterfaceId = new(StringComparer.Ordinal);
        private readonly HashSet<string> _used = new(StringComparer.Ordinal);
        private readonly Dictionary<string, UaNodeAddress> _xmlEncoding = new(StringComparer.Ordinal);
        private uint _next;

        public Writer(XElement root, AnnexAInverseOptions options)
        {
            _root = root;
            _caex = root.Name.Namespace;
            _options = options;
            _ns = options.NamespaceUri;
            _table.Add(_ns);
            foreach (var lib in root.Elements().Where(e => e.Name.LocalName.EndsWith("Lib", StringComparison.Ordinal)))
                Index(lib, Name(lib), lib);
        }

        // ── index of the document's classes by path ────────────────────────

        private void Index(XElement parent, string path, XElement lib)
        {
            foreach (var c in parent.Elements().Where(e => e.Name.LocalName is "SystemUnitClass" or "AttributeType" or "InterfaceClass" or "RoleClass"))
            {
                var p = path + "/" + Name(c);
                _byPath.TryAdd(p, c);
                _libraryOf[c] = Name(lib);
                Index(c, p, lib);
            }
        }

        private static string Name(XElement e) => (string?)e.Attribute("Name") ?? "";

        /// <summary>"[Lib]/[A]/[B]" or "Lib/A/B" → the element.</summary>
        private XElement? Resolve(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var key = path.StartsWith('[')
                ? string.Join("/", path.Trim('[', ']').Split("]/[", StringSplitOptions.None))
                : path;
            return _byPath.GetValueOrDefault(key);
        }

        // ── NodeIds ─────────────────────────────────────────────────────────

        /// <summary>The NodeId Annex A gave an element: its ID ("f;"/"r;" for reference types), else its NodeId attribute.</summary>
        internal static UaNodeAddress? NodeIdOf(XElement e)
        {
            if ((string?)e.Attribute("ID") is { } id)
            {
                var decoded = Uri.UnescapeDataString(id);
                // Reference types carry "f;" (forward) or "r;" (inverse), role classes of Interfaces "rc;".
                foreach (var prefix in new[] { "f;", "r;", "rc;" })
                    if (decoded.StartsWith(prefix, StringComparison.Ordinal)) { decoded = decoded[prefix.Length..]; break; }
                if (decoded.StartsWith("nsu=", StringComparison.Ordinal) && UaNodeAddress.TryParse(decoded, null, out var fromId)) return fromId;
            }
            return NodeIdAttribute(e);
        }

        private static UaNodeAddress? NodeIdAttribute(XElement e)
        {
            var attribute = e.Elements().FirstOrDefault(a => a.Name.LocalName == "Attribute" && Name(a) == "NodeId");
            return attribute == null ? null : ReadNodeId(attribute);
        }

        /// <summary>An Annex A NodeId attribute: RootNodeId with NamespaceUri and one identifier.</summary>
        private static UaNodeAddress? ReadNodeId(XElement attribute)
        {
            var root = Child(attribute, "RootNodeId") ?? attribute;
            var uri = Text(Child(root, "NamespaceUri"));
            if (uri == null) return null;
            foreach (var (name, type) in new[] { ("NumericId", UaIdType.Numeric), ("StringId", UaIdType.String), ("GuidId", UaIdType.Guid), ("OpaqueId", UaIdType.Opaque) })
                if (Text(Child(root, name)) is { Length: > 0 } value) return new UaNodeAddress(uri, type, value);
            return null;
        }

        private readonly Dictionary<XElement, Dictionary<string, XElement>> _declarations = new();
        private readonly List<(UaNodeAddress From, UaNodeAddress Type, UaNodeAddress To, bool Forward)> _listed = new();

        /// <summary>
        /// References Annex A keeps on the source's interface as a list of target
        /// NodeIds ("ReferenceIds"): FromState, HasEffect, HasInterface … An
        /// interface of an inverse class ([FromState]/[…]) lists the other direction.
        /// </summary>
        private void ListedReferences(XElement e, UaNodeAddress id)
        {
            foreach (var ei in e.Elements(_caex + "ExternalInterface"))
            {
                var targets = Attr(ei, "ReferenceIds")?.Elements(_caex + "Attribute").Select(a => (string?)a.Element(_caex + "Value")).ToList();
                if (targets == null || targets.Count == 0) continue;
                if (Resolve((string?)ei.Attribute("RefBaseClassPath")) is not { } cls) continue;
                var inverse = Uri.UnescapeDataString((string?)cls.Attribute("ID") ?? "").StartsWith("r;", StringComparison.Ordinal);
                var refClass = inverse ? cls.Parent : cls;
                if (refClass == null || NodeIdOf(refClass) is not { } refId) continue;
                foreach (var target in targets)
                    // Opc2Aml writes these NodeIds URL-encoded, like its IDs.
                    if (target != null && UaNodeAddress.TryParse(Uri.UnescapeDataString(target), null, out var to) && to != null)
                        _listed.Add((id, refId, to, !inverse));
            }
        }

        /// <summary>
        /// The declarations below a type and its supertypes, at any depth, by
        /// NodeId: the first element that carries it, which is where Opc2Aml
        /// wrote the node itself before repeating it below typed children.
        /// </summary>
        private Dictionary<string, XElement> DeclarationsOf(XElement type)
        {
            if (_declarations.TryGetValue(type, out var known)) return known;
            var result = new Dictionary<string, XElement>(StringComparer.Ordinal);
            _declarations[type] = result;
            var seen = new HashSet<XElement>();
            for (var t = type; t != null && seen.Add(t); t = Resolve((string?)t.Attribute("RefBaseClassPath")))
                foreach (var e in t.Descendants(_caex + "InternalElement"))
                    if (NodeIdOf(e) is { } id) result.TryAdd(id.ToString(), e);
            return result;
        }

        /// <summary>
        /// A child Opc2Aml repeated from the type of the element holding it: its
        /// NodeId is declared in that type by another element. A type may hold a
        /// declaration of its own type (FunctionalGroupType does); the declaration
        /// itself is then no copy.
        /// </summary>
        private bool IsCopy(XElement e, XElement? holderType) =>
            holderType != null && NodeIdOf(e) is { } id && DeclarationsOf(holderType).TryGetValue(id.ToString(), out var declared) && declared != e;

        private UaNodeAddress Assign(XElement e)
        {
            if (_assigned.TryGetValue(e, out var known)) return known;
            var id = NodeIdOf(e);
            if (id == null)
            {
                if (_next == 0) _next = FirstFree();
                id = new UaNodeAddress(_ns, UaIdType.Numeric, (_next++).ToString(CultureInfo.InvariantCulture));
            }
            _assigned[e] = id;
            return id;
        }

        private uint FirstFree()
        {
            uint max = 0;
            foreach (var e in _root.Descendants())
                if (NodeIdOf(e) is { NamespaceUri: var uri, IdType: UaIdType.Numeric, Identifier: var n } && uri == _ns && uint.TryParse(n, out var v))
                    max = Math.Max(max, v);
            return Math.Max(max + 1, 1000);
        }

        private string Text(UaNodeAddress id)
        {
            var index = IndexOf(id.NamespaceUri);
            var prefix = id.IdType switch { UaIdType.Numeric => "i", UaIdType.String => "s", UaIdType.Guid => "g", _ => "b" };
            return (index == 0 ? "" : $"ns={index};") + $"{prefix}={id.Identifier}";
        }

        private int IndexOf(string uri)
        {
            if (uri == UaUri) return 0;
            var i = _table.IndexOf(uri);
            if (i < 0) { _table.Add(uri); i = _table.Count - 1; }
            return i + 1;
        }

        private static UaNodeAddress UaId(uint i) => new(UaUri, UaIdType.Numeric, i.ToString(CultureInfo.InvariantCulture));

        // ── nodes and references ────────────────────────────────────────────

        private XElement Node(string nodeClass, UaNodeAddress id, string browseName, string? browseNamespace = null)
        {
            var key = id.ToString();
            if (_nodes.TryGetValue(key, out var existing)) return existing;
            var index = IndexOf(browseNamespace ?? id.NamespaceUri);
            var node = new XElement(Ua + nodeClass,
                new XAttribute("NodeId", Text(id)),
                new XAttribute("BrowseName", index == 0 ? browseName : $"{index}:{browseName}"),
                new XElement(Ua + "DisplayName", browseName),
                new XElement(Ua + "References"));
            _nodes[key] = node;
            _order.Add(node);
            _used.Add(key);
            return node;
        }

        private void Reference(XElement from, UaNodeAddress type, UaNodeAddress to, bool forward = true)
        {
            var refs = from.Element(Ua + "References")!;
            var target = Text(to);
            var typeText = Text(type);
            if (refs.Elements().Any(r => r.Value == target && (string?)r.Attribute("ReferenceType") == typeText
                                         && ((string?)r.Attribute("IsForward") != "false") == forward)) return;
            refs.Add(new XElement(Ua + "Reference", new XAttribute("ReferenceType", typeText),
                forward ? null : new XAttribute("IsForward", "false"), target));
        }

        // ── the export ──────────────────────────────────────────────────────

        public XDocument Write()
        {
            XElement? Lib(string kind) => _root.Elements(_caex + kind).FirstOrDefault(l => Name(l) == $"{kind switch { "SystemUnitClassLib" => "SUC", "AttributeTypeLib" => "ATL", _ => "ICL" }}_{_ns}");
            var suc = Lib("SystemUnitClassLib");
            var atl = Lib("AttributeTypeLib");
            var icl = Lib("InterfaceClassLib");
            // A model may hold instances only (a dictionary such as IRDI); then there are no libraries.

            foreach (var rt in icl?.Elements(_caex + "InterfaceClass") ?? Enumerable.Empty<XElement>()) ReferenceType(rt);
            foreach (var dt in atl?.Elements(_caex + "AttributeType") ?? Enumerable.Empty<XElement>())
                if (!Name(dt).StartsWith("ListOf", StringComparison.Ordinal)) DataType(dt);
            foreach (var type in suc?.Descendants(_caex + "SystemUnitClass") ?? Enumerable.Empty<XElement>()) Type(type);
            foreach (var ih in _root.Elements(_caex + "InstanceHierarchy"))
                foreach (var e in ih.Elements(_caex + "InternalElement")) Instance(e, null, null, null);
            foreach (var type in suc?.Descendants(_caex + "SystemUnitClass") ?? Enumerable.Empty<XElement>()) Links(type);
            foreach (var ih in _root.Elements(_caex + "InstanceHierarchy")) Links(ih);
            foreach (var (from, type, to, forward) in _listed)
            {
                if (_nodes.TryGetValue(from.ToString(), out var fromNode)) Reference(fromNode, type, to, forward);
                if (_nodes.TryGetValue(to.ToString(), out var toNode)) Reference(toNode, type, from, !forward);
            }

            if (_order.Count == 0)
                throw new ArgumentException($"The document holds no types or instances of '{_ns}'.");
            return Document();
        }

        private XDocument Document()
        {
            var models = new XElement(Ua + "Models");
            var model = new XElement(Ua + "Model", new XAttribute("ModelUri", _ns));
            var (version, date) = LibraryInfo(_ns);
            if ((_options.Version ?? version) is { } v) model.Add(new XAttribute("Version", v));
            if ((_options.PublicationDate ?? date) is { } d) model.Add(new XAttribute("PublicationDate", d.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));
            foreach (var uri in new[] { UaUri }.Concat(_table.Skip(1)))
            {
                var required = new XElement(Ua + "RequiredModel", new XAttribute("ModelUri", uri));
                var (rv, rd) = LibraryInfo(uri);
                if (rv != null) required.Add(new XAttribute("Version", rv));
                if (rd != null) required.Add(new XAttribute("PublicationDate", rd.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));
                model.Add(required);
            }
            models.Add(model);
            foreach (var node in _order)
            {
                var refs = node.Element(Ua + "References")!;
                if (!refs.HasElements) refs.Remove();
            }
            return new XDocument(new XElement(Ua + "UANodeSet",
                new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"),
                new XElement(Ua + "NamespaceUris", _table.Select(u => new XElement(Ua + "Uri", u))),
                models,
                Aliases(),
                _order));
        }

        /// <summary>
        /// Aliases for the base model's ReferenceTypes and DataTypes the file
        /// uses, named as their classes, and written in their place, as
        /// NodeSets usually are.
        /// </summary>
        private XElement? Aliases()
        {
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (path, element) in _byPath)
            {
                if (!(path.StartsWith("ICL_" + UaUri + "/", StringComparison.Ordinal) || path.StartsWith("ATL_" + UaUri + "/", StringComparison.Ordinal))) continue;
                if (NodeIdOf(element) is { NamespaceUri: UaUri } id && !Name(element).StartsWith("ListOf", StringComparison.Ordinal))
                {
                    var text = Text(id);
                    if (!names.ContainsKey(text) && !names.ContainsValue(Name(element))) names[text] = Name(element);
                }
            }
            var used = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var node in _order)
            {
                foreach (var r in node.Element(Ua + "References")?.Elements() ?? Enumerable.Empty<XElement>())
                {
                    var type = (string)r.Attribute("ReferenceType")!;
                    if (names.TryGetValue(type, out var alias)) { used[alias] = type; r.SetAttributeValue("ReferenceType", alias); }
                }
                if ((string?)node.Attribute("DataType") is { } dt && names.TryGetValue(dt, out var dtAlias))
                {
                    used[dtAlias] = dt;
                    node.SetAttributeValue("DataType", dtAlias);
                }
            }
            return used.Count == 0 ? null : new XElement(Ua + "Aliases", used.Select(a => new XElement(Ua + "Alias", new XAttribute("Alias", a.Key), a.Value)));
        }

        /// <summary>Version and publication date Opc2Aml wrote into the libraries of a namespace.</summary>
        private (string? Version, DateTime? Date) LibraryInfo(string uri)
        {
            var lib = _root.Elements().FirstOrDefault(l => Name(l) == "SUC_" + uri) ?? _root.Elements().FirstOrDefault(l => Name(l).EndsWith("_" + uri, StringComparison.Ordinal));
            var info = lib?.Descendants().FirstOrDefault(e => e.Name.LocalName == "OpcUaLibInfo");
            if (info == null) return (null, null);
            string? Value(string local) => info.Elements().FirstOrDefault(e => e.Name.LocalName == local)?.Value;
            var date = DateTime.TryParse(Value("ModelPublicationDate"), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : (DateTime?)null;
            return (Value("ModelVersion"), date);
        }

        // ── ReferenceTypes ──────────────────────────────────────────────────

        private void ReferenceType(XElement rt)
        {
            var id = Assign(rt);
            var node = Node("UAReferenceType", id, Name(rt), BrowseNamespace(rt));
            if (Value(rt, "IsAbstract") == "true" || Value(rt.Elements(_caex + "InterfaceClass").FirstOrDefault(), "IsAbstract") == "true")
                node.Add(new XAttribute("IsAbstract", "true"));
            var connectsTo = Value(rt, "RefClassConnectsToPath");
            var ownPath = $"[{_libraryOf[rt]}]/[{Name(rt)}]";
            if (connectsTo == ownPath) node.Add(new XAttribute("Symmetric", "true"));
            if (Resolve((string?)rt.Attribute("RefBaseClassPath")) is { } super && NodeIdOf(super) is { } superId)
                Reference(node, UaId(45), superId, forward: false);
            if (Value(rt, "InverseName") is { Length: > 0 } inverse) node.Add(new XElement(Ua + "InverseName", inverse));
        }

        // ── DataTypes ───────────────────────────────────────────────────────

        private void DataType(XElement dt)
        {
            var id = Assign(dt);
            var node = Node("UADataType", id, Name(dt), BrowseNamespace(dt));
            if (Value(dt, "IsAbstract") == "true") node.Add(new XAttribute("IsAbstract", "true"));
            var super = Resolve((string?)dt.Attribute("RefAttributeType"));
            var superId = super == null ? null : NodeIdOf(super);
            if (superId != null) Reference(node, UaId(45), superId, forward: false);
            Describe(node, dt);

            var definition = new XElement(Ua + "Definition", new XAttribute("Name", (string)node.Attribute("BrowseName")!));
            var enumFields = Attr(dt, "EnumFieldDefinition");
            var optionFields = Attr(dt, "OptionSetFieldDefinition");
            if (enumFields != null || optionFields != null)
            {
                if (optionFields != null) definition.Add(new XAttribute("IsOptionSet", "true"));
                foreach (var f in (enumFields ?? optionFields)!.Elements(_caex + "Attribute"))
                {
                    var field = new XElement(Ua + "Field", new XAttribute("Name", Name(f)), new XAttribute("Value", Value(f, "Value") ?? "0"));
                    if (Localized(f, "Description") is { Length: > 0 } fd) field.Add(new XElement(Ua + "Description", fd));
                    definition.Add(field);
                }
            }
            else
            {
                if (HasUnionBase(dt)) definition.Add(new XAttribute("IsUnion", "true"));
                foreach (var f in dt.Elements(_caex + "Attribute").Where(a => Attr(a, "StructureFieldDefinition") != null))
                {
                    var def = Attr(f, "StructureFieldDefinition")!;
                    var (fieldType, _) = DataTypeOf(f);
                    var field = new XElement(Ua + "Field", new XAttribute("Name", Name(f)));
                    if (fieldType != null) field.Add(new XAttribute("DataType", Text(fieldType)));
                    if (Value(def, "ValueRank") is { } vr && vr != "-1") field.Add(new XAttribute("ValueRank", vr));
                    if (Dimensions(Attr(def, "ArrayDimensions")) is { Length: > 0 } ad) field.Add(new XAttribute("ArrayDimensions", ad));
                    if (Value(def, "IsOptional") == "true") field.Add(new XAttribute("IsOptional", "true"));
                    if (Value(def, "AllowSubTypes") == "true") field.Add(new XAttribute("AllowSubTypes", "true"));
                    if ((Localized(def, "Description") ?? Localized(f, "Description")) is { Length: > 0 } fd) field.Add(new XElement(Ua + "Description", fd));
                    definition.Add(field);
                }
            }
            node.Add(definition);

            // The properties that name enumeration values and option bits.
            foreach (var name in new[] { "EnumStrings", "EnumValues", "OptionSetValues" })
            {
                if (Attr(dt, name) is not { } list) continue;
                var items = list.Elements(_caex + "Attribute").Where(a => Name(a) != "NodeId").ToList();
                var propId = NodeIdAttribute(list) ?? Assign(list);
                var prop = Node("UAVariable", propId, name, UaUri);
                var isValues = name == "EnumValues";
                prop.Add(new XAttribute("ParentNodeId", Text(id)),
                    new XAttribute("DataType", isValues ? "i=7594" : "i=21"),
                    new XAttribute("ValueRank", "1"),
                    new XAttribute("ArrayDimensions", items.Count.ToString(CultureInfo.InvariantCulture)));
                Reference(prop, UaId(40), UaId(68));
                Reference(prop, UaId(46), id, forward: false);
                Reference(node, UaId(46), propId);
                prop.Add(new XElement(Ua + "Value", isValues
                    ? new XElement(Types + "ListOfExtensionObject", items.Select(i => new XElement(Types + "ExtensionObject",
                        new XElement(Types + "TypeId", new XElement(Types + "Identifier", "i=7616")),
                        new XElement(Types + "Body", new XElement(Types + "EnumValueType",
                            new XElement(Types + "Value", Value(i, "Value") ?? "0"),
                            new XElement(Types + "DisplayName", new XElement(Types + "Text", Value(i, "DisplayName") ?? Name(i))),
                            new XElement(Types + "Description", Value(i, "Description") is { Length: > 0 } d ? new XElement(Types + "Text", d) : null))))))
                    : new XElement(Types + "ListOfLocalizedText", items.Select(i => new XElement(Types + "LocalizedText", new XElement(Types + "Text", (string?)i.Element(_caex + "Value") ?? ""))))));
            }

            // Structures need their encodings to be encodable; Annex A keeps none, so they get new NodeIds.
            if (IsStructure(dt)) Encodings(node, id);
        }

        private bool IsStructure(XElement dt)
        {
            var seen = new HashSet<XElement>();
            for (var t = dt; t != null && seen.Add(t); t = Resolve((string?)t.Attribute("RefAttributeType")))
                if (NodeIdOf(t) is { NamespaceUri: UaUri, Identifier: "22" or "12756" }) return true;
            return false;
        }

        private void Encodings(XElement dataType, UaNodeAddress id)
        {
            foreach (var name in new[] { "Default Binary", "Default XML", "Default JSON" })
            {
                if (_next == 0) _next = FirstFree();
                var encodingId = new UaNodeAddress(_ns, UaIdType.Numeric, (_next++).ToString(CultureInfo.InvariantCulture));
                var encoding = Node("UAObject", encodingId, name, UaUri);
                encoding.Add(new XAttribute("SymbolicName", name.Replace(" ", "")));
                Reference(encoding, UaId(38), id, forward: false);
                Reference(encoding, UaId(40), UaId(76));
                Reference(dataType, UaId(38), encodingId);
                if (name == "Default XML") _xmlEncoding[id.ToString()] = encodingId;
            }
        }

        // ── ObjectTypes, VariableTypes and what they hold ───────────────────

        private void Type(XElement type)
        {
            var id = Assign(type);
            var isVariable = ChainHas(type, "BaseVariableType");
            var node = Node(isVariable ? "UAVariableType" : "UAObjectType", id, Name(type), BrowseNamespace(type));
            if (Value(type, "IsAbstract") == "true") node.Add(new XAttribute("IsAbstract", "true"));
            if (Resolve((string?)type.Attribute("RefBaseClassPath")) is { } super && NodeIdOf(super) is { } superId)
                Reference(node, UaId(45), superId, forward: false);
            Describe(node, type);
            ListedReferences(type, id);
            Interfaces(id, type);
            if (isVariable) VariableAttributes(node, type);
            foreach (var child in type.Elements(_caex + "InternalElement")) Declaration(child, node, id, null);
        }

        /// <summary>
        /// An element of an instance hierarchy: exported when it is a node of the
        /// namespace; otherwise (Objects, Server …) searched for nodes of the namespace.
        /// </summary>
        private void Instance(XElement e, XElement? parentNode, UaNodeAddress? parentId, XElement? parentType)
        {
            if (IsCopy(e, parentType)) return;
            var id = NodeIdOf(e);
            if (id?.NamespaceUri == _ns && parentId != null)
            {
                Declaration(e, parentNode, parentId, parentType);
                return;
            }
            var type = Resolve((string?)e.Attribute("RefBaseSystemUnitPath"));
            foreach (var child in e.Elements(_caex + "InternalElement")) Instance(child, null, id, type);
        }

        private void Declaration(XElement e, XElement? parent, UaNodeAddress parentId, XElement? parentType)
        {
            if (IsCopy(e, parentType)) return;
            var id = Assign(e);
            var typePath = (string?)e.Attribute("RefBaseSystemUnitPath");
            var isMethod = typePath == MethodClass;
            var type = isMethod ? null : Resolve(typePath);
            var nodeClass = isMethod ? "UAMethod" : type != null && ChainHas(type, "BaseVariableType") ? "UAVariable" : "UAObject";
            var (reference, rule) = HeldBy(e);

            // A node held by two parents: Opc2Aml writes it below each, the NodeSet once,
            // with a reference from each parent.
            if (_nodes.TryGetValue(id.ToString(), out var written))
            {
                if (parent != null) Reference(parent, reference, id);
                Reference(written, reference, parentId, forward: false);
                return;
            }
            var node = Node(nodeClass, id, Name(e), BrowseNamespace(e));

            // ParentNodeId names the node that holds this one: one of the file, or a node of the base
            // model (Objects, Server) an instance hangs below. A folder organizes, it holds nothing.
            var organizes = reference.NamespaceUri == UaUri && reference.Identifier == "35";
            if ((parent != null || parentId.NamespaceUri == UaUri) && !organizes) node.Add(new XAttribute("ParentNodeId", Text(parentId)));
            if (isMethod && MethodDeclaration(Name(e), parentType) is { } declaration && declaration.ToString() != id.ToString())
                node.Add(new XAttribute("MethodDeclarationId", Text(declaration)));
            ListedReferences(e, id);
            Interfaces(id, e);
            if (parent != null) Reference(parent, reference, id);
            Reference(node, reference, parentId, forward: false);
            if (type != null && NodeIdOf(type) is { } typeId) Reference(node, UaId(40), typeId);
            if (rule != null && ModellingRules.TryGetValue(rule, out var ruleId)) Reference(node, UaId(37), UaId(ruleId));
            Describe(node, e);
            if (nodeClass == "UAVariable") VariableAttributes(node, e);

            foreach (var child in e.Elements(_caex + "InternalElement")) Declaration(child, node, id, type);
        }

        /// <summary>OPC UA Interfaces become role classes in Annex A: HasInterface from SupportedRoleClass and RoleRequirements.</summary>
        private void Interfaces(UaNodeAddress id, XElement e)
        {
            var paths = e.Elements(_caex + "SupportedRoleClass").Select(r => (string?)r.Attribute("RefRoleClassPath"))
                .Concat(e.Elements(_caex + "RoleRequirements").Select(r => (string?)r.Attribute("RefBaseRoleClassPath")));
            foreach (var path in paths)
            {
                if (path == null || path.StartsWith("RCL_OpcAmlMetaModel", StringComparison.Ordinal)) continue;
                if (Resolve(path) is { } role && NodeIdOf(role) is { } interfaceId) _listed.Add((id, UaId(17603), interfaceId, true));
            }
        }

        /// <summary>The method a method of an instance or declaration instantiates: the one of the same name in the holder's type.</summary>
        private UaNodeAddress? MethodDeclaration(string name, XElement? holderType)
        {
            var seen = new HashSet<XElement>();
            for (var t = holderType; t != null && seen.Add(t); t = Resolve((string?)t.Attribute("RefBaseClassPath")))
            {
                var method = t.Elements(_caex + "InternalElement")
                    .FirstOrDefault(m => Name(m) == name && (string?)m.Attribute("RefBaseSystemUnitPath") == MethodClass);
                if (method != null && NodeIdOf(method) is { } id) return id;
            }
            return null;
        }

        /// <summary>
        /// The reference that holds an element, from the interface on its end
        /// ([ICL]/[HasComponent]/[ComponentOf] → HasComponent), and its ModellingRule.
        /// </summary>
        private (UaNodeAddress Type, string? Rule) HeldBy(XElement e)
        {
            foreach (var ei in e.Elements(_caex + "ExternalInterface"))
            {
                var path = (string?)ei.Attribute("RefBaseClassPath");
                if (path == null) continue;
                var segments = path.Trim('[', ']').Split("]/[", StringSplitOptions.None);
                if (segments.Length < 3 || !segments[0].StartsWith("ICL_", StringComparison.Ordinal)) continue;
                var forward = Resolve($"[{segments[0]}]/[{string.Join("]/[", segments[1..^1])}]");
                if (forward == null || NodeIdOf(forward) is not { } refId) continue;
                return (refId, Value(ei, "ModellingRule"));
            }
            return (UaId(47), null);
        }

        /// <summary>Non-hierarchical references between declarations, from the InternalLinks of a type.</summary>
        private void Links(XElement type)
        {
            foreach (var link in type.Descendants(_caex + "InternalLink"))
            {
                var a = Interface((string?)link.Attribute("RefPartnerSideA"));
                var b = Interface((string?)link.Attribute("RefPartnerSideB"));
                if (a == null || b == null) continue;
                // The forward end names the reference type directly; the other end is its inverse.
                var (source, target) = IsInverseEnd(a.Value.Interface) ? (b.Value, a.Value) : (a.Value, b.Value);
                var refPath = (string?)source.Interface.Attribute("RefBaseClassPath");
                if (Resolve(refPath) is not { } refClass || NodeIdOf(refClass) is not { } refId) continue;
                if (IsHierarchicalClass(refClass)) continue;
                if (!_assigned.TryGetValue(source.Owner, out var from) || !_assigned.TryGetValue(target.Owner, out var to)) continue;
                if (!_nodes.TryGetValue(from.ToString(), out var fromNode) || !_nodes.TryGetValue(to.ToString(), out var toNode)) continue;
                Reference(fromNode, refId, to);
                Reference(toNode, refId, from, forward: false);
            }
        }

        private Dictionary<string, XElement>? _interfaces;

        /// <summary>An ExternalInterface by ID, and its owner; the index is built once (a document may hold tens of thousands of links).</summary>
        private (XElement Interface, XElement Owner)? Interface(string? id)
        {
            if (id == null) return null;
            _interfaces ??= _root.Descendants(_caex + "ExternalInterface")
                .Where(x => x.Attribute("ID") != null)
                .GroupBy(x => (string)x.Attribute("ID")!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            return _interfaces.TryGetValue(id, out var ei) && ei.Parent != null ? (ei, ei.Parent) : null;
        }

        private bool IsInverseEnd(XElement ei)
        {
            var path = (string?)ei.Attribute("RefBaseClassPath") ?? "";
            return Resolve(path) is { } c && Uri.UnescapeDataString((string?)c.Attribute("ID") ?? "").StartsWith("r;", StringComparison.Ordinal);
        }

        private bool IsHierarchicalClass(XElement refClass)
        {
            var seen = new HashSet<XElement>();
            for (var c = refClass; c != null && seen.Add(c); c = Resolve((string?)c.Attribute("RefBaseClassPath")))
                if (NodeIdOf(c) is { NamespaceUri: UaUri, Identifier: "33" }) return true;
            return false;
        }

        private bool ChainHas(XElement type, string name)
        {
            var seen = new HashSet<XElement>();
            for (var t = type; t != null && seen.Add(t); t = Resolve((string?)t.Attribute("RefBaseClassPath")))
                if (Name(t) == name && _libraryOf.GetValueOrDefault(t) == "SUC_" + UaUri) return true;
            return false;
        }

        // ── attributes of variables ─────────────────────────────────────────

        private void VariableAttributes(XElement node, XElement e)
        {
            var value = Attr(e, "Value");
            var (dataType, list) = value == null ? (null, false) : DataTypeOf(value);
            if (dataType != null && !(dataType.NamespaceUri == UaUri && dataType.Identifier == "24"))
                node.Add(new XAttribute("DataType", Text(dataType)));
            var valueRank = Value(e, "ValueRank");
            if (valueRank != null && valueRank != "-1") node.Add(new XAttribute("ValueRank", valueRank));
            if (Attr(e, "ArrayDimensions") is { } dims && dims.Elements(_caex + "Attribute").Any())
                node.Add(new XAttribute("ArrayDimensions", string.Join(",", dims.Elements(_caex + "Attribute").Select(d => (string?)d.Element(_caex + "Value") ?? "0"))));
            if (value != null && ValueXml(value) is { } xml) node.Add(new XElement(Ua + "Value", xml));
        }

        /// <summary>The DataType of an attribute: its RefAttributeType; for a "ListOf" helper type, the element type.</summary>
        private (UaNodeAddress? Type, bool List) DataTypeOf(XElement attribute)
        {
            var path = (string?)attribute.Attribute("RefAttributeType");
            var type = Resolve(path);
            if (type != null && NodeIdOf(type) is { } id) return (id, false);
            if (type != null && Name(type).StartsWith("ListOf", StringComparison.Ordinal))
            {
                var elementPath = $"[{_libraryOf[type]}]/[{Name(type)[6..]}]";
                if (Resolve(elementPath) is { } element && NodeIdOf(element) is { } elementId) return (elementId, true);
            }
            return (null, false);
        }

        /// <summary>
        /// The Value element's content for the values Annex A writes plainly:
        /// built-in types and types derived from them, enumerations (Annex A
        /// writes the name, OPC UA the number), lists of these, and arguments.
        /// </summary>
        private XElement? ValueXml(XElement value)
        {
            var path = (string?)value.Attribute("RefAttributeType") ?? "";
            var typeName = path.Split('/').LastOrDefault()?.Trim('[', ']') ?? "";
            var isList = typeName.StartsWith("ListOf", StringComparison.Ordinal);
            var element = isList ? typeName[6..] : typeName;
            var items = isList ? value.Elements(_caex + "Attribute").ToList() : new List<XElement> { value };
            if (element == "Argument")
            {
                if (!isList || items.Count == 0) return null;
                return new XElement(Types + "ListOfExtensionObject", items.Select(Argument));
            }
            var elementType = Resolve(isList ? path.Replace("[" + typeName + "]", "[" + element + "]") : path);
            var enumValues = elementType == null ? null : EnumValues(elementType);
            var builtIn = enumValues != null ? "Int32" : BuiltIn.Contains(element) ? element : elementType == null ? null : BuiltInOf(elementType);
            if (elementType != null && builtIn != null && OptionBits(elementType) is { } bits)
            {
                var numbers = items.Select(i => new XElement(Types + Base(builtIn), OptionSetNumber(i, bits))).ToList();
                return numbers.Count == 0 ? null : isList ? new XElement(Types + $"ListOf{Base(builtIn)}", numbers) : numbers[0];
            }
            if (builtIn == null)
            {
                if (elementType == null || !IsStructure(elementType)) return null;
                var objects = items.Select(i => StructureObject(i, elementType)).ToList();
                if (objects.Count == 0 || objects.Any(o => o == null)) return null;
                return isList ? new XElement(Types + "ListOfExtensionObject", objects) : objects[0];
            }
            var encoded = items.Select(i => enumValues != null ? EnumScalar(i, enumValues) : Scalar(builtIn, i)).ToList();
            if (encoded.Any(x => x == null) || encoded.Count == 0) return null;
            return isList ? new XElement(Types + $"ListOf{Base(builtIn)}", encoded) : encoded[0];
        }

        /// <summary>The built-in type a DataType of the base model derives from (NumericRange → String).</summary>
        private string? BuiltInOf(XElement type)
        {
            var seen = new HashSet<XElement>();
            for (var t = type; t != null && seen.Add(t); t = Resolve((string?)t.Attribute("RefAttributeType")))
                if (_libraryOf.GetValueOrDefault(t) == "ATL_" + UaUri && BuiltIn.Contains(Name(t))) return Name(t);
            return null;
        }

        /// <summary>An enumeration's values by name, from its EnumFieldDefinition or EnumStrings; null for other types.</summary>
        private Dictionary<string, string>? EnumValues(XElement type)
        {
            var seen = new HashSet<XElement>();
            for (var t = type; t != null && seen.Add(t); t = Resolve((string?)t.Attribute("RefAttributeType")))
            {
                if (Attr(t, "EnumFieldDefinition") is { } fields)
                    return fields.Elements(_caex + "Attribute").ToDictionary(f => Name(f), f => Value(f, "Value") ?? "0", StringComparer.Ordinal);
                if (Attr(t, "EnumStrings") is { } strings)
                    return strings.Elements(_caex + "Attribute").Where(a => Name(a) != "NodeId")
                        .Select((a, i) => ((string?)a.Element(_caex + "Value") ?? "", i.ToString(CultureInfo.InvariantCulture)))
                        .GroupBy(x => x.Item1).ToDictionary(g => g.Key, g => g.First().Item2, StringComparer.Ordinal);
            }
            return null;
        }

        /// <summary>
        /// A structure value as an ExtensionObject in the XML encoding. Annex A
        /// writes the fields as nested attributes; the TypeId is the encoding this
        /// export gave the DataType, or the fixed one of a bundled model (UA, DI);
        /// structures of other models are left out. Structures with optional fields or unions need an encoding mask
        /// Annex A does not keep and are left out.
        /// </summary>
        private XElement? StructureObject(XElement item, XElement type)
        {
            if (NodeIdOf(type) is not { } typeId) return null;
            if (!_xmlEncoding.TryGetValue(typeId.ToString(), out var encoding) && !BundledEncodings.Value.TryGetValue(typeId.ToString(), out encoding)) return null;
            if (!item.Elements(_caex + "Attribute").Any()) return null;
            if (StructureBody(item, type, Name(type)) is not { } body) return null;
            return new XElement(Types + "ExtensionObject",
                new XElement(Types + "TypeId", new XElement(Types + "Identifier", Text(encoding))),
                new XElement(Types + "Body", body));
        }

        private XElement? StructureBody(XElement item, XElement type, string elementName)
        {
            if (HasMask(type)) return null;
            var uri = NodeIdOf(type)?.NamespaceUri ?? _ns;
            var ns = uri == UaUri ? Types : XNamespace.Get(uri + "Types.xsd");
            var body = new XElement(ns + XmlName(elementName));
            foreach (var field in item.Elements(_caex + "Attribute"))
            {
                var path = (string?)field.Attribute("RefAttributeType") ?? "";
                var typeName = path.Split('/').LastOrDefault()?.Trim('[', ']') ?? "";
                var isList = typeName.StartsWith("ListOf", StringComparison.Ordinal);
                var element = isList ? typeName[6..] : typeName;
                var fieldType = Resolve(isList ? path.Replace("[" + typeName + "]", "[" + element + "]") : path);
                if (field.Elements(_caex + "Attribute").Any() && AllowsSubTypes(type, Name(field))) return null;
                if (isList)
                {
                    var list = new XElement(ns + XmlName(Name(field)));
                    foreach (var i in field.Elements(_caex + "Attribute"))
                    {
                        if (FieldValue(i, element, fieldType, ns + XmlName(element)) is not { } encoded) return null;
                        list.Add(encoded);
                    }
                    body.Add(list);
                }
                else if (FieldValue(field, element, fieldType, ns + XmlName(Name(field))) is { } encoded) body.Add(encoded);
                else return null;
            }
            return body;
        }

        /// <summary>One field or list item: built-in, enumeration ("Name_Value"), or a nested structure.</summary>
        private XElement? FieldValue(XElement a, string typeName, XElement? type, XName name)
        {
            var text = (string?)a.Element(_caex + "Value");
            if (type != null && EnumValues(type) is { } values)
                return new XElement(name, text == null ? "" : EnumText(text, values));
            var builtIn = BuiltIn.Contains(typeName) ? typeName : type == null ? null : BuiltInOf(type);
            if (type != null && builtIn != null && OptionBits(type) is { } bits)
                return new XElement(name, OptionSetNumber(a, bits));
            if (builtIn != null)
            {
                // Annex A leaves empty strings out; an empty element says the same.
                if (Scalar(builtIn, a) is not { } scalar) return new XElement(name);
                scalar.Name = name;
                return scalar;
            }
            if (type == null || !IsStructure(type)) return null;
            return StructureBody(a, type, name.LocalName) is { } body ? new XElement(name, body.Elements()) : null;
        }

        /// <summary>The bit of each option of an option set DataType; null for other types.</summary>
        private Dictionary<string, int>? OptionBits(XElement type)
        {
            var seen = new HashSet<XElement>();
            for (var t = type; t != null && seen.Add(t); t = Resolve((string?)t.Attribute("RefAttributeType")))
                if (Attr(t, "OptionSetFieldDefinition") is { } fields)
                    return fields.Elements(_caex + "Attribute").GroupBy(Name)
                        .ToDictionary(g => g.Key, g => int.TryParse(Value(g.First(), "Value"), out var bit) ? bit : 0, StringComparer.Ordinal);
            return null;
        }

        /// <summary>Annex A writes an option set value as one boolean per option; unset options carry no value.</summary>
        private string OptionSetNumber(XElement a, Dictionary<string, int> bits)
        {
            if ((string?)a.Element(_caex + "Value") is { Length: > 0 } plain && ulong.TryParse(plain, out _)) return plain;
            ulong number = 0;
            foreach (var option in a.Elements(_caex + "Attribute"))
                if ((string?)option.Element(_caex + "Value") == "true" && bits.TryGetValue(Name(option), out var bit) && bit is >= 0 and < 64)
                    number |= 1UL << bit;
            return number.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// A type or field name as an XML element name. The XML encoding spells the
        /// base model's 3D types out (ThreeDFrame); other invalid names are escaped.
        /// </summary>
        private static string XmlName(string name)
        {
            if (name.StartsWith("3D", StringComparison.Ordinal)) name = "ThreeD" + name[2..];
            return System.Xml.XmlConvert.EncodeLocalName(name);
        }

        private static string EnumText(string text, Dictionary<string, string> values)
        {
            if (values.TryGetValue(text, out var number)) return $"{text}_{number}";
            if (int.TryParse(text, out _) && values.FirstOrDefault(v => v.Value == text).Key is { } byNumber) return $"{byNumber}_{text}";
            return text;
        }

        /// <summary>A union or a structure with optional fields: both need an encoding mask.</summary>
        private bool HasMask(XElement type)
        {
            if (HasUnionBase(type)) return true;
            var seen = new HashSet<XElement>();
            for (var t = type; t != null && seen.Add(t); t = Resolve((string?)t.Attribute("RefAttributeType")))
                if (t.Elements(_caex + "Attribute").Any(f => Value(Attr(f, "StructureFieldDefinition"), "IsOptional") == "true")) return true;
            return false;
        }

        /// <summary>
        /// A field that allows subtypes holds ExtensionObjects whose type Annex A
        /// does not keep; it can only be written while it is empty.
        /// </summary>
        private bool AllowsSubTypes(XElement type, string field)
        {
            var seen = new HashSet<XElement>();
            for (var t = type; t != null && seen.Add(t); t = Resolve((string?)t.Attribute("RefAttributeType")))
                if (t.Elements(_caex + "Attribute").FirstOrDefault(f => Name(f) == field) is { } definition)
                    return Value(Attr(definition, "StructureFieldDefinition"), "AllowSubTypes") == "true";
            return false;
        }

        private bool HasUnionBase(XElement type)
        {
            var seen = new HashSet<XElement>();
            for (var t = type; t != null && seen.Add(t); t = Resolve((string?)t.Attribute("RefAttributeType")))
                if (NodeIdOf(t) is { NamespaceUri: UaUri, Identifier: "12756" }) return true;
            return false;
        }

        /// <summary>An ArrayDimensions list attribute as the comma separated NodeSet form.</summary>
        private string? Dimensions(XElement? list)
        {
            if (list == null) return null;
            var items = list.Elements(_caex + "Attribute").Select(d => (string?)d.Element(_caex + "Value") ?? "0").ToList();
            return items.Count > 0 ? string.Join(",", items) : (string?)list.Element(_caex + "Value");
        }

        private XElement? EnumScalar(XElement attribute, Dictionary<string, string> values)
        {
            var text = (string?)attribute.Element(_caex + "Value");
            if (text == null) return null;
            // A name, or "Name_Value" as the XML encoding writes enumerations.
            if (values.TryGetValue(text, out var number)) return new XElement(Types + "Int32", number);
            var underscore = text.LastIndexOf('_');
            if (underscore > 0 && int.TryParse(text[(underscore + 1)..], out _)) return new XElement(Types + "Int32", text[(underscore + 1)..]);
            return int.TryParse(text, out _) ? new XElement(Types + "Int32", text) : null;
        }

        private static string Base(string element) => element switch { "Duration" => "Double", "UtcTime" => "DateTime", _ => element };

        private XElement? Scalar(string type, XElement attribute)
        {
            var text = (string?)attribute.Element(_caex + "Value");
            var name = Types + Base(type);
            switch (type)
            {
                case "NodeId" or "ExpandedNodeId":
                    // Annex A writes a NodeId value as a NodeId attribute (RootNodeId, NamespaceUri, identifier).
                    return ReadNodeId(attribute) is { } nodeId ? new XElement(name, new XElement(Types + "Identifier", Text(nodeId))) : null;
                case "LocalizedText":
                {
                    // Annex A writes the locale as a LocalizedAttribute named after it.
                    var locale = attribute.Elements(_caex + "Attribute")
                        .FirstOrDefault(a => ((string?)a.Attribute("RefAttributeType"))?.EndsWith("LocalizedAttribute", StringComparison.Ordinal) == true);
                    if (text == null) return null;
                    return new XElement(name,
                        locale == null ? null : new XElement(Types + "Locale", Name(locale)),
                        new XElement(Types + "Text", text));
                }
                case "QualifiedName":
                    var uri = Value(attribute, "NamespaceUri");
                    var qn = Value(attribute, "Name");
                    return qn == null ? null : new XElement(name, new XElement(Types + "NamespaceIndex", uri == null ? 0 : IndexOf(uri)), new XElement(Types + "Name", qn));
                case "Guid":
                    return text == null ? null : new XElement(name, new XElement(Types + "String", text));
                case "DateTime" or "UtcTime":
                    // OPC UA times are UTC. Opc2Aml writes them with the local offset but
                    // the UTC clock time (00:00:00+01:00 for 00:00:00Z); the clock time counts.
                    if (text == null) return null;
                    return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto)
                        ? new XElement(name, dto.DateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + "Z")
                        : new XElement(name, text);
                default:
                    return text == null ? null : new XElement(name, text);
            }
        }

        private XElement Argument(XElement a)
        {
            var dataType = Attr(a, "DataType") is { } dt ? ReadNodeId(dt) : null;
            var dims = Attr(a, "ArrayDimensions")?.Elements(_caex + "Attribute").Select(d => (string?)d.Element(_caex + "Value") ?? "0").ToList() ?? new List<string>();
            var description = Localized(a, "Description");
            return new XElement(Types + "ExtensionObject",
                new XElement(Types + "TypeId", new XElement(Types + "Identifier", "i=297")),
                new XElement(Types + "Body", new XElement(Types + "Argument",
                    new XElement(Types + "Name", Value(a, "Name") ?? ""),
                    new XElement(Types + "DataType", new XElement(Types + "Identifier", dataType == null ? "i=24" : Text(dataType))),
                    new XElement(Types + "ValueRank", Value(a, "ValueRank") ?? "-1"),
                    new XElement(Types + "ArrayDimensions", dims.Select(d => new XElement(Types + "UInt32", d))),
                    string.IsNullOrEmpty(description) ? null : new XElement(Types + "Description", new XElement(Types + "Text", description)))));
        }

        // ── small helpers ───────────────────────────────────────────────────

        /// <summary>The BrowseName's namespace, from the BrowseName attribute Annex A writes when it is not the node's.</summary>
        private string? BrowseNamespace(XElement e) => Text(Child(Attr(e, "BrowseName"), "NamespaceUri"));

        private void Describe(XElement node, XElement e)
        {
            // Annex A writes a DisplayName only when it is not the BrowseName.
            if (Localized(e, "DisplayName") is { Length: > 0 } display)
                node.Element(Ua + "DisplayName")!.Value = display;
            if (Localized(e, "Description") is { Length: > 0 } text)
                node.Element(Ua + "DisplayName")!.AddAfterSelf(new XElement(Ua + "Description", text));
        }

        private XElement? Attr(XElement? e, string name) =>
            e?.Elements(_caex + "Attribute").FirstOrDefault(a => Name(a) == name);

        private string? Value(XElement? e, string name) => (string?)Attr(e, name)?.Element(_caex + "Value");

        /// <summary>A LocalizedText as Annex A writes it: the value itself, or the first entry of a list of them.</summary>
        private string? Localized(XElement? e, string name)
        {
            var a = Attr(e, name);
            return (string?)a?.Element(_caex + "Value") ?? (string?)a?.Elements(_caex + "Attribute").FirstOrDefault()?.Element(_caex + "Value");
        }

        private static XElement? Child(XElement? e, string name) =>
            e?.Elements().FirstOrDefault(a => a.Name.LocalName == "Attribute" && Name(a) == name);

        private static string? Text(XElement? attribute) =>
            attribute?.Elements().FirstOrDefault(v => v.Name.LocalName == "Value")?.Value;
    }
}
