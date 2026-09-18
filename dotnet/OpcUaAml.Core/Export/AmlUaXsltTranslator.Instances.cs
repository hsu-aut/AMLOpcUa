// AML2Nodeset.xslt, instance side: the property variables (AML_ID, Version,
// AdditionalInformation, constraints), CAEX attributes, RefSemantic,
// InternalElements, ExternalInterfaces with their InternalLinks, and the
// References template that every node uses for its children and types.

using System.Xml.Linq;

namespace OpcUaAml.Export;

internal sealed partial class AmlUaXsltTranslator
{
    // ------------------------------------------------------------------
    // Property variables (templates AttributeVariable, StringAttributeVariable)
    // ------------------------------------------------------------------

    /// <summary>
    /// Template StringAttributeVariable: a String variable below
    /// <paramref name="parentId"/>. <paramref name="context"/> is the element
    /// the XSLT template runs on (null for XML attributes); its children decide
    /// the type definition, its description and its own references.
    /// <paramref name="value"/> is a string, an element (written as XML text)
    /// or null for no value.
    /// </summary>
    private void StringAttributeVariable(XElement? context, string parentId, string ns, string attributeName, string browseName, object? value)
    {
        var qualifiedBrowseName = ns == "" ? browseName
            : ns.Contains('=') ? After(ns, "=") + ":" + browseName
            : ns + ":" + browseName;
        var node = new UaNode("UAVariable", FormatRef(parentId + "_" + attributeName, ns), qualifiedBrowseName)
        {
            ParentNodeId = FormatRef(parentId, ns),
            DataType = "String",
            DisplayName = browseName,
            Description = DescriptionIfNotEmpty(context),
        };
        node.Ref("HasTypeDefinition", VariableTypeDefinition(context, browseName));
        foreach (var r in References(context, parentId + "_" + attributeName + Attr(context, "ID"), ns))
            node.References.Add(r);
        if (value != null)
        {
            var text = value is XElement xml ? CaexXmlText(xml) : (string)value;
            node.Value = new XElement(Ua + "Value", new XElement(Uax + "String", Text(text)));
        }
        Emit(node);
    }

    /// <summary>The HasTypeDefinition choice of template AttributeVariable.</summary>
    private string VariableTypeDefinition(XElement? context, string browseName)
    {
        if (context != null)
        {
            if (Kids(context, "NominalScaledType").Any()) return "CAEXNominalScaledConstraintType";
            if (Kids(context, "OrdinalScaledType").Any()) return "CAEXOrdinalScaledConstraintType";
            // D11: CAEX calls the element UnknownType; the XSLT tests for
            // UnknownConstraint, which never occurs.
            if (Kids(context, _compat ? "UnknownConstraint" : "UnknownType").Any()) return "CAEXUnknownConstraintType";
        }
        if (browseName.Contains("RequiredValue") || browseName.Contains("RequiredMinValue")
            || browseName.Contains("RequiredMaxValue") || browseName.Contains("Requirements"))
            return "CAEX" + (context == null ? "" : L(context));
        return "AMLBaseVariableType";
    }

    /// <summary>
    /// XML-valued properties (AdditionalInformation, SourceDocumentInformation,
    /// Copyright) as text. Template copy-to-new-ns drops the CAEX namespace.
    /// </summary>
    private static string CaexXmlText(XElement e) => StripNamespaces(e).ToString(SaveOptions.DisableFormatting);

    private static XElement StripNamespaces(XElement e) => new(
        e.Name.LocalName,
        Attributes(e).Select(a => new XAttribute(a.Name, a.Value)),
        e.Nodes().Select(n => n switch
        {
            XElement c => StripNamespaces(c),
            XText t => (XNode)new XText(t.Value),
            _ => null,
        }).Where(n => n != null));

    /// <summary>Text that holds XML goes into a CDATA section, as AML2UA.py does it.</summary>
    private static XText Text(string s) => s.TrimStart().StartsWith('<') ? new XCData(s) : new XText(s);

