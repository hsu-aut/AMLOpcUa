// Creating an instance of a UA type the way an OPC UA server would: every
// Mandatory child, the Optional children the user picked, and for each
// placeholder the concrete children the user named.
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

/// <summary>
/// A concrete child for a placeholder: its name, and a type to create it from
/// (a subtype of the placeholder's type), or null for the placeholder's own.
/// </summary>
public sealed record PlaceholderFill(string Name, SystemUnitFamilyType? Type = null);

/// <summary>A placeholder of the type: where it is, what type its children have, and whether one is required.</summary>
public sealed record PlaceholderInfo(string Path, SystemUnitFamilyType? Type, bool Mandatory);

public sealed class InstantiationOptions
{
    /// <summary>
    /// Decides for an Optional child, given its path relative to the new
    /// instance ("ParameterSet", "Identification/Manufacturer"). Default: none.
    /// </summary>
    public Func<string, bool> IncludeOptional { get; init; } = _ => false;

    /// <summary>
    /// The concrete children for a placeholder, given its path relative to the
    /// new instance ("&lt;ObjectIdentifier&gt;", "ParameterSet/&lt;ParameterIdentifier&gt;").
    /// Default: none, the placeholder is left out.
    /// </summary>
    public Func<string, IReadOnlyList<PlaceholderFill>> FillPlaceholder { get; init; } = _ => Array.Empty<PlaceholderFill>();

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
    IReadOnlyList<string> OmittedPlaceholders)
{
    /// <summary>The concrete children created for placeholders, by path.</summary>
    public IReadOnlyList<string> Filled { get; init; } = Array.Empty<string>();

    /// <summary>Every placeholder met, filled or not.</summary>
    public IReadOnlyList<PlaceholderInfo> Placeholders { get; init; } = Array.Empty<PlaceholderInfo>();
}

public static class TypeInstantiator
{
    private static readonly string[] TypeOnlyAttributes = { "IsAbstract" };

    /// <summary>How deep an inherited hierarchy is followed; real models stay far below.</summary>
    private const int MaxMergeDepth = 12;

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
        var filled = new List<string>();
        var placeholders = new List<PlaceholderInfo>();
        var removedInterfaceIds = new HashSet<string>(StringComparer.Ordinal);

        var run = new Run(options, included, omittedOptional, omittedPlaceholders, filled, placeholders, removedInterfaceIds);
        MergeInherited(instance, UaTypes.Chain(type).Cast<SystemUnitClassType>().ToList());
        Prune(instance, "", run);
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

