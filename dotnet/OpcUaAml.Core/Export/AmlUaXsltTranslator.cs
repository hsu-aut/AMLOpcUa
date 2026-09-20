// The port of AML2Nodeset.xslt and its includes (LibraryParsing,
// LibraryTranslation, DatatypeTranslation), AML-UA-XSLT commit a144dcc.
//
// The structure follows the stylesheets so that a change upstream can be
// carried over: one method per template, named after it, the XSLT's
// variable names kept. Apply() is xsl:apply-templates with the stylesheet's
// match patterns; elements without a template are walked through, text
// produces nothing, as with the built-in rules.
//
// This file: document level (namespaces, aliases, models, CAEXFile and the
// collections). Instances.cs: attributes, InternalElements, ExternalInterfaces
// and the References template. Classes.cs: libraries, classes and the class
// lookup of LibraryParsing.
//
// Where the default rules deviate from the XSLT, the code tests _compat; each
// such place names its deviation (D1 to D12, see docs/export.md).

using System.Xml.Linq;

namespace OpcUaAml.Export;

internal sealed partial class AmlUaXsltTranslator
{
    private const string AmlUri = "http://opcfoundation.org/UA/AML/";
    private static readonly XNamespace Ua = NodeSetExporter.UaNodeSetNamespace;
    private static readonly XNamespace Uax = NodeSetExporter.UaTypesNamespace;

    private static readonly string[] LibraryKinds = { "InterfaceClassLib", "RoleClassLib", "SystemUnitClassLib", "AttributeTypeLib" };

    private readonly XElement _root;
    private readonly NodeSetExportOptions _options;
    private readonly bool _compat;
    private readonly string _publicationDate;

    /// <summary>Every attribute of the document in document order (XPath //@*).</summary>
    private readonly List<XAttribute> _allAttributes;

    /// <summary>
    /// The suffix of the NodeId of a unit property. A sub-attribute may be
    /// called "Unit" as well, and the two must not be the same node.
    /// </summary>
    internal const string UnitSuffix = "_Unit#";

    /// <summary>What the export could not do, in the words of the document.</summary>
    private readonly List<string> _notes = [];

    /// <summary>One node per library, even where two libraries share a kind, a name and a namespace.</summary>
    private readonly Dictionary<(string Namespace, string Kind), List<XElement>> _libraryIds = new();

    /// <summary>The NodeId part of a library folder, unique within its namespace.</summary>
    private string LibraryId(XElement lib, string nsId)
    {
        var kind = RemoveSpace(L(lib));
        if (_compat) return kind;
        var key = (nsId, kind);
        if (!_libraryIds.TryGetValue(key, out var seen)) _libraryIds[key] = seen = [];
        var index = seen.FindIndex(e => ReferenceEquals(e, lib));
        if (index < 0) { seen.Add(lib); index = seen.Count - 1; }
        return index == 0 ? kind : $"{kind}_{index + 1}";
    }

    private readonly List<ImportedLibrary> _importedLibraries;
    private readonly List<string> _namespaceUris;
    private readonly List<InternalLinkSides> _internalLinks;

    private readonly List<XNode> _output = new();
    private readonly SortedDictionary<string, string> _extraAliases = new(StringComparer.Ordinal);

    public AmlUaXsltTranslator(XElement root, NodeSetExportOptions options)
    {
        _root = root;
        _options = options;
        _compat = options.XsltCompatibility;
        _publicationDate = (options.PublicationDate ?? DateTime.UtcNow).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        _allAttributes = root.DescendantsAndSelf().SelectMany(Attributes).ToList();
        _importedLibraries = ImportedAmlLibraries();
        _namespaceUris = NamespaceUris();
        _internalLinks = root.Descendants().Where(e => L(e) == "InternalLink").Select(InternalLinkSides.Of).ToList();
    }

    public XDocument Translate()
    {
        CaexFile(_root);
        var nodeSet = new XElement(Ua + "UANodeSet",
            new XAttribute(XNamespace.Xmlns + "uax", Uax.NamespaceName),
            new XElement(Ua + "NamespaceUris", _namespaceUris.Select(u => new XElement(Ua + "Uri", u))),
            Models(),
            Aliases(),
            _output);
        return new XDocument(new XDeclaration("1.0", "utf-8", null), nodeSet);
    }

    // ------------------------------------------------------------------
    // Imported libraries, namespaces, models, aliases
    // ------------------------------------------------------------------

