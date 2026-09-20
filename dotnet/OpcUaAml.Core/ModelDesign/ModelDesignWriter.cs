// A NodeSet as a ModelDesign file (http://opcfoundation.org/UA/ModelDesign.xsd),
// the form the OPC Foundation's ModelCompiler reads. The compiler goes the
// other way round, from ModelDesign to NodeSet, and keeps no design of its
// own; this writer closes the circle, so a model built here or in the modeler
// can be taken into a workflow that generates code from a design.
//
// What it writes: the model's own ObjectTypes, VariableTypes, DataTypes,
// ReferenceTypes with their instance declarations, arguments, fields and
// references, and the objects the model puts into the address space. The
// NodeIds go into the identifier file (CSV) beside the design, keyed by the
// symbolic path the compiler builds, which is where the OPC Foundation's own
// models keep them; so a compiled design has the same ids as the NodeSet it
// came from.

using System.Globalization;
using System.Xml.Linq;

namespace OpcUaAml.ModelDesign;

public sealed class ModelDesignOptions
{
    /// <summary>The model to write when the NodeSet defines several; the first one otherwise.</summary>
    public string? NamespaceUri { get; init; }

    /// <summary>The name and prefix of the target namespace; derived from its URI when absent.</summary>
    public string? ModelName { get; init; }
}

/// <summary>A design and the identifier file that gives its nodes their NodeIds.</summary>
public sealed record ModelDesignResult(XDocument Design, string Identifiers)
{
    /// <summary>
    /// Writes the design, and the identifier file beside it (same name, .csv).
    /// Returns the identifier file, or null when the model has no numeric
    /// NodeId to keep: an empty file would tell the compiler that the ids are
    /// settled when they are not.
    /// </summary>
    public string? Save(string designPath)
    {
        var identifiers = Path.ChangeExtension(designPath, ".csv");
        // A design named "x.csv" would have the identifier file overwrite it.
        if (string.Equals(identifiers, designPath, StringComparison.OrdinalIgnoreCase)) identifiers = designPath + ".csv";
        Design.Save(designPath);
        if (Identifiers.Length == 0)
        {
            if (File.Exists(identifiers)) File.Delete(identifiers);
            return null;
        }
        File.WriteAllText(identifiers, Identifiers);
        return identifiers;
    }
}

public static class ModelDesignWriter
{
    public const string DesignNamespace = "http://opcfoundation.org/UA/ModelDesign.xsd";
    public const string UaNamespace = "http://opcfoundation.org/UA/";

    private static readonly XNamespace Opc = DesignNamespace;
    private static readonly XNamespace Ua = "http://opcfoundation.org/UA/2011/03/UANodeSet.xsd";

    /// <summary>The ModelDesign of one model of the NodeSet file.</summary>
    /// <exception cref="InvalidDataException">The file is no NodeSet, or holds no such model.</exception>
    public static ModelDesignResult FromFile(string nodeSetPath, ModelDesignOptions? options = null) =>
        From(SafeXml.Load(nodeSetPath, LoadOptions.None), options);

    /// <summary>The ModelDesign of one model of the NodeSet.</summary>
    /// <exception cref="InvalidDataException">The document is no NodeSet, or holds no such model.</exception>
    public static ModelDesignResult From(XDocument nodeSet, ModelDesignOptions? options = null)
    {
        options ??= new ModelDesignOptions();
        if (nodeSet.Root?.Name != Ua + "UANodeSet") throw new InvalidDataException("The file is not a UANodeSet.");
        var model = new Model(nodeSet, options);
        var design = model.Write();
        return new ModelDesignResult(design, model.Identifiers());
    }

    /// <summary>A short name for a namespace URI, usable as a prefix ("DI" for .../UA/DI/).</summary>
    public static string NameOf(string namespaceUri)
    {
        var parts = namespaceUri.Split(['/', ':'], StringSplitOptions.RemoveEmptyEntries);
        var last = parts.LastOrDefault(p => !p.Contains('.') || p.Length > 4) ?? "Model";
        var name = new string(last.Where(char.IsLetterOrDigit).ToArray());
        if (name.Length == 0 || char.IsDigit(name[0])) name = "Model" + name;
        return name;
    }

