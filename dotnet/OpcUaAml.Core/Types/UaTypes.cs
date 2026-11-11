// Reading the OPC UA meaning out of Annex A libraries: which SystemUnitClass an
// element instantiates, whether a type is abstract, and which ModellingRule a
// child declaration carries.

using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;

namespace OpcUaAml.Types;

/// <summary>The ModellingRules of OPC 10000-3, as Annex A spells them.</summary>
public enum ModellingRule
{
    None,
    Mandatory,
    Optional,
    MandatoryPlaceholder,
    OptionalPlaceholder,
    ExposesItsArray,
}

/// <summary>A child declaration of a type: its name, rule and type path.</summary>
public sealed record ChildDeclaration(string Name, ModellingRule Rule, string? TypePath, InternalElementType Element)
{
    public bool IsPlaceholder => Rule is ModellingRule.MandatoryPlaceholder or ModellingRule.OptionalPlaceholder;

    /// <summary>
    /// The declarations below this one, inherited ones included: what every
    /// declaration of this BrowseName along the type chain holds, the nearest
    /// first, merged the same way further down (OPC 10000-3, the fully
    /// inherited instance declaration hierarchy). An overriding declaration
    /// that does not repeat a child of the one it overrides still has it here.
    /// </summary>
    public IReadOnlyList<ChildDeclaration> Children { get; init; } = Array.Empty<ChildDeclaration>();

    /// <summary>The declarations of the same BrowseName this one overrides, the nearest first.</summary>
    public IReadOnlyList<InternalElementType> Overridden { get; init; } = Array.Empty<InternalElementType>();

    /// <summary>The type or declaration that holds <see cref="Element"/>, with the links that attach it.</summary>
    internal SystemUnitClassType? Owner { get; init; }
}

public static class UaTypes
{
    public const string ModellingRuleAttribute = "ModellingRule";

    /// <summary>A system unit class library generated from a UA namespace.</summary>
    public static bool IsUaLibraryPath(string? path) =>
        path != null && path.StartsWith("[SUC_", StringComparison.Ordinal);

    public static SystemUnitFamilyType? Resolve(CAEXDocument doc, string? path) =>
        string.IsNullOrEmpty(path) ? null : doc.FindByPath(path) as SystemUnitFamilyType;

    /// <summary>The type an element instantiates, or null if its path does not resolve.</summary>
    public static SystemUnitFamilyType? TypeOf(InternalElementType element) =>
        element.CAEXDocument is { } doc ? Resolve(doc, element.RefBaseSystemUnitPath) : null;

    /// <summary>The type and its supertypes, most derived first.</summary>
    public static IEnumerable<SystemUnitFamilyType> Chain(SystemUnitFamilyType type)
    {
        var seen = new HashSet<SystemUnitFamilyType>();
        for (var t = type; t != null && seen.Add(t); t = Resolve(t.CAEXDocument, t.RefBaseClassPath))
            yield return t;
    }

    public static bool IsAbstract(SystemUnitFamilyType type) =>
        string.Equals(type.Attribute["IsAbstract"]?.Value, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The rule a child declaration carries. Annex A puts it on the child's end
    /// of the reference that attaches it to its parent (the destination
    /// interface, e.g. <c>ComponentOf</c>); a child without one is not an
    /// instance declaration and counts as <see cref="ModellingRule.None"/>.
    /// </summary>
    public static ModellingRule RuleOf(SystemUnitClassType child)
    {
        foreach (var ei in child.ExternalInterface)
        {
            var value = ei.Attribute[ModellingRuleAttribute]?.Value;
            if (!string.IsNullOrEmpty(value) && Enum.TryParse<ModellingRule>(value, out var rule)) return rule;
        }
        return ModellingRule.None;
    }

    /// <summary>How deep declarations below declarations are followed; real models stay far below.</summary>
    private const int MaxDeclarationDepth = 12;

    /// <summary>
    /// The child declarations of a type including inherited ones; a declaration
    /// in a subtype overrides the one of the same name in a supertype. Each
    /// carries what is declared below it, inherited declarations included
    /// (<see cref="ChildDeclaration.Children"/>): the tree an instance of the
    /// type is made from, and checked and brought up to date against.
    /// </summary>
    public static IReadOnlyList<ChildDeclaration> Declarations(SystemUnitFamilyType type) =>
        Collect(Chain(type).Cast<SystemUnitClassType>().ToList(), 0);

    /// <summary>
    /// What several owners of one place declare, the nearest owner first: per
    /// BrowseName the nearest declaration stands, and what all of them hold
    /// below is collected the same way.
    /// </summary>
    private static IReadOnlyList<ChildDeclaration> Collect(IReadOnlyList<SystemUnitClassType> owners, int depth)
    {
        var groups = new Dictionary<string, List<(SystemUnitClassType Owner, InternalElementType Element)>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var owner in owners)
        {
            foreach (var child in owner.InternalElement)
            {
                // A BrowseName is unique with its namespace: a type may hold
                // its own NodeVersion beside the one of the UA namespace, and
                // only a declaration of the same namespace overrides another.
                var key = QualifiedName(child);
                if (!groups.TryGetValue(key, out var group))
                {
                    groups[key] = group = new();
                    order.Add(key);
                }
                group.Add((owner, child));
            }
        }
        return order.Select(key =>
        {
            var group = groups[key];
            var (owner, element) = group[0];
            return new ChildDeclaration(element.Name, RuleOf(element), element.RefBaseSystemUnitPath, element)
            {
                Owner = owner,
                Overridden = group.Skip(1).Select(g => g.Element).ToList(),
                Children = depth + 1 >= MaxDeclarationDepth
                    ? Array.Empty<ChildDeclaration>()
                    : Collect(group.Select(g => (SystemUnitClassType)g.Element).ToList(), depth + 1),
            };
        }).ToList();
    }