    /// <summary>The text nodes of an element, null when it has none (the XSLT's select="text()").</summary>
    private static string? TextValue(XElement e)
    {
        var texts = e.Nodes().OfType<XText>().ToList();
        return texts.Count == 0 ? null : string.Concat(texts.Select(t => t.Value));
    }

    private void AmlId(XAttribute id)
    {
        var owner = id.Parent!;
        var value = id.Value.StartsWith('{') ? id.Value : "{" + id.Value + "}";
        StringAttributeVariable(null, ObjectName(owner, id), NamespaceId(id), "AML_ID", "AML_ID", value);
    }

    private void Version(XElement version)
    {
        var nsId = NamespaceId(version);
        var ownerId = PropertyOwnerId(version.Parent!, version, xsltHandlesContainers: true);
        StringAttributeVariable(version, ownerId, nsId != "" ? nsId : "1", "Version", "Version", TextValue(version));
    }

    /// <summary>
    /// The string part of the NodeId of the node that owns a Version,
    /// Copyright or AdditionalInformation. The XSLT takes libraries and
    /// InstanceHierarchies into account for Version only; for Copyright and
    /// AdditionalInformation it uses their name or ID, which is not their
    /// NodeId (D12).
    /// </summary>
    private string PropertyOwnerId(XElement owner, XObject context, bool xsltHandlesContainers)
    {
        if (xsltHandlesContainers || !_compat)
        {
            if (LibraryKinds.Contains(L(owner))) return RemoveSpace(L(owner));
            if (L(owner) == "InstanceHierarchy") return "InstanceHierarchy_" + Attr(owner, "Name");
        }
        return ObjectName(owner, context);
    }

    private void Copyright(XElement copyright) =>
        StringAttributeVariable(copyright, PropertyOwnerId(copyright.Parent!, copyright, xsltHandlesContainers: false),
            NamespaceId(copyright), "Copyright", "Copyright", copyright);

    /// <summary>AdditionalInformation below anything but CAEXFile.</summary>
    private void AdditionalInformation(XElement info)
    {
        var attributeName = NumberedElementName(info);
        StringAttributeVariable(info, PropertyOwnerId(info.Parent!, info, xsltHandlesContainers: false),
            NamespaceId(info), attributeName, attributeName, info);
    }

    /// <summary>
    /// D12: references from libraries and InstanceHierarchies to their
    /// Version, Copyright and AdditionalInformation properties. The XSLT
    /// creates these nodes (and Copyright nodes of any element) but no
    /// reference to them; References() covers Copyright of the other elements.
    /// </summary>
    private void AddContainerPropertyReferences(UaNode node, XElement container, string ns)
    {
        if (_compat) return;
        var ownerId = PropertyOwnerId(container, container, xsltHandlesContainers: true);
        // Libraries already reference their Version.
        if (L(container) == "InstanceHierarchy" && Kids(container, "Version").Any())
            node.Ref("HasProperty", FormatRef(ownerId + "_Version", ns));
        if (Kids(container, "Copyright").Any())
            node.Ref("HasProperty", FormatRef(ownerId + "_Copyright", ns));
        foreach (var info in Kids(container, "AdditionalInformation"))
            node.Ref("HasComponent", FormatRef(ownerId + "_" + NumberedElementName(info), ns));
    }

    /// <summary>AdditionalInformation below CAEXFile, named after its first child or attribute.</summary>
    private void FileAdditionalInformation(XElement info)
    {
        var attributeName = NumberedElementName(info);
        var browseName = info.Elements().FirstOrDefault()?.Name.LocalName
            ?? Attributes(info).FirstOrDefault()?.Name.LocalName
            ?? attributeName;
        StringAttributeVariable(info, "CAEXFile", "1", attributeName, browseName, info);
    }

    private void SourceDocumentInformation(XElement sdi)
    {
        var attributeName = SourceDocumentInformationName(sdi);
        StringAttributeVariable(sdi, "CAEXFile", "1", attributeName, attributeName, sdi);
    }