    /// <summary>One model of one NodeSet, while it is being written.</summary>
    private sealed class Model
    {
        private readonly XDocument _nodeSet;
        private readonly ModelDesignOptions _options;
        private readonly List<string> _namespaces = [];               // by index, as the file lists them
        private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
        private readonly Dictionary<string, XElement> _nodes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Reference>> _outgoing = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Reference>> _incoming = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _prefixes = new(StringComparer.Ordinal);  // namespace URI to prefix
        private readonly List<(string Symbol, string Identifier, string NodeClass)> _identifiers = [];
        // Where each node goes and what it is called there, decided before
        // anything is written: its name among its siblings, its symbolic path
        // (the key of the identifier file), and the node that holds it.
        private readonly Dictionary<string, string> _symbolOf = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _pathOf = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _holderOf = new(StringComparer.Ordinal);
        private readonly HashSet<string> _paths = new(StringComparer.Ordinal);
        private string _target = "";

        public Model(XDocument nodeSet, ModelDesignOptions options)
        {
            _nodeSet = nodeSet;
            _options = options;
        }

        private sealed record Reference(string Type, string Target, bool IsForward);

        public XDocument Write()
        {
            Read();
            var root = new XElement(Opc + "ModelDesign",
                new XAttribute(XNamespace.Xmlns + "opc", DesignNamespace),
                new XAttribute(XNamespace.Xmlns + "ua", UaNamespace),
                new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"),
                new XAttribute("xmlns", _target),
                new XAttribute("TargetNamespace", _target),
                new XAttribute("TargetXmlNamespace", _target));

            var model = _nodeSet.Root!.Element(Ua + "Models")?.Elements(Ua + "Model")
                .FirstOrDefault(m => (string?)m.Attribute("ModelUri") == _target);
            if ((string?)model?.Attribute("Version") is { Length: > 0 } version) root.Add(new XAttribute("TargetVersion", version));
            if ((string?)model?.Attribute("PublicationDate") is { Length: > 0 } date) root.Add(new XAttribute("TargetPublicationDate", date));
            foreach (var (uri, prefix) in _prefixes.Where(p => p.Key != _target && p.Key != UaNamespace))
                root.Add(new XAttribute(XNamespace.Xmlns + prefix, uri));

            root.Add(Namespaces(model));
            foreach (var node in Plan()) root.Add(Node(node));
            return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        }

        /// <summary>Index of the file: namespaces, aliases, nodes and references in both directions.</summary>
        private void Read()
        {
            _namespaces.Add(UaNamespace);
            _namespaces.AddRange(_nodeSet.Root!.Element(Ua + "NamespaceUris")?.Elements(Ua + "Uri").Select(u => u.Value.Trim()) ?? []);
            foreach (var alias in _nodeSet.Root.Element(Ua + "Aliases")?.Elements(Ua + "Alias") ?? [])
                _aliases[alias.Value.Trim()] = (string)alias.Attribute("Alias")!;

            _target = _options.NamespaceUri
                      ?? (string?)_nodeSet.Root.Element(Ua + "Models")?.Elements(Ua + "Model").FirstOrDefault()?.Attribute("ModelUri")
                      ?? _namespaces.Skip(1).FirstOrDefault()
                      ?? throw new InvalidDataException("The NodeSet declares no model of its own.");
            if (!_namespaces.Contains(_target))
                throw new InvalidDataException($"The NodeSet holds no nodes of '{_target}'.");

            _prefixes[_target] = "";
            if (!_prefixes.ContainsKey(UaNamespace)) _prefixes[UaNamespace] = "ua";
            var others = 0;
            foreach (var uri in _namespaces.Where(u => !_prefixes.ContainsKey(u)))
                _prefixes[uri] = $"ns{++others}";

            foreach (var node in _nodeSet.Root.Elements().Where(e => e.Name.LocalName.StartsWith("UA", StringComparison.Ordinal)))
            {
                if ((string?)node.Attribute("NodeId") is not { Length: > 0 } id) continue;
                _nodes[id] = node;
                foreach (var r in node.Element(Ua + "References")?.Elements(Ua + "Reference") ?? [])
                {
                    var type = Resolve((string?)r.Attribute("ReferenceType"));
                    var forward = (string?)r.Attribute("IsForward") != "false";
                    var other = r.Value.Trim();
                    if (type is null || other.Length == 0) continue;
                    Add(_outgoing, id, new Reference(type, other, forward));
                    Add(_incoming, other, new Reference(type, id, forward));
                }
            }
        }

