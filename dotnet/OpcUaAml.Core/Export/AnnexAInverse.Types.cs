// The inverse of Annex A: ReferenceTypes, DataTypes, and ObjectTypes and
// VariableTypes with the declarations they hold.

using System.Globalization;
using System.Xml.Linq;
using OpcUaAml.Addressing;
using OpcUaAml.NodeSets;

namespace OpcUaAml.Export;

public static partial class AnnexAInverse
{
    private sealed partial class Writer
    {
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
    }
}
