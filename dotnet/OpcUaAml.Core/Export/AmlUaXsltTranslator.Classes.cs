// LibraryTranslation.xslt and the class lookup of LibraryParsing.xslt:
// libraries become Objects of FolderType, classes ObjectTypes (AttributeTypes
// VariableTypes) organized below their library or parent class, with
// HasSubtype to the class they derive from.

using System.Xml.Linq;

namespace OpcUaAml.Export;

internal sealed partial class AmlUaXsltTranslator
{
    private static readonly string[] ClassKinds = { "SystemUnitClass", "RoleClass", "InterfaceClass", "AttributeType" };

    /// <summary>
    /// What template GetClass returns: a class element of the document, an
    /// "Alias" stand-in for a class of an imported library, or nothing.
    /// </summary>
    private readonly record struct ClassLookup(XElement? Class, string? AliasName)
    {
        public static ClassLookup None => default;

        /// <summary>The class name when the result is a class of the given kind with a name.</summary>
        public string? ClassName(string kind) =>
            Class != null && L(Class) == kind && Attr(Class, "Name") != "" ? Attr(Class, "Name") : null;

        /// <summary>@Name of the result, whatever it is (the XSLT's $BaseClass/*/@Name).</summary>
        public string Name => Class != null ? Attr(Class, "Name") : AliasName ?? "";
    }

    /// <summary>
    /// Template GetClass: resolves "Lib/Class/SubClass". Libraries are searched
    /// by kind (RoleClassLib, SystemUnitClassLib, AttributeTypeLib,
    /// InterfaceClassLib), all libraries of that name together. A class found
    /// without a base class is replaced by the alias stand-in when the path
    /// starts with the name of an imported library. The XSLT also collects
    /// the attributes along the base classes, which no caller reads.
    /// </summary>
    private ClassLookup GetClass(string path)
    {
        if (!_classCache.TryGetValue(path, out var result)) _classCache[path] = result = FindClass(path);
        return result;
    }

    private readonly Dictionary<string, ClassLookup> _classCache = new(StringComparer.Ordinal);

    private ILookup<(string Kind, string Name), XElement>? _librariesByName;

    private ClassLookup FindClass(string path)
    {
        _librariesByName ??= _root.DescendantsAndSelf().Where(e => LibraryKinds.Contains(L(e))).ToLookup(e => (L(e), Attr(e, "Name")));
        var libName = Before(path, "/");
        var subPath = After(path, "/");
        XElement? current = null;
        if (libName != "")
        {
            foreach (var (libKind, classKind) in new[]
                     {
                         ("RoleClassLib", "RoleClass"), ("SystemUnitClassLib", "SystemUnitClass"),
                         ("AttributeTypeLib", "AttributeType"), ("InterfaceClassLib", "InterfaceClass"),
                     })
            {
                var libs = _librariesByName[(libKind, libName)].ToList();
                if (libs.Count == 0) continue;
                current = GetSubClass(subPath, libs.SelectMany(l => Kids(l, classKind)).ToList()).FirstOrDefault();
                break;
            }
        }

        if (current != null && Attr(current, "RefBaseClassPath") != "") return new ClassLookup(current, null);

        var imported = _importedLibraries.Where(l => path.StartsWith(l.Name + "/", StringComparison.Ordinal)).ToList();
        if (imported.Count > 0)
        {
            var alias = imported.SelectMany(l => l.Aliases.Select(a => (l.Name, a.Alias)))
                .FirstOrDefault(x => path == x.Name + "/" + x.Alias).Alias;
            return new ClassLookup(null, alias ?? "");
        }
        return new ClassLookup(current, null);
    }

    /// <summary>Template GetSubClass: walks "A/B/C" through nested classes of any kind.</summary>
    private static IEnumerable<XElement> GetSubClass(string search, List<XElement> input)
    {
        if (search.Contains('/'))
        {
            var parentClass = Before(search, "/");
            var parents = input.Where(e => ClassKinds.Contains(L(e)) && Attr(e, "Name") == parentClass).ToList();
            return parents.Count == 0 ? Enumerable.Empty<XElement>()
                : GetSubClass(After(search, "/"), parents.SelectMany(p => p.Elements()).ToList());
        }
        return input.Where(e => ClassKinds.Contains(L(e)) && Attr(e, "Name") == search);
    }

    private static readonly Dictionary<string, string> LibraryCollections = new()
    {
        ["InterfaceClassLib"] = "CAEXFile_InterfaceClassLibs",
        ["RoleClassLib"] = "CAEXFile_RoleClassLibs",
        ["SystemUnitClassLib"] = "CAEXFile_SystemUnitClassLibs",
        ["AttributeTypeLib"] = "CAEXFile_AttributeTypeLibs",
    };

