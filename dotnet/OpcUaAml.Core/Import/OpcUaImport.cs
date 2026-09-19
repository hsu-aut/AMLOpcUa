// The one call the plugin and the command line make: convert a NodeSet and
// bring its libraries into a document.

using Aml.Engine.CAEX;
using OpcUaAml.NodeSets;

namespace OpcUaAml.Import;

public sealed record ImportResult(ConversionResult Conversion, MergeReport Merge)
{
    public IReadOnlyList<string> Warnings => Conversion.Warnings;

    public string Summary =>
        $"Imported {Conversion.NodeSet.PrimaryModel.Model.ModelUri}: {Merge}" +
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
        return new ImportResult(conversion, merge);
    }
}
