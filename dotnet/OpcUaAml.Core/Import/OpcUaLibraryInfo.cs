// Which UA model a generated library came from. Opc2Aml writes an
// <OpcUaLibInfo> block into the AdditionalInformation of every library it
// derives from a NodeSet (OPC 10000-83, Annex K: OpcUaLibInfo schema).

using System.Globalization;
using Aml.Engine.CAEX;

namespace OpcUaAml.Import;

public sealed record OpcUaLibraryInfo(string NamespaceUri, string? ModelVersion, DateTime? PublicationDate)
{
    /// <summary>Reads the block, or returns null for libraries that carry none.</summary>
    public static OpcUaLibraryInfo? Read(CAEXBasicObject library)
    {
        // Read from the XML: the engine exposes AdditionalInformation as
        // untyped content, and the block may sit directly in it or one level down.
        var blocks = library.Node.Elements()
            .Where(e => e.Name.LocalName == "AdditionalInformation")
            .SelectMany(e => e.DescendantsAndSelf())
            .Where(e => e.Name.LocalName == "OpcUaLibInfo");
        foreach (var element in blocks)
        {
            string? Value(string local) => element.Elements().FirstOrDefault(e => e.Name.LocalName == local)?.Value;

            var uri = Value("OpcUaNamespaceUri");
            if (string.IsNullOrEmpty(uri)) continue;
            DateTime? date = DateTime.TryParse(Value("ModelPublicationDate"), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;
            return new OpcUaLibraryInfo(uri, Value("ModelVersion"), date);
        }
        return null;
    }
}

/// <summary>One UA namespace present in a document, with its libraries.</summary>
public sealed record NamespaceEntry(string NamespaceUri, string? ModelVersion, DateTime? PublicationDate,
    IReadOnlyList<string> Libraries);

public static class NamespaceOverview
{
    /// <summary>The UA namespaces whose libraries a document holds.</summary>
    public static IReadOnlyList<NamespaceEntry> Of(CAEXDocument doc)
    {
        var libs = doc.CAEXFile.AttributeTypeLib.Cast<CAEXBasicObject>()
            .Concat(doc.CAEXFile.InterfaceClassLib)
            .Concat(doc.CAEXFile.RoleClassLib)
            .Concat(doc.CAEXFile.SystemUnitClassLib);

        return libs
            .Select(l => (Lib: (CAEXObject)l, Info: OpcUaLibraryInfo.Read(l)))
            .Where(x => x.Info != null)
            .GroupBy(x => x.Info!.NamespaceUri)
            .Select(g =>
            {
                var newest = g.OrderByDescending(x => x.Info!.PublicationDate ?? DateTime.MinValue).First().Info!;
                return new NamespaceEntry(g.Key, newest.ModelVersion, newest.PublicationDate,
                    g.Select(x => x.Lib.Name).ToList());
            })
            .OrderBy(e => e.NamespaceUri, StringComparer.Ordinal)
            .ToList();
    }
}
