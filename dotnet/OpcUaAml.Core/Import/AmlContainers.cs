// Copies of .amlx containers (AutomationML part 1, OPC packaging) with
// another root document. Done with System.IO.Packaging alone: inside the
// AutomationML Editor, Aml.Engine is the editor's own version, and its
// container API has differed from the one this code was built against
// (see Opc2Aml patch 0010).

using System.IO.Packaging;
using System.Xml.Linq;

namespace OpcUaAml.Import;

public static class AmlContainers
{
    /// <summary>The relationship that names a container's root document.</summary>
    public const string RootDocumentRelationship = "http://schemas.automationml.org/container/relationship/RootDocument";

    /// <summary>Whether a path names a container (.amlx).</summary>
    public static bool IsContainerPath(string path) =>
        Path.GetExtension(path).Equals(".amlx", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Writes <paramref name="target"/> as a copy of the container <paramref name="source"/>
    /// whose root document is <paramref name="root"/>; every other part stays as it is.
    /// </summary>
    /// <exception cref="InvalidDataException">The source names no root document.</exception>
    public static void CopyWithRoot(string source, XDocument root, string target)
    {
        var same = string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);
        if (!same)
        {
            using var check = Package.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!check.GetRelationshipsByType(RootDocumentRelationship).Any())
                throw new InvalidDataException($"'{source}' is not an AutomationML container: it names no root document.");
            File.Copy(source, target, overwrite: true);
        }
        using var package = Package.Open(target, FileMode.Open, FileAccess.ReadWrite);
        var relation = package.GetRelationshipsByType(RootDocumentRelationship).FirstOrDefault()
                       ?? throw new InvalidDataException($"'{source}' is not an AutomationML container: it names no root document.");
        var part = package.GetPart(PackUriHelper.ResolvePartUri(new Uri("/", UriKind.Relative), relation.TargetUri));
        using var stream = part.GetStream(FileMode.Create, FileAccess.Write);
        root.Save(stream);
    }
}
