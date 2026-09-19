// Loading and saving AML documents as .aml files and as .amlx containers
// (AutomationML part 1, OPC packaging) the same way.
//
// A container is read through its root document. Saved into a container that
// exists, only the root document is replaced; libraries, geometry and other
// parts stay. A new container gets the document as its root.

using System.IO.Packaging;
using Aml.Engine.AmlObjects;
using Aml.Engine.CAEX;

namespace OpcUaAml.Import;

public static class AmlFiles
{
    public static bool IsContainer(string path) =>
        Path.GetExtension(path).Equals(".amlx", StringComparison.OrdinalIgnoreCase);

    public static CAEXDocument Load(string path)
    {
        if (!File.Exists(path)) throw new ImportException($"'{path}' does not exist.");
        return IsContainer(path) ? NodeSetImporter.ReadContainer(path) : CAEXDocument.LoadFromFile(path);
    }

    public static void Save(CAEXDocument document, string path)
    {
        if (!IsContainer(path))
        {
            document.SaveToFile(path, true);
            return;
        }
        using var content = document.SaveToStream(true);
        if (File.Exists(path))
        {
            using var existing = new AutomationMLContainer(path, FileMode.Open, FileAccess.ReadWrite);
            if (existing.RootPackage is { } root)
            {
                using var target = root.GetStream(FileMode.Create, FileAccess.Write);
                content.CopyTo(target);
                return;
            }
            existing.AddRoot(content, RootUri(path));
            return;
        }
        using var container = new AutomationMLContainer(path, FileMode.Create);
        container.AddRoot(content, RootUri(path));
    }

    /// <summary>The root document's part, named after the container ("plant.amlx" holds "/plant.aml").</summary>
    private static Uri RootUri(string containerPath) =>
        PackUriHelper.CreatePartUri(new Uri(Uri.EscapeDataString(Path.GetFileNameWithoutExtension(containerPath)) + ".aml", UriKind.Relative));
}