    private void SuperiorStandardVersion(XElement ssv)
    {
        // D6: repeated SuperiorStandardVersion elements get numbered NodeIds.
        var attributeName = _compat ? "SuperiorStandardVersion" : NumberedElementName(ssv);
        StringAttributeVariable(ssv, "CAEXFile", "1", attributeName, attributeName, ssv.Value);
    }

    private static readonly string[] RequirementKinds = { "RequiredValue", "RequiredMaxValue", "RequiredMinValue", "Requirements" };

    /// <summary>A Constraint of an attribute, with one variable per required value.</summary>
    private void Constraint(XElement constraint)
    {
        var attributeName = NumberedElementName(constraint);
        var browseName = Attr(constraint, "Name") != "" ? Attr(constraint, "Name")
            : Attributes(constraint).FirstOrDefault()?.Name.LocalName
            ?? constraint.Elements().FirstOrDefault()?.Name.LocalName
            ?? attributeName;
        var nsId = NamespaceId(constraint);
        var objectId = ObjectName(constraint.Parent, constraint);
        StringAttributeVariable(constraint, objectId, nsId, attributeName, browseName, null);
        foreach (var requirement in constraint.Elements().Elements().Where(r => RequirementKinds.Contains(L(r))))
        {
            var constraintName = NumberedElementName(requirement);
            var constraintBrowseName = Attr(requirement, "Name") != "" ? Attr(requirement, "Name") : constraintName;
            StringAttributeVariable(requirement, objectId + "_" + attributeName, nsId, constraintName, constraintBrowseName, TextValue(requirement));
        }
    }

    // ------------------------------------------------------------------
    // Object names
    // ------------------------------------------------------------------

    /// <summary>
    /// Template GetObjectName: the string part of an element's NodeId. Named
    /// classes go by name, everything with an ID by ID, attributes by their
    /// owner's name and their own. <paramref name="context"/> is the node the
    /// calling template runs on; its local name is the last resort.
    /// </summary>
    private string ObjectName(XElement? obj, XObject context)
    {
        var name = Attr(obj, "Name");
        if (obj != null && name != "" && IsNamedClass(L(obj))) return name;
        // D3: an Attribute with an ID (CAEX 3.0 allows one) has a NodeId built
        // from its owner and name, but the XSLT names its children by the ID.
        var id = obj != null && L(obj) == "Attribute" && !_compat ? "" : Attr(obj, "ID");
        if (id != "") return id;
        if (obj != null && L(obj) == "Attribute") return ObjectName(obj.Parent, context) + "_" + name;
        if (name != "") return name;
        return context switch
        {
            XElement e => L(e),
            XAttribute a => a.Name.LocalName,
            _ => "",
        };
    }

    /// <summary>
    /// D3: the XSLT lists "AttributeTypeClass", which is no CAEX element, so an
    /// AttributeType with an ID is named by its ID in its children's NodeIds
    /// while its own NodeId uses the name.
    /// </summary>
    private bool IsNamedClass(string localName) =>
        localName is "SystemUnitClass" or "RoleClass" or "InterfaceClass"
        || localName == (_compat ? "AttributeTypeClass" : "AttributeType");

    // ------------------------------------------------------------------
    // CAEX attributes
    // ------------------------------------------------------------------