    private sealed record ImportedLibrary(string Name, string Source, IReadOnlyList<(string Alias, string Target)> Aliases);

    /// <summary>
    /// Variable ImportedAMLLibraries: the AML base types, one library per
    /// library name used through an ExternalReference alias, and fallbacks for
    /// AutomationMLBaseRole and AutomationMLBaseInterface when the document
    /// never mentions them.
    /// </summary>
    private List<ImportedLibrary> ImportedAmlLibraries()
    {
        var libs = new List<ImportedLibrary>
        {
            new(AmlUri, "AML Base", AmlBaseAliases.Select(a => (a.Alias, a.Target)).ToList()),
        };

        foreach (var extRef in _root.Descendants().Where(e => L(e) == "ExternalReference"))
        {
            var refAlias = Attr(extRef, "Alias") + "@";
            var source = Attr(extRef, "Path");
            var used = _allAttributes.Where(a => a.Value.StartsWith(refAlias, StringComparison.Ordinal)).Select(a => a.Value).ToList();
            foreach (var libName in used.Select(v => After(Before(v, "/"), "@")).Distinct())
            {
                // Each library gets the classes that belong to it. The XSLT
                // gives every library the whole list, so one alias name is
                // declared once per library, each time for another namespace,
                // and half the references land in the wrong one.
                var mine = _compat
                    ? used.Distinct().ToList()
                    : used.Distinct().Where(v => After(Before(v, "/"), "@") == libName).ToList();
                libs.Add(new ImportedLibrary(libName, source, mine.Select(c => (After(c, "@"), "s=" + LastSegment(c))).ToList()));
            }
        }

        if (!_allAttributes.Any(a => a.Value.Contains("AutomationMLBaseRole")))
            libs.Add(new ImportedLibrary("AutomationMLBaseRoleClassLib", "Fallback: AutomationMLBaseRole",
                new[] { ("AutomationMLBaseRoleClassLib/AutomationMLBaseRole", "s=AutomationMLBaseRole") }));
        if (!_allAttributes.Any(a => a.Value.Contains("AutomationMLBaseInterface")))
            libs.Add(new ImportedLibrary("AutomationMLInterfaceClassLib", "Fallback: AutomationMLBaseInterface",
                new[] { ("AutomationMLInterfaceClassLib/AutomationMLBaseInterface", "s=AutomationMLBaseInterface") }));
        return libs;
    }

    private static readonly (string Alias, string Target)[] AmlBaseAliases =
    {
        ("CAEXObjectType", "i=1001"), ("CAEXFileType", "i=1005"),
        ("CAEXConstraintType", "i=2000"), ("CAEXNominalScaledConstraintType", "i=2001"),
        ("CAEXOrdinalScaledConstraintType", "i=2002"), ("CAEXUnknownConstraintType", "i=2003"),
        ("CAEXRequiredValue", "i=2004"), ("CAEXRequiredMinValue", "i=2005"),
        ("CAEXRequiredMaxValue", "i=2006"), ("CAEXRequirements", "i=2007"),
        ("AMLBaseVariableType", "i=3001"),
        ("HasAMLRoleReference", "i=4001"), ("HasAMLInternalLink", "i=4002"), ("HasAMLReferenceType", "i=4003"),
        ("IsAMLMirroredAs", "i=4004"), ("HasAMLConstraint", "i=4005"), ("HasAMLRequirement", "i=4006"),
        ("CAEXFile_AutomationMLInstanceHierarchies", "i=5005"), ("CAEXFile_AutomationMLFiles", "i=5006"),
        ("CAEXFile_InterfaceClassLibs", "i=5008"), ("CAEXFile_RoleClassLibs", "i=5009"),
        ("CAEXFile_SystemUnitClassLibs", "i=5010"), ("CAEXFile_AttributeTypeLibs", "i=5011"),
    };