        private static void Add(Dictionary<string, List<Reference>> map, string key, Reference reference)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            list.Add(reference);
        }

        /// <summary>An alias resolved to the NodeId it stands for.</summary>
        private string? Resolve(string? idOrAlias)
        {
            if (idOrAlias is not { Length: > 0 }) return null;
            var alias = _aliases.FirstOrDefault(a => a.Value == idOrAlias).Key;
            return alias ?? idOrAlias;
        }

        private XElement Namespaces(XElement? model)
        {
            var name = _options.ModelName ?? NameOf(_target);
            var target = new XElement(Opc + "Namespace",
                new XAttribute("Name", name), new XAttribute("Prefix", name), new XAttribute("XmlPrefix", name), _target);
            if ((string?)model?.Attribute("Version") is { Length: > 0 } version) target.SetAttributeValue("Version", version);
            if ((string?)model?.Attribute("PublicationDate") is { Length: > 0 } date) target.SetAttributeValue("PublicationDate", date);

            var namespaces = new XElement(Opc + "Namespaces", target);
            // The base model, unless the model being written is the base model.
            if (_target != UaNamespace)
            {
                namespaces.Add(new XElement(Opc + "Namespace",
                    new XAttribute("Name", "OpcUa"), new XAttribute("Prefix", "Opc.Ua"),
                    new XAttribute("InternalPrefix", "Opc.Ua.Server"),
                    new XAttribute("XmlNamespace", "http://opcfoundation.org/UA/2008/02/Types.xsd"),
                    new XAttribute("XmlPrefix", "OpcUa"), UaNamespace));
            }
            foreach (var uri in _namespaces.Where(u => u != _target && u != UaNamespace))
            {
                var other = NameOf(uri);
                namespaces.Add(new XElement(Opc + "Namespace",
                    new XAttribute("Name", other), new XAttribute("Prefix", other), new XAttribute("XmlPrefix", other), uri));
            }
            return namespaces;
        }