    /// <summary>DatatypeTranslation.xslt, in table order: the first entry that matches wins.</summary>
    private static readonly (string OpcAlias, string OpcNodeId, string Aml)[] DataTypes =
    {
        ("String", "i=12", "xs:string"), ("Boolean", "i=1", "xs:boolean"), ("Decimal", "", "xs:decimal"),
        ("Float", "i=10", "xs:float"), ("Double", "i=11", "xs:double"), ("Duration", "", "xs:duration"),
        ("ByteString", "i=15", "xs:base64Binary"), ("Int64", "i=8", "xs:long"), ("Int32", "i=6", "xs:int"),
        ("Int32", "i=6", "xs:integer"), ("Int16", "i=4", "xs:short"), ("SByte", "i=2", "xs:byte"),
        ("UInt64", "i=9", "xs:positiveInteger"), ("UInt64", "i=9", "xs:unsignedLong"), ("UInt32", "i=7", "xs:unsignedInt"),
        ("UInt16", "i=5", "xs:unsignedShort"), ("Byte", "i=3", "xs:unsignedByte"),
        ("NormalizedString", "", "xs:normalizedString "), ("LocaleId", "", "xs:language"),
        ("QualifiedName", "", "xs:anyURI"), ("UriString", "", "xs:anyURI"), ("DateTime", "i=13", "xs:date"),
        ("Guid", "i=14", "xs:token"), ("ImagePNG", "i=2003", "xs:string"),
    };

    /// <summary>
    /// Base model NodeIds of data type names the table produces but the alias
    /// list of the XSLT lacks (D9).
    /// </summary>
    private static readonly Dictionary<string, string> MissingDataTypeAliases = new()
    {
        ["Decimal"] = "i=50", ["NormalizedString"] = "i=12877", ["LocaleId"] = "i=295", ["UriString"] = "i=23751", ["ImagePNG"] = "i=2003",
    };

    private string DataTypeOf(XElement attribute)
    {
        var amlType = AsciiLower(Attr(attribute, "AttributeDataType"));
        // D9: "xs:normalizedString " carries a trailing space in the table.
        var matches = DataTypes.Where(d => AsciiLower(_compat ? d.Aml : d.Aml.Trim()) == amlType).ToList();
        string dataType;
        if (matches.Any(m => m.OpcAlias != "")) dataType = matches[0].OpcAlias;
        else if (matches.Any(m => m.OpcNodeId != "")) dataType = matches[0].OpcNodeId;
        // D9: for types missing from the table the XSLT writes the lower-cased
        // XML Schema name (e.g. "datetime"), which is no UA data type.
        else dataType = _compat ? After(amlType, ":") : "";

        if (!_compat && MissingDataTypeAliases.TryGetValue(dataType, out var nodeId)) _extraAliases[dataType] = nodeId;
        return dataType;
    }

    private static string AsciiLower(string s) =>
        string.Create(s.Length, s, (span, src) =>
        {
            for (var i = 0; i < src.Length; i++) span[i] = src[i] is >= 'A' and <= 'Z' ? (char)(src[i] + 32) : src[i];
        });

    /// <summary>
    /// Values are written with their own type for these data types. D10: the
    /// XSLT writes Float, Double, Byte and SByte values as String.
    /// </summary>
    private bool HasTypedValue(string dataType) =>
        dataType is "String" or "Boolean" || dataType.Contains("Int")
        || (!_compat && dataType is "Float" or "Double" or "Byte" or "SByte");

    private void Attribute(XElement attribute)
    {
        if (attribute.Parent != null && L(attribute.Parent) == "RoleRequirements") return;
        var nsId = NamespaceId(attribute);
        var objectId = ObjectName(attribute.Parent, attribute);
        var name = Attr(attribute, "Name");
        var dataType = DataTypeOf(attribute);
        var refAttributeType = Attr(attribute, "RefAttributeType");
        var attrAliasName = After(refAttributeType, "@");
        var attrLibName = Before(refAttributeType, "/");
        var amlAttributeType = attrLibName != "" ? GetClass(refAttributeType) : ClassLookup.None;
        var attrLibId = NamespaceIdByName(attrLibName);

        var node = new UaNode("UAVariable", FormatRef(objectId + "_" + name, nsId), nsId + ":" + name)
        {
            ParentNodeId = FormatRef(objectId, nsId),
            DataType = dataType != "" ? dataType : null,
            DisplayName = name,
            Description = DescriptionIfNotEmpty(attribute),
        };
        node.Ref("HasComponent", FormatRef(objectId, nsId), forward: false);
        if (amlAttributeType.ClassName("AttributeType") is { } typeName)
            node.Ref("HasTypeDefinition", FormatRef(typeName, attrLibId));
        else if (attrLibName.Contains('@'))
            node.Ref("HasTypeDefinition", attrAliasName);
        else
            node.Ref("HasTypeDefinition", "AMLBaseVariableType");
        foreach (var r in References(attribute, objectId + "_" + name, nsId)) node.References.Add(r);

        var values = Kids(attribute, "Value").ToList();
        if (HasTypedValue(dataType))
        {
            var defaults = Kids(attribute, "DefaultValue").ToList();
            var source = values.Any(v => v.Value != "") ? values : defaults.Any(v => v.Value != "") ? defaults : null;
            if (source != null) node.Value = new XElement(Ua + "Value", new XElement(Uax + dataType, Join(source)));
        }
        else if (values.Count > 0)
        {
            node.Value = new XElement(Ua + "Value", new XElement(Uax + "String", Join(values)));
        }
        Emit(node);
        // D3: References() points to the AML_ID of an Attribute with an ID,
        // which the XSLT never creates.
        if (!_compat) ApplyId(attribute);
        ApplyChildren(attribute);
    }