    private static readonly (string Alias, string Target)[] BaseAliases =
    {
        ("Boolean", "i=1"), ("SByte", "i=2"), ("Byte", "i=3"), ("Int16", "i=4"), ("UInt16", "i=5"),
        ("Int32", "i=6"), ("UInt32", "i=7"), ("Int64", "i=8"), ("UInt64", "i=9"), ("Float", "i=10"),
        ("Double", "i=11"), ("DateTime", "i=13"), ("String", "i=12"), ("ByteString", "i=15"), ("Guid", "i=14"),
        ("XmlElement", "i=16"), ("NodeId", "i=17"), ("ExpandedNodeId", "i=18"), ("QualifiedName", "i=20"),
        ("LocalizedText", "i=21"), ("StatusCode", "i=19"), ("Structure", "i=22"), ("Number", "i=26"),
        ("Integer", "i=27"), ("UInteger", "i=28"),
        ("HasComponent", "i=47"), ("HasProperty", "i=46"), ("Organizes", "i=35"), ("HasEventSource", "i=36"),
        ("HasNotifier", "i=48"), ("HasSubtype", "i=45"), ("HasTypeDefinition", "i=40"), ("HasModellingRule", "i=37"),
        ("HasEncoding", "i=38"), ("HasDescription", "i=39"), ("FolderType", "i=61"),
        ("BaseDataVariableType", "i=63"), ("Duration", "i=290"),
    };

    /// <summary>
    /// Variable NamespaceUris: the file, the imported libraries, then every
    /// InstanceHierarchy and library of the document that is not imported.
    /// </summary>
    private List<string> NamespaceUris()
    {
        var fileNames = string.Join(" ", _allAttributes.Where(a => a.Name.LocalName == "FileName").Select(a => a.Value));
        var uris = new List<string> { AmlUri + fileNames };
        foreach (var lib in _importedLibraries)
            uris.Add(lib.Name.Contains('/') ? lib.Name : AmlUri + lib.Name);
        foreach (var child in _root.Elements())
        {
            var name = Attr(child, "Name");
            if (name != "" && !_importedLibraries.Any(l => l.Name == name)) uris.Add(AmlUri + name);
        }
        // D7: the XSLT lists a URI once per element, so two libraries of the
        // same name give a namespace table with duplicates.
        return _compat ? uris : uris.Distinct().ToList();
    }

    /// <summary>
    /// Template GetNamespaceIdByName: the index of the namespace whose URI ends
    /// with "/name" or equals it. The XSLT concatenates the indexes of all
    /// matches; with a table free of duplicates (D7) the first one is taken.
    /// </summary>
    private string NamespaceIdByName(string name)
    {
        var hits = new List<int>();
        for (var i = 0; i < _namespaceUris.Count; i++)
        {
            var uri = _namespaceUris[i];
            if (uri.EndsWith("/" + name, StringComparison.Ordinal) || uri == name) hits.Add(i + 1);
        }
        if (hits.Count == 0) return "";
        return _compat ? string.Concat(hits) : hits[0].ToString();
    }

    /// <summary>Template GetNamespace: the name of the enclosing library or InstanceHierarchy.</summary>
    private static string NamespaceName(XObject context)
    {
        if (context is XElement self && (LibraryKinds.Contains(L(self)) || L(self) == "InstanceHierarchy"))
            return Attr(self, "Name");
        var ancestors = (context is XAttribute a ? a.Parent!.AncestorsAndSelf() : ((XElement)context).Ancestors()).ToList();
        foreach (var kind in new[] { "InterfaceClassLib", "RoleClassLib", "SystemUnitClassLib", "AttributeTypeLib", "InstanceHierarchy" })
        {
            var hit = ancestors.FirstOrDefault(x => L(x) == kind);
            if (hit != null) return Attr(hit, "Name");
        }
        return ""; // fn:root()/@FileName: the document node has no attributes
    }

    /// <summary>Template GetNamespaceId.</summary>
    private string NamespaceId(XObject context) => NamespaceIdByName(NamespaceName(context));

