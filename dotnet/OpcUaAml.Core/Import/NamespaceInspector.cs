// What one UA namespace contributes to a document, and removing it again.
//
// Opc2Aml gives each namespace four libraries: ObjectTypes and VariableTypes
// as SystemUnitClasses (SUC_), DataTypes as AttributeTypes (ATL_, each with a
// "ListOf" array type beside it), ReferenceTypes as InterfaceClasses (ICL_,
// the inverse name as a child class), and interfaces as RoleClasses (RCL_).
// A namespace builds on another when any of its classes, declarations,
// interfaces or attributes refers to a library of the other.

using System.Xml.Linq;
using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using OpcUaAml.Types;

namespace OpcUaAml.Import;

public sealed record NamespaceDetails(
    string NamespaceUri,
    int ObjectTypes,
    int VariableTypes,
    int DataTypes,
    int ReferenceTypes,
    IReadOnlyList<string> BuildsOn,
    IReadOnlyList<string> UsedBy,
    int Instances,
    IReadOnlyList<string> Libraries);

public static class NamespaceInspector
{
    private static readonly HashSet<string> PathAttributes = new(StringComparer.Ordinal)
    {
        "RefBaseClassPath", "RefBaseSystemUnitPath", "RefAttributeType", "RefBaseRoleClassPath", "RefRoleClassPath",
    };

    public static NamespaceDetails Of(CAEXDocument doc, string namespaceUri)
    {
        var entries = NamespaceOverview.Of(doc);
        var entry = entries.FirstOrDefault(e => e.NamespaceUri == namespaceUri)
            ?? throw new ArgumentException($"The document holds no libraries of '{namespaceUri}'.", nameof(namespaceUri));
        var owner = OwnerOfLibraries(entries);
        var libraries = entry.Libraries.ToHashSet(StringComparer.Ordinal);

        int objectTypes = 0, variableTypes = 0;
        foreach (var lib in doc.CAEXFile.SystemUnitClassLib.Where(l => libraries.Contains(l.Name)))
        {
            foreach (var type in lib.SystemUnitClass.SelectMany(c => c.Descendants<SystemUnitFamilyType>().Prepend(c)))
            {
                if (UaTypes.Chain(type).Any(t => t.Name == "BaseVariableType")) variableTypes++;
                else objectTypes++;
            }
        }
        var dataTypes = doc.CAEXFile.AttributeTypeLib.Where(l => libraries.Contains(l.Name))
            .SelectMany(l => l.AttributeType).Count(a => !a.Name.StartsWith("ListOf", StringComparison.Ordinal));
        var referenceTypes = doc.CAEXFile.InterfaceClassLib.Where(l => libraries.Contains(l.Name)).Sum(l => l.InterfaceClass.Count);

        var buildsOn = BuildsOn(doc, entry, owner);
        var usedBy = entries.Where(e => e.NamespaceUri != namespaceUri && BuildsOn(doc, e, owner).Contains(namespaceUri))
            .Select(e => e.NamespaceUri).ToList();
        return new NamespaceDetails(namespaceUri, objectTypes, variableTypes, dataTypes, referenceTypes,
            buildsOn, usedBy, InstancesOf(doc, entry).Count, entry.Libraries);
    }

    /// <summary>
    /// What keeps a namespace's libraries in the document: namespaces that build
    /// on it and elements of instance hierarchies typed by it. Empty when it can go.
    /// </summary>
    public static IReadOnlyList<string> RemovalBlockers(CAEXDocument doc, string namespaceUri)
    {
        var details = Of(doc, namespaceUri);
        var blockers = details.UsedBy.Select(u => $"{u} builds on it.").ToList();
        if (details.Instances > 0) blockers.Add($"{details.Instances} element(s) of the instance hierarchies use its types.");
        return blockers;
    }

    /// <summary>Removes the namespace's libraries; returns their names.</summary>
    /// <exception cref="InvalidOperationException">Something still depends on them (see <see cref="RemovalBlockers"/>).</exception>
    public static IReadOnlyList<string> Remove(CAEXDocument doc, string namespaceUri)
    {
        var blockers = RemovalBlockers(doc, namespaceUri);
        if (blockers.Count > 0)
            throw new InvalidOperationException($"'{namespaceUri}' is still needed: {string.Join(" ", blockers)}");
        var libraries = NamespaceOverview.Of(doc).Single(e => e.NamespaceUri == namespaceUri).Libraries.ToHashSet(StringComparer.Ordinal);
        var removed = new List<string>();
        foreach (var lib in doc.CAEXFile.AttributeTypeLib.Cast<CAEXObject>().Concat(doc.CAEXFile.InterfaceClassLib)
                     .Concat(doc.CAEXFile.RoleClassLib).Concat(doc.CAEXFile.SystemUnitClassLib)
                     .Where(l => libraries.Contains(l.Name)).ToList())
        {
            removed.Add(lib.Name);
            lib.Remove();
        }
        return removed;
    }

    private static Dictionary<string, string> OwnerOfLibraries(IReadOnlyList<NamespaceEntry> entries)
    {
        var owner = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in entries)
            foreach (var lib in e.Libraries) owner.TryAdd(lib, e.NamespaceUri);
        return owner;
    }

    /// <summary>The namespaces whose libraries this namespace's libraries refer to.</summary>
    private static List<string> BuildsOn(CAEXDocument doc, NamespaceEntry entry, Dictionary<string, string> owner)
    {
        var own = entry.Libraries.ToHashSet(StringComparer.Ordinal);
        var result = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var lib in Libraries(doc).Where(l => own.Contains(l.Name)))
        {
            foreach (var attribute in lib.Node.DescendantsAndSelf().Attributes().Where(a => PathAttributes.Contains(a.Name.LocalName)))
            {
                if (LibraryOf(attribute.Value) is { } name && !own.Contains(name) && owner.TryGetValue(name, out var uri))
                    result.Add(uri);
            }
        }
        return result.ToList();
    }

    private static List<InternalElementType> InstancesOf(CAEXDocument doc, NamespaceEntry entry)
    {
        var prefixes = entry.Libraries.Where(l => l.StartsWith("SUC_", StringComparison.Ordinal)).Select(l => $"[{l}]/").ToList();
        return doc.CAEXFile.InstanceHierarchy.SelectMany(ih => ih.Descendants<InternalElementType>())
            .Where(ie => ie.RefBaseSystemUnitPath is { } p && prefixes.Any(prefix => p.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();
    }

    private static IEnumerable<CAEXObject> Libraries(CAEXDocument doc) =>
        doc.CAEXFile.AttributeTypeLib.Cast<CAEXObject>().Concat(doc.CAEXFile.InterfaceClassLib)
            .Concat(doc.CAEXFile.RoleClassLib).Concat(doc.CAEXFile.SystemUnitClassLib);

    /// <summary>"[SUC_x]/[Type]" → "SUC_x".</summary>
    private static string? LibraryOf(string path) =>
        path.StartsWith('[') && path.IndexOf(']') is var end && end > 1 ? path[1..end] : null;
}