    /// <summary>
    /// D6: the XSLT names every RefSemantic of an element "_RefSemantic", so a
    /// second one repeats the NodeId. The default numbers them.
    /// </summary>
    private string RefSemanticSuffix(XElement refSemantic)
    {
        var n = refSemantic.ElementsBeforeSelf().Count(s => L(s) == "RefSemantic");
        return _compat || n == 0 ? "_RefSemantic" : $"_RefSemantic_{n}";
    }

    private void RefSemantic(XElement refSemantic)
    {
        var nsId = NamespaceId(refSemantic);
        var objectId = ObjectName(refSemantic.Parent, refSemantic);
        var suffix = RefSemanticSuffix(refSemantic);
        var node = new UaNode("UAVariable", FormatRef(objectId + suffix, nsId), nsId + ":" + objectId + suffix)
        {
            ParentNodeId = FormatRef(objectId, nsId),
            // D1: the XSLT writes the display name "RefSematic".
            DisplayName = _compat ? "RefSematic" : "RefSemantic",
            Description = DescriptionIfNotEmpty(refSemantic),
        };
        node.Ref("HasAMLReferenceType", FormatRef(objectId, nsId), forward: false);
        node.Ref("HasTypeDefinition", "AMLBaseVariableType");
        node.Value = new XElement(Ua + "Value", new XElement(Uax + "String", Attr(refSemantic, "CorrespondingAttributePath")));
        Emit(node);
    }

    // ------------------------------------------------------------------
    // InternalElements and ExternalInterfaces
    // ------------------------------------------------------------------

    private void InternalElement(XElement ie)
    {
        var nsId = NamespaceId(ie);
        var node = new UaNode("UAObject", FormatRef(Attr(ie, "ID"), nsId), nsId + ":" + Attr(ie, "Name"))
        {
            DisplayName = Attr(ie, "Name"),
            Description = DescriptionIfPresent(ie),
        };
        foreach (var r in References(ie, Attr(ie, "ID"), nsId)) node.References.Add(r);
        Emit(node);
        ApplyId(ie);
        ApplyChildren(ie);
    }

