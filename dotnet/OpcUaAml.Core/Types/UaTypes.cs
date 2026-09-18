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

    /// <summary>
    /// The child declarations of a type including inherited ones; a declaration
    /// in a subtype overrides the one of the same name in a supertype.
    /// </summary>
    public static IReadOnlyList<ChildDeclaration> Declarations(SystemUnitFamilyType type)
    {
        var byName = new Dictionary<string, ChildDeclaration>(StringComparer.Ordinal);
        foreach (var t in Chain(type))
        {
            foreach (var child in t.InternalElement)
            {
                if (byName.ContainsKey(child.Name)) continue;
                byName[child.Name] = new ChildDeclaration(child.Name, RuleOf(child), child.RefBaseSystemUnitPath, child);
            }
        }
        return byName.Values.ToList();
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
