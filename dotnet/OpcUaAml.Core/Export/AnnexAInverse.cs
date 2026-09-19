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
//
// This file holds the public entry points, the index of the document, the
// NodeIds and the order of the export; AnnexAInverse.Types.cs the
// ReferenceTypes, DataTypes and types with what they hold, AnnexAInverse.Values.cs
// the values of variables.

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

public static partial class AnnexAInverse
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
                var root = SafeXml.Load(file).Root!;
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

    private sealed partial class Writer
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