    private void ExternalInterface(XElement ei)
    {
        var nsId = NamespaceId(ei);
        var parent = ei.Parent!;
        var parentId = parent.Attribute("ID")?.Value;
        var interfaceName = ei.Attribute("Name")?.Value;
        var parentNodeId = FormatRef(ObjectName(parent, ei), nsId);
        var interfaceNodeId = FormatRef(ObjectName(ei, ei), nsId);
        var refBaseClassPath = Attr(ei, "RefBaseClassPath");
        var icAliasName = After(refBaseClassPath, "@");
        var icLibName = Before(refBaseClassPath, "/");
        var icContent = icLibName != "" && refBaseClassPath != "AutomationMLInterfaceClassLib/AutomationMLBaseInterface"
            ? GetClass(refBaseClassPath) : ClassLookup.None;
        var libNsId = NamespaceIdByName(icLibName);

        var node = new UaNode("UAObject", interfaceNodeId, nsId + ":" + Attr(ei, "Name"))
        {
            ParentNodeId = parentNodeId,
            DisplayName = Attr(ei, "Name"),
            Description = DescriptionIfPresent(ei),
        };
        if (icContent.ClassName("InterfaceClass") is { } className)
            node.Ref("HasTypeDefinition", FormatRef(className, libNsId));
        else if (icLibName.Contains('@'))
            node.Ref("HasTypeDefinition", icAliasName);
        foreach (var r in References(ei, Attr(ei, "ID"), nsId)) node.References.Add(r);

        foreach (var link in _internalLinks.Where(l => l.InterfaceSideA == interfaceName && l.ParentSideA == parentId))
            foreach (var partner in LinkPartners(link.ParentSideB, link.InterfaceSideB, nsId))
                node.Ref("HasAMLInternalLink", partner);
        foreach (var link in _internalLinks.Where(l => l.InterfaceSideB == interfaceName && l.ParentSideB == parentId))
            foreach (var partner in LinkPartners(link.ParentSideA, link.InterfaceSideA, nsId))
                node.Ref("HasAMLInternalLink", partner, forward: false);
        Emit(node);
        ApplyId(ei);
        ApplyChildren(ei);
    }

    /// <summary>
    /// Whether the ID of some InternalElement contains <paramref name="path"/>:
    /// the XSLT's test for a mirror object, whose RefBaseSystemUnitPath is the
    /// ID of its master.
    /// </summary>
    private bool IsMirrorPath(string path)
    {
        _internalElementIds ??= _root.DescendantsAndSelf().Where(x => L(x) == "InternalElement").Select(x => Attr(x, "ID")).ToList();
        if (!_mirrorCache.TryGetValue(path, out var isMirror))
            _mirrorCache[path] = isMirror = _internalElementIds.Any(id => id.Contains(path, StringComparison.Ordinal));
        return isMirror;
    }

    private List<string>? _internalElementIds;
    private readonly Dictionary<string, bool> _mirrorCache = new(StringComparer.Ordinal);
    private ILookup<string, XElement>? _elementsById;

    /// <summary>
    /// The interface at the other end of an InternalLink: any descendant named
    /// <paramref name="interfaceName"/> of the element with ID
    /// <paramref name="parentId"/>. The XSLT joins the IDs of all matches into
    /// one NodeId in the namespace of the near end, and writes "ns=n;s=" when
    /// there is none. D8: one reference per match, in the partner's namespace,
    /// none without a match.
    /// </summary>
    private IEnumerable<string> LinkPartners(string parentId, string interfaceName, string nsId)
    {
        _elementsById ??= _root.DescendantsAndSelf().Where(e => e.Attribute("ID") != null).ToLookup(e => e.Attribute("ID")!.Value, StringComparer.Ordinal);
        var matches = _elementsById[parentId]
            .SelectMany(e => e.Descendants())
            .Where(e => e.Attribute("Name")?.Value == interfaceName && e.Attribute("ID") != null)
            .Distinct()
            .ToList();
        if (_compat)
            return new[] { FormatRef(string.Join(" ", matches.Select(m => m.Attribute("ID")!.Value)), nsId) };
        return matches.Select(m => FormatRef(m.Attribute("ID")!.Value, NamespaceId(m)));
    }

    // ------------------------------------------------------------------
    // Template References
    // ------------------------------------------------------------------