    /// <summary>Variable Models: one model per library and InstanceHierarchy, and one for the file.</summary>
    private XElement Models()
    {
        var baseModels = new[]
        {
            new XElement(Ua + "RequiredModel", new XAttribute("ModelUri", "http://opcfoundation.org/UA/"),
                new XAttribute("Version", "1.04.3"), new XAttribute("PublicationDate", "2019-09-09T00:00:00Z")),
            new XElement(Ua + "RequiredModel", new XAttribute("ModelUri", AmlUri),
                new XAttribute("Version", "1.0.1"), new XAttribute("PublicationDate", "2019-09-09T00:00:00Z")),
        };
        if (!_compat)
        {
            // D14: the classes of imported and fallback libraries (the
            // AutomationML base role, interface and system unit classes) are
            // referenced in their own namespaces, but the XSLT does not require
            // those models, so a NodeSet importer cannot know it has to load them.
            baseModels = baseModels.Concat(_importedLibraries
                .Select(l => l.Name.Contains('/') ? l.Name : AmlUri + l.Name)
                .Where(uri => uri != AmlUri)
                .Distinct()
                .Select(uri => new XElement(Ua + "RequiredModel", new XAttribute("ModelUri", uri)))).ToArray();
        }
        var parts = _root.Elements().Where(e => L(e) == "InstanceHierarchy" || LibraryKinds.Contains(L(e))).ToList();
        var models = new XElement(Ua + "Models");
        // Two libraries of one name, or a library and an instance hierarchy of
        // one name, are one model; declaring it twice makes the file unreadable.
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in parts)
        {
            if (!_compat && !declared.Add(AmlUri + Attr(part, "Name"))) continue;
            models.Add(new XElement(Ua + "Model",
                new XAttribute("ModelUri", AmlUri + Attr(part, "Name")),
                new XAttribute("Version", Join(Kids(part, "Version"))),
                new XAttribute("PublicationDate", _publicationDate),
                baseModels));
        }
        var fileNames = string.Join(" ", _allAttributes.Where(a => a.Name.LocalName == "FileName").Select(a => a.Value));
        var file = new XElement(Ua + "Model",
            new XAttribute("ModelUri", AmlUri + fileNames),
            new XAttribute("Version", "0.0.0"),
            new XAttribute("PublicationDate", _publicationDate),
            baseModels);
        var required = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in parts)
        {
            if (!_compat && !required.Add(AmlUri + Attr(part, "Name"))) continue;
            var versions = Kids(part, "Version").ToList();
            var version = versions.Any(v => v.Value == "0" || v.Value == "") ? "0.0.0" : Join(versions);
            file.Add(new XElement(Ua + "RequiredModel",
                new XAttribute("ModelUri", AmlUri + Attr(part, "Name")),
                new XAttribute("Version", version),
                new XAttribute("PublicationDate", _publicationDate)));
        }
        models.Add(file);
        return models;
    }

    private XElement Aliases()
    {
        var aliases = new XElement(Ua + "Aliases", BaseAliases.Select(a => Alias(a.Alias, a.Target)));
        foreach (var lib in _importedLibraries)
        {
            var nsId = NamespaceIdByName(lib.Name);
            foreach (var (alias, target) in lib.Aliases) aliases.Add(Alias(alias, $"ns={nsId};{target}"));
        }
        foreach (var (alias, target) in _extraAliases) aliases.Add(Alias(alias, target));
        return aliases;

        static XElement Alias(string alias, string target) => new(Ua + "Alias", new XAttribute("Alias", alias), target);
    }

    // ------------------------------------------------------------------
    // Template dispatch (xsl:apply-templates)
    // ------------------------------------------------------------------

    private void Apply(XElement e)
    {
        switch (L(e))
        {
            case "InstanceHierarchy": InstanceHierarchy(e); break;
            case "Version": Version(e); break;
            case "Copyright": Copyright(e); break;
            case "AdditionalInformation":
                if (e.Parent != null && L(e.Parent) == "CAEXFile") FileAdditionalInformation(e);
                else AdditionalInformation(e);
                break;
            case "Constraint": Constraint(e); break;
            case "SourceDocumentInformation": SourceDocumentInformation(e); break;
            case "SuperiorStandardVersion": SuperiorStandardVersion(e); break;
            case "Attribute": Attribute(e); break;
            case "RefSemantic": RefSemantic(e); break;
            case "ExternalInterface": ExternalInterface(e); break;
            case "InternalElement": InternalElement(e); break;
            case "InterfaceClassLib":
            case "RoleClassLib":
            case "SystemUnitClassLib":
            case "AttributeTypeLib":
                Library(e);
                ApplyChildren(e);
                break;
            case "InterfaceClass":
            case "RoleClass":
            case "SystemUnitClass":
            case "AttributeType":
                Class(e);
                break;
            default: ApplyChildren(e); break;
        }
    }

    private void ApplyChildren(XElement e)
    {
        foreach (var child in e.Elements().ToList()) Apply(child);
    }

    /// <summary>Templates for @ID: the AML_ID property of the owner.</summary>
    private void ApplyId(XElement owner)
    {
        if (owner.Attribute("ID") is { } id) AmlId(id);
    }

    // ------------------------------------------------------------------
    // CAEXFile and the collections
    // ------------------------------------------------------------------

    private void CaexFile(XElement file)
    {
        var fileName = Attr(file, "FileName");
        var node = new UaNode("UAObject", "ns=1;s=CAEXFile", "1:CAEXFile") { DisplayName = fileName, Description = DescriptionIfPresent(file) };
        node.Ref("HasTypeDefinition", "CAEXFileType");
        if (file.Attribute("FileName") != null) node.Ref("HasProperty", FormatRef("CAEXFile_FileName"));
        if (file.Attribute("SchemaVersion") != null) node.Ref("HasProperty", FormatRef("CAEXFile_SchemaVersion"));
        foreach (var ssv in Kids(file, "SuperiorStandardVersion").Take(_compat ? 1 : int.MaxValue))
            node.Ref("HasProperty", FormatRef("CAEXFile_" + (_compat ? "SuperiorStandardVersion" : NumberedElementName(ssv))));

        var all = file.DescendantsAndSelf().ToList();
        if (all.Any(e => L(e) == "InstanceHierarchy")) node.Ref("Organizes", FormatRef("InstanceHierarchies"));
        if (all.Any(e => L(e) == "InterfaceClassLib" && HasOtherName(e, "AutomationMLInterfaceClassLib")))
            node.Ref("Organizes", FormatRef("InterfaceClassLibs"));
        if (all.Any(e => L(e) == "RoleClassLib" && HasOtherName(e, "AutomationMLBaseRoleClassLib")))
            node.Ref("Organizes", FormatRef("RoleClassLibs"));
        if (all.Any(e => L(e) == "SystemUnitClassLib")) node.Ref("Organizes", FormatRef("SystemUnitClassLibs"));
        if (all.Any(e => L(e) == "AttributeTypeLib" && HasOtherName(e, "AutomationMLBaseAttributeTypeLib")))
            node.Ref("Organizes", FormatRef("AttributeTypeLibs"));
        foreach (var info in Kids(file, "AdditionalInformation"))
            node.Ref("HasProperty", FormatRef("CAEXFile_" + NumberedElementName(info)));
        foreach (var sdi in Kids(file, "SourceDocumentInformation"))
            node.Ref("HasProperty", FormatRef("CAEXFile_" + SourceDocumentInformationName(sdi)));
        node.Ref("Organizes", "CAEXFile_AutomationMLFiles", forward: false);
        Section("CAEXFile");
        Emit(node);

        if (file.Attribute("FileName") is { } fn)
            StringAttributeVariable(null, "CAEXFile", "1", "FileName", "FileName", fn.Value);
        if (file.Attribute("SchemaVersion") is { } sv)
            StringAttributeVariable(null, "CAEXFile", "1", "SchemaVersion", "SchemaVersion", sv.Value);
        foreach (var ssv in Kids(file, "SuperiorStandardVersion").ToList()) SuperiorStandardVersion(ssv);
        foreach (var info in Kids(file, "AdditionalInformation").ToList()) FileAdditionalInformation(info);
        foreach (var sdi in Kids(file, "SourceDocumentInformation").ToList()) SourceDocumentInformation(sdi);

        HierarchicalElement(file, "InstanceHierarchies", "InstanceHierarchy");
        HierarchicalElement(file, "InterfaceClassLibs", "InterfaceClassLib");
        HierarchicalElement(file, "RoleClassLibs", "RoleClassLib");
        HierarchicalElement(file, "SystemUnitClassLibs", "SystemUnitClassLib");
        HierarchicalElement(file, "AttributeTypeLibs", "AttributeTypeLib");

        static bool HasOtherName(XElement e, string name) => e.Attribute("Name") is { } n && n.Value != name;
    }

    /// <summary>Template HierarchicalElement: the folder below CAEXFile for one kind of child.</summary>
    private void HierarchicalElement(XElement file, string name, string childName)
    {
        var children = Kids(file, childName).ToList();
        Section(name);
        if (children.Count > 0)
        {
            // D5: the XSLT gives each folder the descriptions of all children of
            // CAEXFile, libraries of every kind included.
            var descriptionOwners = _compat ? file.Elements() : children;
            var descriptions = descriptionOwners.SelectMany(c => Kids(c, "Description")).ToList();
            var node = new UaNode("UAObject", FormatRef(name, "1"), "1:" + name)
            {
                ParentNodeId = FormatRef("CAEXFile"),
                DisplayName = name,
                Description = descriptions.Count > 0 ? Join(descriptions) : null,
            };
            node.Ref("HasTypeDefinition", "i=61");
            foreach (var child in children)
            {
                var childNs = NamespaceId(child);
                // The library's own NodeId, which tells two libraries of one
                // kind and name in one namespace apart.
                var target = name == "InstanceHierarchies"
                    ? "InstanceHierarchy_" + Attr(child, "Name")
                    : LibraryId(child, childNs);
                node.Ref("Organizes", FormatRef(target, childNs));
            }
            Emit(node);
        }
        foreach (var child in children) Apply(child);
    }

    private void InstanceHierarchy(XElement ih)
    {
        var nsId = NamespaceId(ih);
        var node = new UaNode("UAObject", FormatRef("InstanceHierarchy_" + Attr(ih, "Name"), nsId), nsId + ":" + Attr(ih, "Name"))
        {
            DisplayName = Attr(ih, "Name"),
            Description = DescriptionIfPresent(ih),
        };
        node.Ref("HasTypeDefinition", "i=61");
        node.Ref("Organizes", "CAEXFile_AutomationMLInstanceHierarchies", forward: false);
        node.Ref("HasComponent", FormatRef("InstanceHierarchies"), forward: false);
        foreach (var ie in Kids(ih, "InternalElement")) node.Ref("HasComponent", FormatRef(Attr(ie, "ID"), nsId));
        AddContainerPropertyReferences(node, ih, nsId);
        Emit(node);
        ApplyChildren(ih);
    }

    // ------------------------------------------------------------------
    // Output helpers
    // ------------------------------------------------------------------

    private void Emit(UaNode node) => _output.Add(node.ToXml());

    private void Section(string title)
    {
        if (_options.Comments) _output.Add(new XComment($" {title} "));
    }

    /// <summary>A node under construction; ToXml writes the elements in schema order.</summary>
    private sealed class UaNode
    {
        public UaNode(string kind, string nodeId, string browseName)
        {
            Kind = kind;
            NodeId = nodeId;
            BrowseName = browseName;
        }

        public string Kind { get; }
        public string NodeId { get; }
        public string BrowseName { get; }
        public string? ParentNodeId { get; init; }
        public string? DataType { get; set; }
        public string DisplayName { get; init; } = "";
        public string? Description { get; init; }
        public string? Documentation { get; init; }
        public XElement? Value { get; set; }
        public List<XElement> References { get; } = new();

        public void Ref(string type, string target, bool forward = true) =>
            References.Add(new XElement(Ua + "Reference", new XAttribute("ReferenceType", type),
                forward ? null : new XAttribute("IsForward", "false"), target));

        public XElement ToXml() => new(Ua + Kind,
            new XAttribute("NodeId", NodeId),
            new XAttribute("BrowseName", BrowseName),
            ParentNodeId == null ? null : new XAttribute("ParentNodeId", ParentNodeId),
            DataType == null ? null : new XAttribute("DataType", DataType),
            new XElement(Ua + "DisplayName", DisplayName),
            Description == null ? null : new XElement(Ua + "Description", Description),
            Documentation == null ? null : new XElement(Ua + "Documentation", Documentation),
            new XElement(Ua + "References", References),
            Value);
    }

    // ------------------------------------------------------------------
    // XPath helpers
    // ------------------------------------------------------------------

    private static string L(XElement e) => e.Name.LocalName;

    private static IEnumerable<XAttribute> Attributes(XElement e) => e.Attributes().Where(a => !a.IsNamespaceDeclaration);

    /// <summary>An unqualified attribute's value, "" when absent (XPath string(@name)).</summary>
    private static string Attr(XElement? e, string name) => e?.Attribute(name)?.Value ?? "";

    /// <summary>Children by local name (the XSLT's *[local-name()='x']).</summary>
    private static IEnumerable<XElement> Kids(XElement e, string localName) => e.Elements().Where(c => L(c) == localName);

    /// <summary>
    /// Children tested with an unprefixed name in the XSLT, which matches only
    /// elements without namespace, so only in CAEX 2.15 documents (D2).
    /// </summary>
    private IEnumerable<XElement> UnprefixedKids(XElement e, string localName) =>
        _compat ? e.Elements(XName.Get(localName)) : Kids(e, localName);

    /// <summary>xsl:value-of over several nodes: their string values separated by spaces.</summary>
    private static string Join(IEnumerable<XElement> elements) => string.Join(" ", elements.Select(e => e.Value));

    private static string? DescriptionIfPresent(XElement e)
    {
        var d = Kids(e, "Description").ToList();
        return d.Count > 0 ? Join(d) : null;
    }

    private static string? DescriptionIfNotEmpty(XElement? e)
    {
        if (e == null) return null;
        var d = Kids(e, "Description").ToList();
        return d.Any(x => x.Value != "") ? Join(d) : null;
    }

    private static string Before(string s, string sep)
    {
        var i = s.IndexOf(sep, StringComparison.Ordinal);
        return i < 0 ? "" : s[..i];
    }

    private static string After(string s, string sep)
    {
        var i = s.IndexOf(sep, StringComparison.Ordinal);
        return i < 0 ? "" : s[(i + sep.Length)..];
    }

    /// <summary>
    /// The library of a class path. D13: CAEX 3.0 writes a path whose parts
    /// contain '/' with brackets, "[SUC_http://x/]/[Type]"; the XSLT splits at
    /// the first '/' and gets "[SUC_http:". Every library OPC 10000-83 Annex A
    /// generates is named after a namespace URI, so without this no class of
    /// such a library resolves.
    /// </summary>
    private string PathLib(string path)
    {
        if (_compat || !path.StartsWith('[')) return Before(path, "/");
        var end = path.IndexOf("]/", StringComparison.Ordinal);
        return end > 0 ? path[1..end] : path.Trim('[', ']');
    }

    /// <summary>The class part of a path, "A/B" for "[Lib]/[A]/[B]" (D13).</summary>
    private string PathRest(string path)
    {
        if (_compat || !path.StartsWith('[')) return After(path, "/");
        var end = path.IndexOf("]/", StringComparison.Ordinal);
        if (end < 0) return "";
        return string.Join("/", path[(end + 2)..].Split("]/[").Select(s => s.Trim('[', ']')));
    }

    /// <summary>replace($s, '.*/(.*)', '$1'): the part after the last slash.</summary>
    private static string LastSegment(string s)
    {
        var i = s.LastIndexOf('/');
        return i < 0 ? s : s[(i + 1)..];
    }

    /// <summary>fn2:remove-space.</summary>
    private static string RemoveSpace(string s) => s.Replace(" ", "");

    private static readonly System.Text.RegularExpressions.Regex BareGuid =
        new(@"^([a-z,A-Z,0-9]*-){4}[a-z,A-Z,0-9]*\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Template FormatRef: a string NodeId; namespace 1 when none is given, and
    /// braces around IDs that look like a GUID without them.
    /// </summary>
    private static string FormatRef(string objectId, string? ns = null)
    {
        var nsId = string.IsNullOrEmpty(ns) ? "1" : ns;
        return BareGuid.IsMatch(objectId)
            ? $"ns={nsId};s={{{RemoveSpace(objectId)}}}"
            : $"ns={nsId};s={RemoveSpace(objectId)}";
    }

    /// <summary>Template NumberedElementName: the local name, with _n for the n-th repetition among siblings.</summary>
    private static string NumberedElementName(XElement e)
    {
        var count = e.ElementsBeforeSelf().Count(s => L(s) == L(e));
        return count == 0 ? RemoveSpace(L(e)) : $"{RemoveSpace(L(e))}_{count}";
    }

    /// <summary>
    /// Template NumberedAttributeName as the XSLT applies it to
    /// SourceDocumentInformation. That element has no Name, so the XSLT never
    /// numbers it and a second SourceDocumentInformation repeats the NodeId (D6).
    /// </summary>
    private string SourceDocumentInformationName(XElement sdi) =>
        _compat ? RemoveSpace(L(sdi)) : NumberedElementName(sdi);

    /// <summary>One InternalLink with its two sides split at the colon.</summary>
    private sealed record InternalLinkSides(string Name, string ParentSideA, string InterfaceSideA, string ParentSideB, string InterfaceSideB)
    {
        public static InternalLinkSides Of(XElement link)
        {
            var a = Attr(link, "RefPartnerSideA");
            var b = Attr(link, "RefPartnerSideB");
            return new InternalLinkSides(Attr(link, "Name"), Before(a, ":"), After(a, ":"), Before(b, ":"), After(b, ":"));
        }
    }
}
