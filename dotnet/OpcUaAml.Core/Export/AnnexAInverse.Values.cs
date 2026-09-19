// The inverse of Annex A: the values of variables, from built-in types to
// structures, enumerations, option sets and arguments.

using System.Globalization;
using System.Xml.Linq;
using OpcUaAml.Addressing;
using OpcUaAml.NodeSets;

namespace OpcUaAml.Export;

public static partial class AnnexAInverse
{
    private sealed partial class Writer
    {
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
    }
}
