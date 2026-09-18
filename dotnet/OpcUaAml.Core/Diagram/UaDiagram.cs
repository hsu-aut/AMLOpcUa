// A diagram of a UA type or instance in the graphical notation of
// OPC 10000-3 (node classes by shape), built from its Annex A representation:
// the element and its children become nodes, the references between them
// edges. Layout and SVG output live here so the plugin view and the export
// draw exactly the same picture.

using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using OpcUaAml.Types;

namespace OpcUaAml.Diagram;

/// <summary>Node classes with a shape of their own in OPC 10000-3.</summary>
public enum UaNodeKind { Object, Variable, Method, ObjectType, VariableType }

public sealed class DiagramNode
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required UaNodeKind Kind { get; init; }

    /// <summary>The type the node instantiates (its HasTypeDefinition), by name.</summary>
    public string? TypeName { get; init; }

    /// <summary>For a type: its supertype (HasSubtype, inverse), by name.</summary>
    public string? SupertypeName { get; init; }

    /// <summary>M, O, MP, OP or E for ExposesItsArray; null on instances.</summary>
    public string? ModellingRule { get; init; }

    public int Depth { get; init; }

    // Layout, filled by DiagramLayout.
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

public sealed record DiagramEdge(string From, string To, string ReferenceType, bool Hierarchical);

public sealed class UaDiagram
{
    public required string Title { get; init; }
    public List<DiagramNode> Nodes { get; } = new();
    public List<DiagramEdge> Edges { get; } = new();
    public bool Truncated { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

public static class DiagramBuilder
{
    /// <summary>
    /// A diagram of a UA type (a SystemUnitClass of an Annex A library) with
    /// its instance declarations, or of an instance with its children.
    /// </summary>
    public static UaDiagram Build(SystemUnitClassType root, int maxDepth = 3, int maxNodes = 300)
    {
        var isType = root is SystemUnitFamilyType;
        var diagram = new UaDiagram { Title = root.Name };
        var ids = new Dictionary<SystemUnitClassType, string>();
        var interfaceOwner = new Dictionary<string, string>(StringComparer.Ordinal);

        var rootNode = new DiagramNode
        {
            Id = Key(root),
            Name = root.Name,
            Kind = isType ? TypeKind((SystemUnitFamilyType)root) : KindOf(root),
            TypeName = isType ? null : TypeName(root),
            SupertypeName = isType ? BaseName(((SystemUnitFamilyType)root).RefBaseClassPath) : null,
            Depth = 0,
        };
        diagram.Nodes.Add(rootNode);
        ids[root] = rootNode.Id;
        foreach (var ei in root.ExternalInterface) interfaceOwner[ei.ID] = rootNode.Id;

        // Inherited declarations are shown too: a type's picture is what an
        // instance of it contains.
        var children = isType
            ? UaTypes.Declarations((SystemUnitFamilyType)root).Select(d => (SystemUnitClassType)d.Element).ToList()
            : root.InternalElement.Cast<SystemUnitClassType>().ToList();
        foreach (var child in children) Add(child, rootNode, 1);

        // Non-hierarchical references: InternalLinks whose ends are not a
        // parent and its child.
        foreach (var owner in new[] { root }.Concat(root.Descendants<InternalElementType>()))
        {
            foreach (var link in owner.InternalLink)
            {
                if (!interfaceOwner.TryGetValue(link.RefPartnerSideA, out var a) ||
                    !interfaceOwner.TryGetValue(link.RefPartnerSideB, out var b) || a == b) continue;
                if (diagram.Edges.Any(e => (e.From == a && e.To == b) || (e.From == b && e.To == a))) continue;
                diagram.Edges.Add(new DiagramEdge(a, b, ReferenceOf(owner.CAEXDocument, link.RefPartnerSideA) ?? "References", false));
            }
        }
        return diagram;

        void Add(SystemUnitClassType element, DiagramNode parent, int depth)
        {
            if (depth > maxDepth) return;
            if (diagram.Nodes.Count >= maxNodes) { diagram.Truncated = true; return; }
            var rule = UaTypes.RuleOf(element);
            var node = new DiagramNode
            {
                Id = Key(element) + "#" + diagram.Nodes.Count,
                Name = element.Name,
                Kind = KindOf(element),
                TypeName = KindOf(element) == UaNodeKind.Method ? null : TypeName(element),
                ModellingRule = rule switch
                {
                    ModellingRule.Mandatory => "M",
                    ModellingRule.Optional => "O",
                    ModellingRule.MandatoryPlaceholder => "MP",
                    ModellingRule.OptionalPlaceholder => "OP",
                    ModellingRule.ExposesItsArray => "E",
                    _ => null,
                },
                Depth = depth,
            };
            diagram.Nodes.Add(node);
            ids[element] = node.Id;
            foreach (var ei in element.ExternalInterface) interfaceOwner[ei.ID] = node.Id;
            diagram.Edges.Add(new DiagramEdge(parent.Id, node.Id, HierarchicalReference(element), true));
            foreach (var child in element.InternalElement) Add(child, node, depth + 1);
        }
    }

    private static string Key(CAEXObject o) => string.IsNullOrEmpty(o.ID) ? o.Name : o.ID;

    /// <summary>
    /// The reference that attaches a child to its parent, read from the
    /// child's end: an interface of class e.g. [HasComponent]/[ComponentOf]
    /// belongs to HasComponent.
    /// </summary>
    private static string HierarchicalReference(SystemUnitClassType child)
    {
        foreach (var ei in child.ExternalInterface)
        {
            var parts = Segments(ei.RefBaseClassPath);
            if (parts.Count >= 2 && parts[0].StartsWith("ICL_", StringComparison.Ordinal))
                return parts.Count >= 3 ? parts[^2] : parts[^1];
        }
        return "HasComponent";
    }

    private static string? ReferenceOf(CAEXDocument doc, string interfaceId)
    {
        if (doc.FindByID(interfaceId, true, null) is not ExternalInterfaceType ei) return null;
        var parts = Segments(ei.RefBaseClassPath);
        return parts.Count >= 2 ? parts[^1] : null;
    }

    private static UaNodeKind KindOf(SystemUnitClassType element)
    {
        var type = element is InternalElementType ie ? UaTypes.TypeOf(ie) : null;
        if (type == null) return UaNodeKind.Object;
        if (type.Name == "UaMethodNodeClass" || UaTypes.Chain(type).Any(t => t.Name == "UaMethodNodeClass")) return UaNodeKind.Method;
        return UaTypes.Chain(type).Any(t => t.Name == "BaseVariableType") ? UaNodeKind.Variable : UaNodeKind.Object;
    }

    private static UaNodeKind TypeKind(SystemUnitFamilyType type) =>
        UaTypes.Chain(type).Any(t => t.Name == "BaseVariableType") ? UaNodeKind.VariableType : UaNodeKind.ObjectType;

    private static string? TypeName(SystemUnitClassType element) =>
        element is InternalElementType ie ? BaseName(ie.RefBaseSystemUnitPath) : null;

    private static string? BaseName(string? path) => path == null ? null : Segments(path).LastOrDefault();

    /// <summary>"[Lib]/[A]/[B]" or "Lib/A/B" into its segments; namespace URIs in brackets keep their slashes.</summary>
    internal static List<string> Segments(string? path)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(path)) return result;
        if (path.StartsWith('['))
        {
            foreach (var part in path.Split("]/", StringSplitOptions.None))
                result.Add(part.Trim('[', ']'));
        }
        else
        {
            result.AddRange(path.Split('/'));
        }
        return result;
    }
}
