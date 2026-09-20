// The one call the plugin and the command line make: convert a NodeSet and
// bring its libraries into a document.

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
        return new ImportResult(conversion, merge, InstancesOf(conversion.Document, modelUri));
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
