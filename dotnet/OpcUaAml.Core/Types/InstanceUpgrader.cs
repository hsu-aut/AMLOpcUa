// Bringing existing instances up to date after a newer version of a
// companion specification was imported. A newer type may declare Mandatory
// children the instance does not have yet; they are added from the type,
// the way TypeInstantiator would create them. Nothing that exists is changed
// or removed: values, extra children and links stay as the user made them.

using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;

namespace OpcUaAml.Types;

public sealed record UpgradeChange(string ElementPath, string Added);

public static class InstanceUpgrader
{
    /// <summary>
    /// Adds the Mandatory children the instance's type declares and the
    /// instance lacks, recursively for children that are themselves typed.
    /// Returns what was added.
    /// </summary>
    public static IReadOnlyList<UpgradeChange> AddMissingMandatory(InternalElementType instance)
    {
        var changes = new List<UpgradeChange>();
        Upgrade(instance, PathOf(instance), changes);
        return changes;
    }

    /// <summary>All instances in the document's instance hierarchies whose type is a UA type.</summary>
    public static IReadOnlyList<UpgradeChange> UpgradeDocument(CAEXDocument doc)
    {
        var changes = new List<UpgradeChange>();
        foreach (var ih in doc.CAEXFile.InstanceHierarchy)
            foreach (var top in ih.InternalElement)
                Upgrade(top, ih.Name + "/" + top.Name, changes);
        return changes;
    }

    /// <summary>What <see cref="UpgradeDocument"/> would add, without changing the document.</summary>
    public static IReadOnlyList<UpgradeChange> PreviewDocument(CAEXDocument doc)
    {
        var changes = new List<UpgradeChange>();
        foreach (var ih in doc.CAEXFile.InstanceHierarchy)
            foreach (var top in ih.InternalElement)
                Preview(top, ih.Name + "/" + top.Name, changes);
        return changes;
    }

    private static void Preview(InternalElementType element, string path, List<UpgradeChange> changes)
    {
        changes.AddRange(Missing(element).Select(name => new UpgradeChange(path, name)));
        foreach (var child in element.InternalElement)
            Preview(child, path + "/" + child.Name, changes);
    }

    /// <summary>The Mandatory children the element's UA type declares and the element lacks.</summary>
    private static HashSet<string> Missing(InternalElementType element)
    {
        var type = UaTypes.IsUaLibraryPath(element.RefBaseSystemUnitPath) ? UaTypes.TypeOf(element) : null;
        if (type == null) return new HashSet<string>();
        var present = element.InternalElement.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        return UaTypes.Declarations(type)
            .Where(d => d.Rule == ModellingRule.Mandatory && !present.Contains(d.Name))
            .Select(d => d.Name)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static void Upgrade(InternalElementType element, string path, List<UpgradeChange> changes)
    {
        var type = UaTypes.IsUaLibraryPath(element.RefBaseSystemUnitPath) ? UaTypes.TypeOf(element) : null;
        if (type != null)
        {
            var missing = Missing(element);
            if (missing.Count > 0)
            {
                // A fresh instance of the type provides the children exactly
                // as a new instance would get them, links to the parent included.
                var template = TypeInstantiator.Instantiate(type, element.Name, new InstantiationOptions { AllowAbstract = true }).Instance;
                foreach (var name in missing)
                {
                    var child = template.InternalElement[name];
                    if (child == null) continue;
                    element.InternalElement.Insert(child, asFirst: false);
                    changes.Add(new UpgradeChange(path, name));
                }
                RelinkToParent(element, template, missing);
            }
        }
        foreach (var child in element.InternalElement)
            Upgrade(child, path + "/" + child.Name, changes);
    }

    /// <summary>
    /// The template's links from its own reference interfaces to the added
    /// children, redirected to the instance. Matched by child and interface
    /// name: inserting a child that has a parent copies it with new IDs.
    /// </summary>
    private static void RelinkToParent(InternalElementType element, InternalElementType template, HashSet<string> added)
    {
        var templateOwn = template.ExternalInterface.ToDictionary(ei => ei.ID, ei => ei);
        var templateChildSides = template.InternalElement.Where(c => added.Contains(c.Name))
            .SelectMany(c => c.ExternalInterface.Select(ei => (ei.ID, Child: c.Name, Interface: ei.Name)))
            .ToDictionary(x => x.ID, x => (x.Child, x.Interface));

        foreach (var link in template.InternalLink)
        {
            ExternalInterfaceType? parentSide = null;
            (string Child, string Interface) childSide = default;
            if (templateOwn.TryGetValue(link.RefPartnerSideA, out var a) && templateChildSides.TryGetValue(link.RefPartnerSideB, out var b))
                (parentSide, childSide) = (a, b);
            else if (templateOwn.TryGetValue(link.RefPartnerSideB, out var a2) && templateChildSides.TryGetValue(link.RefPartnerSideA, out var b2))
                (parentSide, childSide) = (a2, b2);
            if (parentSide == null) continue;

            var target = element.InternalElement[childSide.Child]?.ExternalInterface[childSide.Interface];
            if (target == null) continue;
            var own = element.ExternalInterface[parentSide.Name];
            if (own == null)
            {
                own = element.ExternalInterface.Append(parentSide.Name);
                own.RefBaseClassPath = parentSide.RefBaseClassPath;
            }
            var newLink = element.InternalLink.Append(link.Name);
            newLink.RefPartnerSideA = own.ID;
            newLink.RefPartnerSideB = target.ID;
        }
    }

    private static string PathOf(CAEXObject element)
    {
        var parts = new List<string>();
        for (CAEXBasicObject? o = element; o is CAEXObject c; o = c.CAEXParent as CAEXBasicObject)
        {
            parts.Add(c.Name);
            if (c is InstanceHierarchyType) break;
        }
        parts.Reverse();
        return string.Join("/", parts);
    }
}