        return new InstantiationResult(instance, included, omittedOptional, omittedPlaceholders) { Filled = filled, Placeholders = placeholders };
    }

    private sealed record Run(InstantiationOptions Options, List<string> Included, List<string> OmittedOptional,
        List<string> OmittedPlaceholders, List<string> Filled, List<PlaceholderInfo> Placeholders, HashSet<string> RemovedIds);

    /// <summary>
    /// Gives a declaration back what it inherits. Aml.Engine flattens a type
    /// hierarchy by replacing a declaration of the same name; OPC UA replaces
    /// the node, not what hangs below it, so an instance of 3DFrameType keeps
    /// the LengthUnit that FrameType declares under CartesianCoordinates (OPC
    /// 10000-3, the fully inherited instance declaration hierarchy). Missing
    /// children are copied in, with the reference that holds them.
    /// </summary>
    /// <param name="target">The element to complete, an instance or a copy in one.</param>
    /// <param name="declarations">What it stands for, the nearest declaration first.</param>
    private static void MergeInherited(SystemUnitClassType target, IReadOnlyList<SystemUnitClassType> declarations, int depth = 0)
    {
        if (depth >= MaxMergeDepth) return;

        var byName = new Dictionary<string, List<(SystemUnitClassType Owner, InternalElementType Element)>>(StringComparer.Ordinal);
        foreach (var declaration in declarations)
        {
            foreach (var child in declaration.InternalElement)
            {
                var name = UaTypes.QualifiedName(child);
                if (!byName.TryGetValue(name, out var declared)) byName[name] = declared = new();
                declared.Add((declaration, child));
            }
        }

        foreach (var declared in byName.Values)
        {
            var name = UaTypes.QualifiedName(declared[0].Element);
            var own = target.InternalElement.FirstOrDefault(e => UaTypes.QualifiedName(e) == name)
                      ?? CopyWithLinks(target, declared[0].Owner, declared[0].Element);
            MergeInherited(own, declared.Select(d => (SystemUnitClassType)d.Element).ToList(), depth + 1);
        }
    }

    /// <summary>
    /// Copies a declared child into <paramref name="target"/>, together with
    /// the links that attach it, so the copy hangs where the original hung.
    /// </summary>
    private static InternalElementType CopyWithLinks(SystemUnitClassType target, SystemUnitClassType source, InternalElementType element)
    {
        // Annex A keeps the link in the parent: it joins an interface of the
        // parent (HasProperty) to one of the child (PropertyOf).
        var ownEnds = element.ExternalInterface.Where(ei => !string.IsNullOrEmpty(ei.ID)).ToDictionary(ei => ei.ID, StringComparer.Ordinal);
        var links = source.InternalLink.Where(l => ownEnds.ContainsKey(l.RefPartnerSideA) || ownEnds.ContainsKey(l.RefPartnerSideB)).ToList();

        var copy = (InternalElementType)element.Copy(deepCopy: true, assignNewIDs: true);
        var newIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (original, made) in element.ExternalInterface.Zip(copy.ExternalInterface))
            if (!string.IsNullOrEmpty(original.ID)) newIds[original.ID] = made.ID;
        target.InternalElement.Insert(copy, asFirst: false);

        foreach (var link in links)
        {
            var parentEnd = ownEnds.ContainsKey(link.RefPartnerSideA) ? link.RefPartnerSideB : link.RefPartnerSideA;
            if (!newIds.TryGetValue(parentEnd, out _) && EndOfParent(target, source, parentEnd) is { } mapped)
                newIds[parentEnd] = mapped;
            if (!newIds.ContainsKey(parentEnd)) continue;
            var added = target.New_InternalLink(copy.Name);
            added.RefPartnerSideA = newIds.GetValueOrDefault(link.RefPartnerSideA, link.RefPartnerSideA);
            added.RefPartnerSideB = newIds.GetValueOrDefault(link.RefPartnerSideB, link.RefPartnerSideB);
        }
        return copy;
    }

    /// <summary>
    /// The interface of <paramref name="target"/> that stands for the one the
    /// declaration links from, by name (HasProperty, HasComponent); it is
    /// copied over when the target has none of that name yet.
    /// </summary>
    private static string? EndOfParent(SystemUnitClassType target, SystemUnitClassType source, string endId)
    {
        var end = source.ExternalInterface.FirstOrDefault(ei => ei.ID == endId);
        if (end == null) return null;
        var own = target.ExternalInterface.FirstOrDefault(ei => ei.Name == end.Name);
        if (own != null) return own.ID;
        var copy = (ExternalInterfaceType)end.Copy(deepCopy: true, assignNewIDs: true);
        target.ExternalInterface.Insert(copy, asFirst: false);
        return copy.ID;
    }

    private static void Prune(SystemUnitClassType owner, string prefix, Run run)
    {
        foreach (var child in owner.InternalElement.ToList())
        {
            var path = prefix.Length == 0 ? child.Name : prefix + "/" + child.Name;
            var rule = UaTypes.RuleOf(child);
            if (rule is ModellingRule.MandatoryPlaceholder or ModellingRule.OptionalPlaceholder)
            {
                run.Placeholders.Add(new PlaceholderInfo(path, UaTypes.TypeOf(child), rule == ModellingRule.MandatoryPlaceholder));
                var fills = run.Options.FillPlaceholder(path);
                foreach (var fill in fills)
                {
                    var concrete = Fill(owner, child, fill, prefix, run);
                    run.Filled.Add(prefix.Length == 0 ? concrete.Name : prefix + "/" + concrete.Name);
                }
                if (fills.Count == 0) run.OmittedPlaceholders.Add(path);
                Remove(owner, child, run);
                continue;
            }
            // Without a ModellingRule a child is no instance declaration (OPC 10000-3 6.4.4.4):
            // it describes the type, like DefaultInstanceBrowseName, and is not instantiated.
            if (rule == ModellingRule.None)
            {
                Remove(owner, child, run);
                continue;
            }
            // ExposesItsArray: the instance gets one child per element of the
            // array, and the specification leaves their BrowseNames open. How
            // many there are is known when there are values, not here, so the
            // instance gets none rather than exactly one under a name the
            // specification does not give it (the modeler does the same).
            if (rule == ModellingRule.ExposesItsArray)
            {
                Remove(owner, child, run);
                continue;
            }
            if (rule == ModellingRule.Optional && !run.Options.IncludeOptional(path))
            {
                run.OmittedOptional.Add(path);
                Remove(owner, child, run);
                continue;
            }

            run.Included.Add(path);
            Prune(child, path, run);
        }
    }

    private static void Remove(SystemUnitClassType owner, InternalElementType child, Run run)
    {
        foreach (var ei in child.Descendants<ExternalInterfaceType>().Concat(child.ExternalInterface))
            if (!string.IsNullOrEmpty(ei.ID)) run.RemovedIds.Add(ei.ID);
        owner.InternalElement.RemoveElement(child);
    }

    /// <summary>
    /// A concrete child in place of a placeholder: a copy of the placeholder
    /// (its declaration already holds the structure of its type) or an instance
    /// of the given subtype, named as asked and linked to the owner the way the
    /// placeholder was.
    /// </summary>
    private static InternalElementType Fill(SystemUnitClassType owner, InternalElementType placeholder, PlaceholderFill fill, string prefix, Run run)
    {
        if (string.IsNullOrWhiteSpace(fill.Name)) throw new InstantiationException($"A child for '{placeholder.Name}' needs a name.");
        // The placeholder is still in the collection while it is being filled,
        // and a method keeps the name of its declaration, so the placeholder
        // itself does not count as a child of that name.
        if (owner.InternalElement.Any(e => e.Name == fill.Name && e.ID != placeholder.ID))
            throw new InstantiationException($"'{owner.Name}' already has a child named '{fill.Name}'.");

        // The links that attach the placeholder to its owner, and the placeholder's end of each.
        var ownEnds = placeholder.ExternalInterface.Where(ei => !string.IsNullOrEmpty(ei.ID)).ToDictionary(ei => ei.ID, StringComparer.Ordinal);
        var links = owner.InternalLink.Where(l => ownEnds.ContainsKey(l.RefPartnerSideA) || ownEnds.ContainsKey(l.RefPartnerSideB)).ToList();

        InternalElementType concrete;
        var newIds = new Dictionary<string, string>(StringComparer.Ordinal);
        if (fill.Type == null)
        {
            concrete = (InternalElementType)placeholder.Copy(deepCopy: true, assignNewIDs: true);
            // The copy keeps the order of the interfaces.
            foreach (var (original, copy) in placeholder.ExternalInterface.Zip(concrete.ExternalInterface))
                if (!string.IsNullOrEmpty(original.ID)) newIds[original.ID] = copy.ID;
        }
        else
        {
            if (UaTypes.IsAbstract(fill.Type) && !run.Options.AllowAbstract)
                throw new InstantiationException($"'{fill.Type.Name}' is abstract. OPC UA only instantiates concrete subtypes of it.");
            concrete = fill.Type.CreateClassInstance();
            if (concrete.Attribute["IsAbstract"] is { } isAbstract) concrete.Attribute.RemoveElement(isAbstract);
            // An instance of a type has no end for the reference from its parent; it gets the placeholder's.
            foreach (var link in links)
            {
                var end = ownEnds.GetValueOrDefault(link.RefPartnerSideA) ?? ownEnds[link.RefPartnerSideB];
                if (newIds.ContainsKey(end.ID)) continue;
                var copy = (ExternalInterfaceType)end.Copy(deepCopy: true, assignNewIDs: true);
                concrete.ExternalInterface.Insert(copy, asFirst: false);
                newIds[end.ID] = copy.ID;
            }
        }
        concrete.Name = fill.Name;
        owner.InternalElement.Insert(concrete, asFirst: false);

        foreach (var link in links)
        {
            var added = owner.New_InternalLink(fill.Name);
            added.RefPartnerSideA = newIds.GetValueOrDefault(link.RefPartnerSideA, link.RefPartnerSideA);
            added.RefPartnerSideB = newIds.GetValueOrDefault(link.RefPartnerSideB, link.RefPartnerSideB);
        }

        var path = prefix.Length == 0 ? fill.Name : prefix + "/" + fill.Name;
        run.Included.Add(path);
        Prune(concrete, path, run);
        return concrete;
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