    /// <summary>
    /// Two sets of declarations for one place, <paramref name="nearer"/>
    /// standing where both declare a BrowseName, and what they hold below
    /// merged the same way: what a parent's type declares below a child over
    /// what the child's own type declares.
    /// </summary>
    internal static IReadOnlyList<ChildDeclaration> Merge(IReadOnlyList<ChildDeclaration> nearer, IReadOnlyList<ChildDeclaration> farther)
    {
        if (farther.Count == 0) return nearer;
        if (nearer.Count == 0) return farther;
        var result = nearer.ToList();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < result.Count; i++) index.TryAdd(QualifiedName(result[i].Element), i);
        foreach (var declaration in farther)
        {
            var key = QualifiedName(declaration.Element);
            if (index.TryGetValue(key, out var i))
            {
                result[i] = result[i] with
                {
                    Children = Merge(result[i].Children, declaration.Children),
                    Overridden = result[i].Overridden.Append(declaration.Element).Concat(declaration.Overridden).ToList(),
                };
            }
            else
            {
                index[key] = result.Count;
                result.Add(declaration);
            }
        }
        return result;
    }

    /// <summary>
    /// The declarations that apply to an element of an instance: what its
    /// parent's type declares below it (<paramref name="declared"/>, see
    /// <see cref="Below"/>) over what its own UA type declares.
    /// </summary>
    internal static IReadOnlyList<ChildDeclaration> Applying(InternalElementType element, IReadOnlyList<ChildDeclaration>? declared)
    {
        var type = IsUaLibraryPath(element.RefBaseSystemUnitPath) ? TypeOf(element) : null;
        return Merge(declared ?? Array.Empty<ChildDeclaration>(), type == null ? Array.Empty<ChildDeclaration>() : Declarations(type));
    }

    /// <summary>
    /// What the declaration a child of an instance stands for declares below
    /// it, found by name among the declarations that apply to its parent. A
    /// child that fills a placeholder stands for no declaration of its own.
    /// </summary>
    internal static IReadOnlyList<ChildDeclaration>? Below(IReadOnlyList<ChildDeclaration> applying, InternalElementType child)
    {
        // Two declarations may share a name in different namespaces; the
        // child's BrowseName, where it has one, tells them apart.
        var named = applying.Where(d => !d.IsPlaceholder && d.Name == child.Name).ToList();
        var qualified = QualifiedName(child);
        return (named.FirstOrDefault(d => QualifiedName(d.Element) == qualified) ?? named.FirstOrDefault())?.Children;
    }

    /// <summary>The type whose library holds a declaration, at any depth below it.</summary>
    internal static SystemUnitFamilyType? DeclaringType(ChildDeclaration declaration)
    {
        for (CAEXBasicObject? o = declaration.Element; o is CAEXObject c; o = c.CAEXParent as CAEXBasicObject)
            if (c is SystemUnitFamilyType type) return type;
        return null;
    }

    /// <summary>The BrowseName of a child with the namespace Annex A writes beside it.</summary>
    public static string QualifiedName(SystemUnitClassType child)
    {
        var uri = child.Attribute["BrowseName"]?.Attribute["NamespaceUri"]?.Value;
        return string.IsNullOrEmpty(uri) ? child.Name : uri + "|" + child.Name;
    }

    /// <summary>Whether <paramref name="type"/> is <paramref name="ancestor"/> or derives from it.</summary>
    public static bool DerivesFrom(SystemUnitFamilyType type, SystemUnitFamilyType ancestor) =>
        Chain(type).Any(t => ReferenceEquals(t, ancestor) || t.ID == ancestor.ID);

    /// <summary>All UA types of the document, as (path, class).</summary>
    public static IEnumerable<(string Path, SystemUnitFamilyType Type)> AllTypes(CAEXDocument doc)
    {
        foreach (var lib in doc.CAEXFile.SystemUnitClassLib)
        {
            if (!lib.Name.StartsWith("SUC_", StringComparison.Ordinal)) continue;
            foreach (var suc in lib.SystemUnitClass.SelectMany(s => s.DescendantsAndSelf()))
                yield return (suc.CAEXPath(), suc);
        }
    }

    private static IEnumerable<SystemUnitFamilyType> DescendantsAndSelf(this SystemUnitFamilyType suc) =>
        new[] { suc }.Concat(suc.SystemUnitClass.SelectMany(c => c.DescendantsAndSelf()));
}
