// Creating an instance of a UA type the way an OPC UA server would: every
// Mandatory child, the Optional children the user picked, no placeholders.
//
// Aml.Engine's CreateClassInstance does the heavy part: it flattens the type
// hierarchy (a subtype's declaration overrides the supertype's), assigns new
// IDs and rewires the InternalLinks. What it cannot know is OPC UA's
// ModellingRules, so this class prunes the copy afterwards and removes what
// only makes sense on a type.

using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;

namespace OpcUaAml.Types;

public sealed class InstantiationException : Exception
{
    public InstantiationException(string message) : base(message) { }
}

public sealed class InstantiationOptions
{
    /// <summary>
    /// Decides for an Optional child, given its path relative to the new
    /// instance ("ParameterSet", "Identification/Manufacturer"). Default: none.
    /// </summary>
    public Func<string, bool> IncludeOptional { get; init; } = _ => false;

    /// <summary>Instantiate an abstract type anyway (OPC UA forbids it).</summary>
    public bool AllowAbstract { get; init; }

    /// <summary>
    /// Keep the NodeIds of the type's instance declarations. Off by default:
    /// they identify nodes of the type, and an instance's nodes get their own
    /// NodeIds from the server they are deployed to.
    /// </summary>
    public bool KeepNodeIds { get; init; }
}

public sealed record InstantiationResult(
    InternalElementType Instance,
    IReadOnlyList<string> Included,
    IReadOnlyList<string> OmittedOptional,
    IReadOnlyList<string> OmittedPlaceholders);

public static class TypeInstantiator
{
    private static readonly string[] TypeOnlyAttributes = { "IsAbstract" };

    /// <summary>
    /// Creates a detached instance of <paramref name="type"/> named
    /// <paramref name="name"/>. Insert it where it belongs, e.g.
    /// <c>hierarchy.InternalElement.Insert(result.Instance, asFirst: false)</c>.
    /// </summary>
    public static InstantiationResult Instantiate(SystemUnitFamilyType type, string name, InstantiationOptions? options = null)
    {
        options ??= new InstantiationOptions();
        if (string.IsNullOrWhiteSpace(name)) throw new InstantiationException("The instance needs a name.");
        if (UaTypes.IsAbstract(type) && !options.AllowAbstract)
            throw new InstantiationException(
                $"'{type.Name}' is abstract. OPC UA only instantiates concrete subtypes of it.");

        var instance = type.CreateClassInstance();
        instance.Name = name;

        var included = new List<string>();
        var omittedOptional = new List<string>();
        var omittedPlaceholders = new List<string>();
        var removedInterfaceIds = new HashSet<string>(StringComparer.Ordinal);

        Prune(instance, "", options, included, omittedOptional, omittedPlaceholders, removedInterfaceIds);
        RemoveLinksTo(instance, removedInterfaceIds);

        foreach (var attr in TypeOnlyAttributes)
            if (instance.Attribute[attr] is { } a) instance.Attribute.RemoveElement(a);

        // The root's BrowseName namespace is the type's; the instance lives in
        // the namespace of whatever server it is deployed to. Without the
        // attribute Annex A infers the name from the element and leaves the
        // namespace to the deployment.
        if (instance.Attribute["BrowseName"] is { } browseName) instance.Attribute.RemoveElement(browseName);

        if (!options.KeepNodeIds) RemoveNodeIds(instance);
        RemoveModellingRules(instance);

        return new InstantiationResult(instance, included, omittedOptional, omittedPlaceholders);
    }

    private static void Prune(SystemUnitClassType owner, string prefix, InstantiationOptions options,
        List<string> included, List<string> omittedOptional, List<string> omittedPlaceholders, HashSet<string> removedIds)
    {
        foreach (var child in owner.InternalElement.ToList())
        {
            var path = prefix.Length == 0 ? child.Name : prefix + "/" + child.Name;
            var keep = UaTypes.RuleOf(child) switch
            {
                ModellingRule.MandatoryPlaceholder or ModellingRule.OptionalPlaceholder => Omit(omittedPlaceholders),
                ModellingRule.Optional => options.IncludeOptional(path) || Omit(omittedOptional),
                _ => true,
            };
            bool Omit(List<string> list) { list.Add(path); return false; }

            if (!keep)
            {
                foreach (var ei in child.Descendants<ExternalInterfaceType>().Concat(child.ExternalInterface))
                    if (!string.IsNullOrEmpty(ei.ID)) removedIds.Add(ei.ID);
                owner.InternalElement.RemoveElement(child);
                continue;
            }

            included.Add(path);
            Prune(child, path, options, included, omittedOptional, omittedPlaceholders, removedIds);
        }
    }

    /// <summary>Drops the links that pointed into removed children, at every level.</summary>
    private static void RemoveLinksTo(InternalElementType root, HashSet<string> removedIds)
    {
        if (removedIds.Count == 0) return;
        foreach (var owner in new SystemUnitClassType[] { root }.Concat(root.Descendants<InternalElementType>()))
        {
            foreach (var link in owner.InternalLink.ToList())
            {
                if (removedIds.Contains(link.RefPartnerSideA) || removedIds.Contains(link.RefPartnerSideB))
                    owner.InternalLink.RemoveElement(link);
            }
        }
    }

    private static void RemoveNodeIds(InternalElementType root)
    {
        foreach (var element in new SystemUnitClassType[] { root }.Concat(root.Descendants<InternalElementType>()))
            if (element.Attribute["NodeId"] is { } nodeId) element.Attribute.RemoveElement(nodeId);
    }

    private static void RemoveModellingRules(InternalElementType root)
    {
        foreach (var element in new SystemUnitClassType[] { root }.Concat(root.Descendants<InternalElementType>()))
            foreach (var ei in element.ExternalInterface)
                if (ei.Attribute[UaTypes.ModellingRuleAttribute] is { } rule) ei.Attribute.RemoveElement(rule);
    }
}