    /// <summary>
    /// Template References: the forward references of a node to its
    /// properties, attributes, interfaces and child elements, its type
    /// definition and its roles. <paramref name="objectId"/> is the string part
    /// of the node's own NodeId, from which the child NodeIds are built.
    /// </summary>
    private IEnumerable<XElement> References(XElement? e, string objectId, string ns)
    {
        if (e == null) yield break;
        var list = new UaNode("UAObject", "", "");

        if (e.Attribute("ID") != null) list.Ref("HasProperty", FormatRef(objectId + "_AML_ID", ns));
        // D2: an unprefixed test in the XSLT, true only in CAEX 2.15 documents.
        if (UnprefixedKids(e, "Version").Any()) list.Ref("HasProperty", FormatRef(objectId + "_Version", ns));
        foreach (var info in Kids(e, "AdditionalInformation"))
            list.Ref("HasComponent", FormatRef(objectId + "_" + NumberedElementName(info), ns));
        // D12: the XSLT creates Copyright properties without a reference to them.
        if (!_compat && Kids(e, "Copyright").Any()) list.Ref("HasProperty", FormatRef(objectId + "_Copyright", ns));
        foreach (var constraint in Kids(e, "Constraint"))
            list.Ref("HasAMLConstraint", FormatRef(objectId + "_" + NumberedElementName(constraint), ns));
        foreach (var requirement in e.Elements().Elements().Where(r => RequirementKinds.Contains(L(r))))
            list.Ref("HasAMLRequirement", FormatRef(objectId + "_" + NumberedElementName(requirement), ns));
        foreach (var attribute in Kids(e, "Attribute"))
            list.Ref("HasComponent", FormatRef(objectId + "_" + RemoveSpace(Attr(attribute, "Name")), ns));
        foreach (var refSemantic in Kids(e, "RefSemantic"))
            list.Ref("HasAMLReferenceType", FormatRef(objectId + RefSemanticSuffix(refSemantic), ns));

        if (e.Attribute("RefBaseSystemUnitPath") is { } refBaseSystemUnitPath)
        {
            var path = refBaseSystemUnitPath.Value;
            var sucLibName = Before(path, "/");
            var sucAliasName = After(path, "@");
            var sucContent = sucLibName != "" ? GetClass(path) : ClassLookup.None;
            var mirror = IsMirrorPath(path) ? path : "";
            if (sucContent.ClassName("SystemUnitClass") is { } sucName)
                list.Ref("HasTypeDefinition", FormatRef(sucName, NamespaceIdByName(sucLibName)));
            else if (sucLibName.Contains('@'))
                list.Ref("HasTypeDefinition", sucAliasName);
            else if (mirror != "")
                list.Ref("IsAMLMirroredAs", FormatRef(mirror, ns));
        }
        else if (L(e) == "InternalElement")
        {
            list.Ref("HasTypeDefinition", "CAEXObjectType");
        }

        foreach (var role in e.Elements().Where(c => L(c) is "SupportedRoleClass" or "RoleRequirements"))
        {
            var refBase = role.Attribute("RefBaseRoleClassPath")?.Value;
            var refRole = role.Attribute("RefRoleClassPath")?.Value;
            var rcLibName = Before(refBase ?? "", "/") + Before(refRole ?? "", "/");
            var rcAliasName = After(refBase ?? "", "@") + After(refRole ?? "", "@");
            const string baseRole = "AutomationMLBaseRoleClassLib/AutomationMLBaseRole";
            var rcContent = new List<ClassLookup>();
            if (rcLibName != "" && refRole != null && refRole != baseRole) rcContent.Add(GetClass(refRole));
            if (rcLibName != "" && refBase != null && refBase != baseRole) rcContent.Add(GetClass(refBase));
            var libNsId = NamespaceIdByName(rcLibName);
            if (rcContent.Select(c => c.ClassName("RoleClass")).FirstOrDefault(n => n != null) is { } roleName)
                list.Ref("HasAMLRoleReference", FormatRef(roleName, libNsId));
            else if (rcLibName.Contains('@'))
                list.Ref("HasAMLRoleReference", rcAliasName);
        }

        foreach (var ei in Kids(e, "ExternalInterface")) list.Ref("HasComponent", FormatRef(Attr(ei, "ID"), ns));
        foreach (var ie in Kids(e, "InternalElement")) list.Ref("HasComponent", FormatRef(Attr(ie, "ID"), ns));

        foreach (var r in list.References) yield return r;
    }
}