    /// <summary>Template Library: the library as a folder in its own namespace.</summary>
    private void Library(XElement lib)
    {
        var nsId = NamespaceId(lib);
        var libId = RemoveSpace(L(lib));
        Section($"{L(lib)} {Attr(lib, "Name")}");
        var node = new UaNode("UAObject", $"ns={nsId};s={libId}", Attr(lib, "Name"))
        {
            DisplayName = Attr(lib, "Name"),
            Documentation = DescriptionIfNotEmpty(lib),
        };
        if (Kids(lib, "Version").Any()) node.Ref("HasProperty", $"ns={nsId};s={libId}_Version");
        node.Ref("HasTypeDefinition", "i=61");
        node.Ref("Organizes", LibraryCollections[L(lib)], forward: false);
        foreach (var cls in lib.Elements().Where(c => ClassKinds.Contains(L(c))))
            node.Ref("Organizes", $"ns={nsId};s={RemoveSpace(Attr(cls, "Name"))}");
        AddContainerPropertyReferences(node, lib, nsId);
        Emit(node);
    }

    /// <summary>
    /// Templates InterfaceClass, RoleClass, SystemUnitClass (ObjectTypes) and
    /// AttributeType (VariableType).
    /// </summary>
    private void Class(XElement cls)
    {
        var nsId = NamespaceId(cls);
        var name = Attr(cls, "Name");
        // D2: the XSLT tests an unprefixed Description, so classes of CAEX 3.0
        // documents never get their documentation.
        var descriptions = UnprefixedKids(cls, "Description").ToList();
        var node = new UaNode(L(cls) == "AttributeType" ? "UAVariableType" : "UAObjectType", $"ns={nsId};s={RemoveSpace(name)}", name)
        {
            DisplayName = name,
            Documentation = descriptions.Any(d => d.Value != "") ? Join(descriptions) : null,
        };
        foreach (var r in ClassReferences(cls)) node.References.Add(r);
        Emit(node);
        ApplyId(cls);
        ApplyChildren(cls);
    }

    /// <summary>
    /// Template ClassReferences: the inverse Organizes from the parent class or
    /// library, the inverse HasSubtype from the base class, then the References
    /// template.
    /// </summary>
    private IEnumerable<XElement> ClassReferences(XElement cls)
    {
        var nsId = NamespaceId(cls);
        var libId = string.Concat(cls.Ancestors().Where(a => LibraryKinds.Contains(L(a))).Select(a => RemoveSpace(L(a))));
        var node = new UaNode("UAObjectType", "", "");

        var parentClass = new[] { "InterfaceClass", "RoleClass", "SystemUnitClass", "AttributeType" }
            .Select(kind => cls.Ancestors().FirstOrDefault(a => L(a) == kind))
            .FirstOrDefault(a => a != null);
        // D4: the XSLT keeps spaces in the parent's name here, while the
        // parent's NodeId has them removed.
        node.Ref("Organizes", parentClass != null ? $"ns={nsId};s={NoSpaceUnlessCompat(Attr(parentClass, "Name"))}" : $"ns={nsId};s={libId}",
            forward: false);

        var basePath = cls.Attribute("RefBaseClassPath")?.Value ?? cls.Attribute("RefAttributeType")?.Value ?? "";
        var baseClass = GetClass(basePath);
        var name = Attr(cls, "Name");
        string superType;
        if (basePath != "" && !basePath.Contains('/')) superType = $"ns={nsId};s={NoSpaceUnlessCompat(basePath)}";
        else if (basePath.Contains('@')) superType = After(basePath, "@");
        else if (basePath != "") superType = $"ns={NamespaceIdByName(Before(basePath, "/"))};s={RemoveSpace(baseClass.Name)}";
        else if (L(cls) == "SystemUnitClass" && name != "AutomationMLBaseSystemUnit") superType = "CAEXObjectType";
        else if (L(cls) == "RoleClass" && name != "AutomationMLBaseRole") superType = "AutomationMLBaseRoleClassLib/AutomationMLBaseRole";
        else if (L(cls) == "InterfaceClass" && name != "AutomationMLBaseInterface") superType = "AutomationMLInterfaceClassLib/AutomationMLBaseInterface";
        else if (L(cls) == "AttributeType") superType = "AMLBaseVariableType";
        else superType = "CAEXObjectType";
        node.Ref("HasSubtype", superType, forward: false);

        return node.References.Concat(References(cls, ObjectName(cls, cls), nsId));
    }

    private string NoSpaceUnlessCompat(string s) => _compat ? s : RemoveSpace(s);
}
