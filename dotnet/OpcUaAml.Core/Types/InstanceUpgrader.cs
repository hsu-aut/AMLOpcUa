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
        Upgrade(instance, PathOf(instance), null, changes);
        return changes;
    }

    /// <summary>All instances in the document's instance hierarchies whose type is a UA type.</summary>
    public static IReadOnlyList<UpgradeChange> UpgradeDocument(CAEXDocument doc)
    {
        var changes = new List<UpgradeChange>();
        foreach (var ih in doc.CAEXFile.InstanceHierarchy)
            foreach (var top in ih.InternalElement)
                Upgrade(top, ih.Name + "/" + top.Name, null, changes);
        return changes;
    }

    /// <summary>What <see cref="UpgradeDocument"/> would add, without changing the document.</summary>
    public static IReadOnlyList<UpgradeChange> PreviewDocument(CAEXDocument doc)
    {
        var changes = new List<UpgradeChange>();
        foreach (var ih in doc.CAEXFile.InstanceHierarchy)
            foreach (var top in ih.InternalElement)
                Preview(top, ih.Name + "/" + top.Name, null, changes);
        return changes;
    }

    // Down the hierarchy, as AnnexAChecker goes: what a type declares below a
    // child counts there, beside what the child's own type declares.
    private static void Preview(InternalElementType element, string path, IReadOnlyList<ChildDeclaration>? declared, List<UpgradeChange> changes)
    {
        var applying = UaTypes.Applying(element, declared);
        changes.AddRange(Missing(element, applying).Select(d => new UpgradeChange(path, d.Name)));
        foreach (var child in element.InternalElement)
            Preview(child, path + "/" + child.Name, UaTypes.Below(applying, child), changes);
    }

    /// <summary>The Mandatory children the declarations that apply to the element require and the element lacks.</summary>
    private static List<ChildDeclaration> Missing(InternalElementType element, IReadOnlyList<ChildDeclaration> applying)
    {
        var present = element.InternalElement.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        return applying.Where(d => d.Rule == ModellingRule.Mandatory && !present.Contains(d.Name)).ToList();
    }

    private static void Upgrade(InternalElementType element, string path, IReadOnlyList<ChildDeclaration>? declared, List<UpgradeChange> changes)
    {
        var applying = UaTypes.Applying(element, declared);
        // Each child made from its declaration as a new instance would get
        // it, linked to the element the way the declaration is linked.
        foreach (var missing in Missing(element, applying))
        {
            TypeInstantiator.AddDeclared(element, missing);
            changes.Add(new UpgradeChange(path, missing.Name));
        }
        foreach (var child in element.InternalElement.ToList())
            Upgrade(child, path + "/" + child.Name, UaTypes.Below(applying, child), changes);
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