        /// <summary>The model's own nodes that stand on their own: types first, then objects and variables.</summary>
        private IEnumerable<XElement> Ordered()
        {
            var own = _nodes.Values.Where(n => Own((string)n.Attribute("NodeId")!)).ToList();
            var order = new[] { "UAReferenceType", "UADataType", "UAVariableType", "UAObjectType", "UAObject", "UAVariable", "UAMethod", "UAView" };
            // The compiler resolves a BaseType while it reads, so a type must
            // stand after the type it derives from.
            return own.Where(Standalone)
                .OrderBy(n => Array.IndexOf(order, n.Name.LocalName))
                .ThenBy(n => Chain(n).Count())
                .ThenBy(BrowseName, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Decides where every node goes and under which name before anything
        /// is written, so that two nodes never claim one symbolic path and
        /// every node of the model ends up somewhere: a reference must not
        /// point at a node the file does not hold.
        /// </summary>
        private List<XElement> Plan()
        {
            var roots = Ordered().ToList();
            foreach (var root in roots) Assign(root, null, null);
            // What no parent declares forward: a child held only by its own
            // inverse reference, or two nodes that hold each other. They stand
            // on their own rather than disappear.
            foreach (var node in _nodes.Values)
            {
                var id = (string)node.Attribute("NodeId")!;
                if (!Own(id) || _pathOf.ContainsKey(id) || Generated(node)) continue;
                roots.Add(node);
                Assign(node, null, null);
            }
            return roots;
        }

        private void Assign(XElement node, string? parentPath, string? parentId)
        {
            var id = (string)node.Attribute("NodeId")!;
            if (_pathOf.ContainsKey(id)) return;
            var name = Unique(Symbol(BrowseName(node)), parentPath);
            var path = parentPath is null ? name : $"{parentPath}_{name}";
            _symbolOf[id] = name;
            _pathOf[id] = path;
            _paths.Add(path);
            if (parentId != null) _holderOf[id] = parentId;
            foreach (var r in Outgoing(id).Where(r => r.IsForward && Hierarchical(r.Type)))
            {
                if (!_nodes.TryGetValue(r.Target, out var child) || !Own(r.Target) || Generated(child)) continue;
                Assign(child, path, id);
            }
        }

        /// <summary>A name no sibling has yet: two nodes may well share a BrowseName.</summary>
        private string Unique(string name, string? parentPath)
        {
            var candidate = name;
            for (var i = 2; _paths.Contains(parentPath is null ? candidate : $"{parentPath}_{candidate}"); i++)
                candidate = $"{name}_{i}";
            return candidate;
        }

        /// <summary>A node written on its own: a type, or an instance that no type declares.</summary>
        private bool Standalone(XElement node)
        {
            if (node.Name.LocalName is "UAObjectType" or "UAVariableType" or "UADataType" or "UAReferenceType") return true;
            var id = (string)node.Attribute("NodeId")!;
            if (Generated(node)) return false;                // the compiler writes it itself
            if (ModellingRule(id) != null) return false;      // an instance declaration, written inside its type
            return Parent(id) is null;                        // a child of an object is written inside that object
        }

        /// <summary>
        /// Nodes the ModelCompiler creates from the design itself: the encodings
        /// of a structure, the names of an enumeration, the arguments of a
        /// method. Writing them again would make the compiler write them twice.
        /// </summary>
        private bool Generated(XElement node)
        {
            var type = TypeDefinition((string)node.Attribute("NodeId")!);
            if (node.Name.LocalName == "UAObject")
                return type is "i=76" or "i=11616";       // an encoding, the namespace metadata
            return node.Name.LocalName == "UAVariable"
                   && (type is "i=72" or "i=69"           // the type dictionaries and their descriptions
                       || BrowseName(node) is "InputArguments" or "OutputArguments" or "EnumStrings" or "EnumValues" or "OptionSetValues");
        }

        private XElement Node(XElement node)
        {
            Note(Path(node), node);
            return node.Name.LocalName switch
            {
                "UAObjectType" => ObjectType(node),
                "UAVariableType" => VariableType(node),
                "UADataType" => DataType(node),
                "UAReferenceType" => ReferenceType(node),
                _ => Instance(node, holder: null),
            };
        }

        /// <summary>The symbolic path the plan gave the node.</summary>
        private string Path(XElement node) =>
            _pathOf.GetValueOrDefault((string)node.Attribute("NodeId")!) ?? Symbol(BrowseName(node));

        /// <summary>
        /// The identifier file the compiler reads: the symbolic path of a node,
        /// its identifier and its node class, as the OPC Foundation's models
        /// carry them. An identifier that is no number is left out; the
        /// compiler counts in numbers.
        /// </summary>
        public string Identifiers()
        {
            var text = new System.Text.StringBuilder();
            foreach (var (symbol, identifier, nodeClass) in _identifiers)
                text.Append(symbol).Append(',').Append(identifier).Append(',').Append(nodeClass).AppendLine();
            return text.ToString();
        }

        private void Note(string symbol, XElement node)
        {
            if (Numeric(node) is { } numeric) _identifiers.Add((symbol, numeric, NodeClass(node)));
        }

        /// <summary>The numeric identifier of a node, or null when its NodeId is a string or a GUID.</summary>
        private static string? Numeric(XElement node)
        {
            var part = Identifier(node);
            return part.StartsWith("i=", StringComparison.Ordinal) && uint.TryParse(part[2..], out var numeric)
                ? numeric.ToString(CultureInfo.InvariantCulture)
                : null;
        }

        /// <summary>A NodeId that is a string, which the design carries on the node itself.</summary>
        private static string? StringId(XElement node)
        {
            var part = Identifier(node);
            return part.StartsWith("s=", StringComparison.Ordinal) ? part[2..] : null;
        }

        private static string Identifier(XElement node)
        {
            var id = (string?)node.Attribute("NodeId") ?? "";
            return id[(id.IndexOf(';') + 1)..];
        }

        private static string NodeClass(XElement node) => node.Name.LocalName[2..];

        /// <summary>
        /// The identifiers of the nodes the compiler makes from a DataType: the
        /// names of an enumeration and the encodings of a structure. They carry
        /// the names the compiler gives them ("Vector_Encoding_DefaultBinary"),
        /// so the compiled model keeps the ids the NodeSet had.
        /// </summary>
        private void GeneratedIdentifiers(XElement dataType)
        {
            var id = (string)dataType.Attribute("NodeId")!;
            var path = Path(dataType);
            foreach (var r in Outgoing(id).Where(r => r.IsForward && r.Type == "i=46"))
            {
                if (_nodes.TryGetValue(r.Target, out var target)
                    && BrowseName(target) is "EnumStrings" or "EnumValues" or "OptionSetValues")
                {
                    Note(path + "_" + Symbol(BrowseName(target)), target);
                }
            }
            // An encoding names its DataType, not the other way round.
            foreach (var r in _incoming.GetValueOrDefault(id) ?? [])
            {
                if (r.Type != "i=38" || !_nodes.TryGetValue(r.Target, out var encoding)) continue;
                // The compiler spells them DefaultBinary, DefaultXml, DefaultJson.
                var name = BrowseName(encoding).Replace(" ", "") switch
                {
                    "DefaultXML" => "DefaultXml",
                    "DefaultJSON" => "DefaultJson",
                    var other => other,
                };
                Note($"{path}_Encoding_{Symbol(name)}", encoding);
            }
        }

        // ── types ────────────────────────────────────────────────────────────

        private XElement ObjectType(XElement node)
        {
            var design = new XElement(Opc + "ObjectType", Head(node), Supertype(node, "ua:BaseObjectType"));
            Fill(design, node);
            return design;
        }

        private XElement VariableType(XElement node)
        {
            var design = new XElement(Opc + "VariableType", Head(node), Supertype(node, "ua:BaseDataVariableType"));
            ValueAttributes(design, node);
            Fill(design, node);
            return design;
        }

        private XElement ReferenceType(XElement node)
        {
            var design = new XElement(Opc + "ReferenceType", Head(node), Supertype(node, "ua:NonHierarchicalReferences"));
            if ((string?)node.Attribute("Symmetric") == "true") design.SetAttributeValue("Symmetric", "true");
            References(design, node);
            // The schema puts InverseName after the references every node has.
            design.Add(Text(node, "InverseName"));
            return design;
        }

        private XElement DataType(XElement node)
        {
            var design = new XElement(Opc + "DataType", Head(node), Supertype(node, "ua:Structure"));
            Fill(design, node);
            var definition = node.Element(Ua + "Definition");
            if (definition is null) return design;

            GeneratedIdentifiers(node);
            if ((string?)definition.Attribute("IsOptionSet") == "true") design.SetAttributeValue("IsOptionSet", "true");
            if ((string?)definition.Attribute("IsUnion") == "true") design.SetAttributeValue("IsUnion", "true");
            var enumeration = Chain(node).Any(t => t is "i=29" or "i=12756");   // Enumeration, OptionSet
            var fields = new XElement(Opc + "Fields");
            foreach (var field in definition.Elements(Ua + "Field"))
            {
                var f = new XElement(Opc + "Field", new XAttribute("Name", (string)field.Attribute("Name")!));
                if (enumeration || (string?)field.Attribute("Value") is { Length: > 0 })
                {
                    if ((string?)field.Attribute("Value") is { Length: > 0 } value) f.SetAttributeValue("Identifier", value);
                }
                else
                {
                    f.SetAttributeValue("DataType", QName(Resolve((string?)field.Attribute("DataType")) ?? "i=24"));
                    if (Rank((string?)field.Attribute("ValueRank")) is { } rank) f.SetAttributeValue("ValueRank", rank);
                    if ((string?)field.Attribute("IsOptional") == "true") f.SetAttributeValue("IsOptional", "true");
                    if ((string?)field.Attribute("AllowSubTypes") == "true") f.SetAttributeValue("AllowSubTypes", "true");
                }
                f.Add(Text(field, "Description"));
                fields.Add(f);
            }
            if (fields.HasElements) design.Add(fields);
            return design;
        }

        // ── instances ────────────────────────────────────────────────────────

        /// <summary>An object, variable, property or method, as a child or on its own.</summary>
        private XElement Instance(XElement node, string? holder)
        {
            var id = (string)node.Attribute("NodeId")!;
            var property = holder == "i=46";
            var name = node.Name.LocalName switch
            {
                "UAMethod" => "Method",
                "UAVariable" when property => "Property",
                "UAVariable" => "Variable",
                "UAView" => "View",
                _ => "Object",
            };
            var design = new XElement(Opc + name, Head(node));
            if (TypeDefinition(id) is { } type && !(property && type == "i=68")) design.SetAttributeValue("TypeDefinition", QName(type));
            if (ModellingRule(id) is { } rule) design.SetAttributeValue("ModellingRule", rule);
            if (node.Name.LocalName is "UAVariable") ValueAttributes(design, node);
            Fill(design, node);
            // The schema puts these after the children and references: a child
            // held by anything but the usual reference names it, and a method
            // carries its arguments.
            if (holder is { } h && h != "i=47" && h != "i=46")
                design.Add(new XElement(Opc + "ReferenceType", QName(h)));
            if (node.Name.LocalName == "UAMethod") Arguments(design, id, Path(node));
            return design;
        }

        /// <summary>Children and references of a type or instance.</summary>
        private void Fill(XElement design, XElement node)
        {
            var id = (string)node.Attribute("NodeId")!;
            var children = new XElement(Opc + "Children");
            foreach (var r in Outgoing(id).Where(r => r.IsForward && Hierarchical(r.Type)))
            {
                // Only the children this node holds by the plan: a node two
                // parents hold belongs to one of them and is a reference in the other.
                if (!_nodes.TryGetValue(r.Target, out var child) || _holderOf.GetValueOrDefault(r.Target) != id) continue;
                Note(Path(child), child);
                children.Add(Instance(child, r.Type));
            }
            if (children.HasElements) design.Add(children);
            References(design, node);
        }

        /// <summary>References that no child and no supertype already carries.</summary>
        private void References(XElement design, XElement node)
        {
            var id = (string)node.Attribute("NodeId")!;
            var references = new XElement(Opc + "References");
            foreach (var r in Outgoing(id))
            {
                if (r.Type is "i=45" or "i=40" or "i=37") continue;                        // HasSubtype, HasTypeDefinition, HasModellingRule
                if (r.Type is "i=38" or "i=39") continue;                                  // HasEncoding, HasDescription: the compiler writes both
                if (_nodes.TryGetValue(r.Target, out var other) && Generated(other)) continue;
                // The child this node holds is written inside it, not beside it.
                if (r.IsForward && Hierarchical(r.Type) && _holderOf.GetValueOrDefault(r.Target) == id) continue;
                if (!r.IsForward && Hierarchical(r.Type) && _holderOf.GetValueOrDefault(id) == r.Target) continue;   // the parent holds us
                if (Own(r.Target) && !_pathOf.ContainsKey(r.Target)) continue;   // a node the design does not hold
                var target = Name(r.Target);
                if (target is null) continue;
                var reference = new XElement(Opc + "Reference",
                    new XElement(Opc + "ReferenceType", QName(r.Type)),
                    new XElement(Opc + "TargetId", target));
                if (!r.IsForward) reference.SetAttributeValue("IsInverse", "true");
                references.Add(reference);
            }
            if (references.HasElements) design.Add(references);
        }

        /// <summary>InputArguments and OutputArguments of a method, from the properties that hold them.</summary>
        private void Arguments(XElement design, string id, string path)
        {
            foreach (var which in new[] { "InputArguments", "OutputArguments" })
            {
                var property = Outgoing(id).Where(r => r.IsForward && r.Type == "i=46")
                    .Select(r => _nodes.GetValueOrDefault(r.Target))
                    .FirstOrDefault(n => n != null && BrowseName(n) == which);
                if (property is null) continue;
                Note(path + "_" + which, property);

                var list = new XElement(Opc + which);
                foreach (var argument in property.Element(Ua + "Value")?.Descendants()
                             .Where(e => e.Name.LocalName == "Argument") ?? [])
                {
                    var name = argument.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value ?? "";
                    var dataType = argument.Elements().FirstOrDefault(e => e.Name.LocalName == "DataType")
                        ?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Identifier")?.Value ?? "i=24";
                    var rank = argument.Elements().FirstOrDefault(e => e.Name.LocalName == "ValueRank")?.Value;
                    var a = new XElement(Opc + "Argument",
                        new XAttribute("Name", name), new XAttribute("DataType", QName(dataType.Trim())));
                    if (Rank(rank) is { } valueRank) a.SetAttributeValue("ValueRank", valueRank);
                    var description = argument.Elements().FirstOrDefault(e => e.Name.LocalName == "Description")
                        ?.Elements().FirstOrDefault(e => e.Name.LocalName == "Text")?.Value;
                    if (description is { Length: > 0 }) a.Add(new XElement(Opc + "Description", description));
                    list.Add(a);
                }
                if (list.HasElements) design.Add(list);
            }
        }

        // ── attributes shared by several kinds of node ────────────────────────

        private IEnumerable<object> Head(XElement node)
        {
            yield return new XAttribute("SymbolicName", Symbolic(node));
            if ((string?)node.Attribute("IsAbstract") == "true") yield return new XAttribute("IsAbstract", "true");
            // A NodeId that is a string has no place in the identifier file, which counts in numbers.
            if (StringId(node) is { } stringId) yield return new XAttribute("StringId", stringId);
            // A name the symbol could not keep is written out: one XML cannot
            // carry as a QName (<PlaceholderName>), or one a sibling had first.
            if (BrowseName(node) != Local(node)) yield return new XElement(Opc + "BrowseName", BrowseName(node));
            // A DisplayName that only repeats the name is left out; the compiler makes it.
            if (node.Element(Ua + "DisplayName")?.Value != BrowseName(node) && Text(node, "DisplayName") is { } display) yield return display;
            if (Text(node, "Description") is { } description) yield return description;
        }

        private void ValueAttributes(XElement design, XElement node)
        {
            if (Resolve((string?)node.Attribute("DataType")) is { } dataType) design.SetAttributeValue("DataType", QName(dataType));
            if (Rank((string?)node.Attribute("ValueRank")) is { } rank) design.SetAttributeValue("ValueRank", rank);
            if ((string?)node.Attribute("ArrayDimensions") is { Length: > 0 } dimensions) design.SetAttributeValue("ArrayDimensions", dimensions);
        }

        private XElement? Text(XElement node, string name)
        {
            var value = node.Element(Ua + name)?.Value;
            return value is { Length: > 0 } ? new XElement(Opc + name, value) : null;
        }

        private XAttribute? Supertype(XElement node, string fallback)
        {
            var id = (string)node.Attribute("NodeId")!;
            var super = Outgoing(id).FirstOrDefault(r => r.Type == "i=45" && !r.IsForward)?.Target;
            return new XAttribute("BaseType", super is null ? fallback : QName(super));
        }

        /// <summary>The node and its supertypes, as NodeIds.</summary>
        private IEnumerable<string> Chain(XElement node)
        {
            var id = (string?)node.Attribute("NodeId");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (id != null && seen.Add(id))
            {
                yield return id;
                id = Outgoing(id).FirstOrDefault(r => r.Type == "i=45" && !r.IsForward)?.Target;
            }
        }

        private static string? Rank(string? valueRank) => valueRank switch
        {
            null or "" or "-1" => null,
            "1" => "Array",
            "0" => "OneOrMoreDimensions",
            "-2" => "ScalarOrArray",
            "-3" => "ScalarOrOneDimension",
            // A design knows no matrix: it says "one or more dimensions", and
            // ArrayDimensions still carries how many there are.
            _ => "OneOrMoreDimensions",
        };

        // ── names and ids ────────────────────────────────────────────────────

        private static string BrowseName(XElement node)
        {
            var name = (string?)node.Attribute("BrowseName") ?? "";
            var colon = name.IndexOf(':');
            return colon < 0 ? name : name[(colon + 1)..];
        }

        /// <summary>
        /// The SymbolicName of a node: its BrowseName, prefixed when the name
        /// belongs to another namespace than the model, as a child called
        /// StateNumber or NodeVersion does ("ua:StateNumber").
        /// </summary>
        private string Symbolic(XElement node)
        {
            var name = Local(node);
            var full = (string?)node.Attribute("BrowseName") ?? "";
            var colon = full.IndexOf(':');
            // A BrowseName without an index belongs to the UA namespace, as
            // children called Id, Name or Size of a state machine do.
            var index = colon < 0 ? 0 : int.TryParse(full[..colon], out var i) ? i : 0;
            var space = index < _namespaces.Count ? _namespaces[index] : _target;
            var prefix = _prefixes.GetValueOrDefault(space, "");
            return prefix.Length == 0 ? name : $"{prefix}:{name}";
        }

        /// <summary>The name the plan gave the node among its siblings.</summary>
        private string Local(XElement node) =>
            _symbolOf.GetValueOrDefault((string)node.Attribute("NodeId")!) ?? Symbol(BrowseName(node));

        /// <summary>A BrowseName as a SymbolicName: the compiler needs a name XML can carry.</summary>
        private static string Symbol(string browseName)
        {
            var symbol = new string(browseName.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
            return symbol.Length == 0 || char.IsDigit(symbol[0]) ? "_" + symbol : symbol;
        }

        private List<Reference> Outgoing(string id) => _outgoing.GetValueOrDefault(id) ?? [];

        private bool Own(string nodeId) => NamespaceOf(nodeId) == _target;

        private string NamespaceOf(string nodeId)
        {
            if (!nodeId.StartsWith("ns=", StringComparison.Ordinal)) return UaNamespace;
            var end = nodeId.IndexOf(';');
            return int.TryParse(nodeId[3..(end < 0 ? nodeId.Length : end)], out var index) && index < _namespaces.Count
                ? _namespaces[index]
                : UaNamespace;
        }

        /// <summary>The QName a design uses for a node: "ua:HasComponent", "Own", "ns1:Other".</summary>
        private string QName(string nodeId) => Name(nodeId) ?? nodeId;

        private string? Name(string nodeId)
        {
            var space = NamespaceOf(nodeId);
            // A reference names its target by the symbolic id, which for a child
            // is the path through its parents ("PumpType_Start"), not its name.
            if (_pathOf.TryGetValue(nodeId, out var path)) return path;
            if (_nodes.TryGetValue(nodeId, out var node)) return Symbolic(node);
            var name = space == UaNamespace ? UaNames.Of(nodeId) : null;
            if (name is null) return null;
            var prefix = _prefixes.GetValueOrDefault(space, "");
            return prefix.Length == 0 ? name : $"{prefix}:{name}";
        }

        private string? TypeDefinition(string id) =>
            Outgoing(id).FirstOrDefault(r => r.Type == "i=40" && r.IsForward)?.Target;

        private string? ModellingRule(string id) =>
            Outgoing(id).FirstOrDefault(r => r.Type == "i=37" && r.IsForward)?.Target switch
            {
                "i=78" => "Mandatory",
                "i=80" => "Optional",
                "i=11508" => "MandatoryPlaceholder",
                "i=11510" => "OptionalPlaceholder",
                "i=83" => "ExposesItsArray",
                _ => null,
            };

        private string? Parent(string id) =>
            _incoming.GetValueOrDefault(id)?.FirstOrDefault(r => r.IsForward && Hierarchical(r.Type) && Own(r.Target))?.Target;

        /// <summary>HasComponent, HasProperty, HasOrderedComponent: the references that hold a child.</summary>
        private static bool Hierarchical(string type) => type is "i=47" or "i=46" or "i=49";
    }
}
