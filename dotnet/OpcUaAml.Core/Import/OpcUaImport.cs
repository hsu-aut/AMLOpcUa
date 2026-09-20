// The one call the plugin and the command line make: convert a NodeSet and
// bring its libraries into a document.

using System.Xml.Linq;
using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using OpcUaAml.NodeSets;

namespace OpcUaAml.Import;

/// <param name="InstancesNotTaken">
/// Nodes of the model that Annex A put into an instance hierarchy. The import
/// takes the libraries, not the address space, so they stay behind; a model
/// that is nothing but instances arrives empty, and that has to be said.
/// </param>
public sealed record ImportResult(ConversionResult Conversion, MergeReport Merge, int InstancesNotTaken = 0)
{
    public IReadOnlyList<string> Warnings => Conversion.Warnings;

    public string Summary =>
        $"Imported {Conversion.NodeSet.PrimaryModel.Model.ModelUri}: {Merge}" +
        (InstancesNotTaken > 0 ? $", {InstancesNotTaken} instance(s) of the model not taken (only its types are imported)" : "") +
        (Warnings.Count > 0 ? $", {Warnings.Count} warning(s)" : "") +
        $" ({Conversion.Duration.TotalSeconds:0.0} s{(Conversion.FromCache ? ", converted before" : "")}).";
}

public static class OpcUaImport
{
    /// <summary>
    /// Converts <paramref name="nodeSetPath"/> according to OPC 10000-83 Annex A
    /// and merges the resulting libraries into <paramref name="target"/>.
    /// </summary>
    /// <exception cref="ImportException">Conversion or merge failed; the document is unchanged.</exception>
    public static ImportResult ImportInto(CAEXDocument target, string nodeSetPath, NodeSetCatalog catalog,
        MergeOptions? options = null)
    {
        // Checked before the conversion, which takes seconds, so a wrong
        // document fails at once. Merge checks again for direct callers.
        if (target.CAEXFile.SchemaVersion != LibraryMerger.RequiredSchemaVersion)
            LibraryMerger.Merge(target, CAEXDocument.New_CAEXDocument(), options);

        var conversion = NodeSetImporter.Convert(nodeSetPath, catalog);
        var merge = LibraryMerger.Merge(target, conversion.Document, options);
        var modelUri = conversion.NodeSet.PrimaryModel.Model.ModelUri;
        var warnings = conversion.Warnings.Concat(UnmappedChildren(nodeSetPath, modelUri)).ToList();
        return new ImportResult(conversion with { Warnings = warnings }, merge, InstancesOf(conversion.Document, modelUri));
    }

    /// <summary>
    /// Nodes that hang off their type only through a reference Annex A does
    /// not follow: Organizes, or a reference type the model defines itself.
    /// They do not arrive in the document, and their children go with them, so
    /// the import says which ones they are rather than let them vanish.
    /// </summary>
    private static IEnumerable<string> UnmappedChildren(string nodeSetPath, string modelUri)
    {
        XDocument nodeSet;
        try
        {
            nodeSet = SafeXml.Load(nodeSetPath, LoadOptions.None);
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            yield break;
        }
        if (nodeSet.Root is null) yield break;

        XNamespace ua = "http://opcfoundation.org/UA/2011/03/UANodeSet.xsd";
        var namespaces = new List<string> { NodeSetInfo.UaNamespace };
        namespaces.AddRange(nodeSet.Root.Element(ua + "NamespaceUris")?.Elements(ua + "Uri").Select(u => u.Value.Trim()) ?? []);
        var index = namespaces.IndexOf(modelUri);
        if (index < 1) yield break;
        var prefix = $"ns={index};";

        var aliases = (nodeSet.Root.Element(ua + "Aliases")?.Elements(ua + "Alias") ?? [])
            .ToDictionary(a => (string)a.Attribute("Alias")!, a => a.Value.Trim(), StringComparer.Ordinal);
        string Resolve(string? text) => text is null ? "" : aliases.TryGetValue(text, out var id) ? id : text;

        // What holds a node: the references Annex A maps as a child, and the
        // rest, which it does not.
        var holds = new Dictionary<string, (bool Mapped, bool Other, string By)>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var kinds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in nodeSet.Root.Elements().Where(e => e.Name.LocalName.StartsWith("UA", StringComparison.Ordinal)))
        {
            if ((string?)node.Attribute("NodeId") is { Length: > 0 } id)
            {
                // A type lives in a library of its own; only what a type holds
                // can be lost by the reference that holds it.
                if (node.Name.LocalName is "UAObject" or "UAVariable" or "UAMethod") names[id] = (string?)node.Attribute("BrowseName") ?? id;
            }
            foreach (var r in node.Element(ua + "References")?.Elements(ua + "Reference") ?? [])
            {
                if ((string?)r.Attribute("IsForward") == "false") continue;
                var type = Resolve((string?)r.Attribute("ReferenceType"));
                var target = r.Value.Trim();
                if (target.Length == 0) continue;
                if (type == "i=40" && (string?)node.Attribute("NodeId") is { Length: > 0 } self) kinds[self] = target;
                var mapped = type is "i=47" or "i=46" or "i=49";
                var current = holds.GetValueOrDefault(target);
                holds[target] = (current.Mapped || mapped, current.Other || !mapped, mapped ? current.By : type);
            }
        }

        var lost = holds
            .Where(h => h.Key.StartsWith(prefix, StringComparison.Ordinal) && !h.Value.Mapped && h.Value.Other)
            .Where(h => names.ContainsKey(h.Key))
            // The encodings of a structure, the type dictionaries and the
            // namespace metadata are left out on purpose and are reported by
            // the round trip, not here.
            .Where(h => kinds.GetValueOrDefault(h.Key) is not ("i=76" or "i=72" or "i=69" or "i=11616"))
            .Select(h => names[h.Key])
            .ToList();
        if (lost.Count > 0)
        {
            yield return $"{lost.Count} node(s) hang off their type only through a reference Annex A does not map "
                         + $"(Organizes or a reference type of the model), so they and what they hold are not in the document: "
                         + string.Join(", ", lost.Take(5)) + (lost.Count > 5 ? ", …" : "") + ".";
        }
    }

    /// <summary>
    /// How many nodes of the model Annex A left in an instance hierarchy. They
    /// are the model's address space, which the import does not bring into the
    /// document; a model that consists of instances (a dictionary, for one)
    /// would otherwise seem to have arrived.
    /// </summary>
    private static int InstancesOf(CAEXDocument converted, string modelUri)
    {
        var count = 0;
        foreach (var hierarchy in converted.CAEXFile.InstanceHierarchy)
        {
            foreach (var element in hierarchy.InternalElement.SelectMany(e => e.Descendants<InternalElementType>().Prepend(e)))
            {
                var uri = element.Attribute["NodeId"]?.Attribute["RootNodeId"]?.Attribute["NamespaceUri"]?.Value;
                if (uri == modelUri) count++;
            }
        }
        return count;
    }
}
