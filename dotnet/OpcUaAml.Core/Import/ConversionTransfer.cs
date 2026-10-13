// A conversion handed from one process to another: the document as an AML
// file, the rest of the result as JSON beside it (<file>.json).
//
// The plugin does not convert inside the AutomationML Editor: there Aml.Engine
// is the editor's, in a version of its own and with services the editor
// registers for the whole process (undo, unique names). They turned a
// conversion of seconds into many minutes and could rename what Annex A names
// on purpose. The plugin runs `uaaml convert` instead and reads the result.

using System.Text.Json;
using OpcUaAml.NodeSets;

namespace OpcUaAml.Import;

public static class ConversionTransfer
{
    private sealed record Description(
        List<string> LoadedModels,
        List<string> Warnings,
        double DurationSeconds,
        bool FromCache,
        List<string> CatalogNotes);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>The file that describes the result written to <paramref name="amlPath"/>.</summary>
    public static string DescriptionPath(string amlPath) => amlPath + ".json";

    /// <summary>Writes the document to <paramref name="amlPath"/> and the rest of the result beside it.</summary>
    /// <param name="catalogNotes">What the catalog noted while it looked for the NodeSets, for the log.</param>
    public static void Write(ConversionResult result, IEnumerable<string> catalogNotes, string amlPath)
    {
        lock (NodeSetImporter.EngineGate) result.Document.SaveToFile(amlPath, true);
        var description = new Description(result.LoadedModels.ToList(), result.Warnings.ToList(),
            result.Duration.TotalSeconds, result.FromCache, catalogNotes.ToList());
        File.WriteAllText(DescriptionPath(amlPath), JsonSerializer.Serialize(description, Json));
    }

    /// <summary>Reads a result <see cref="Write"/> wrote, for the NodeSet at <paramref name="nodeSetPath"/>.</summary>
    /// <exception cref="ImportException">The description is missing or damaged, or the NodeSet cannot be read.</exception>
    public static (ConversionResult Result, IReadOnlyList<string> CatalogNotes) Read(string amlPath, string nodeSetPath)
    {
        var descriptionPath = DescriptionPath(amlPath);
        Description description;
        try
        {
            description = JsonSerializer.Deserialize<Description>(File.ReadAllText(descriptionPath))
                ?? throw new JsonException("empty");
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new ImportException($"The converter left no readable description of its result ({Path.GetFileName(descriptionPath)}): {ex.Message}", ex);
        }
        var info = NodeSetInfo.TryRead(nodeSetPath)
            ?? throw new ImportException($"'{nodeSetPath}' is not an OPC UA NodeSet (no UANodeSet root element).");
        var result = new ConversionResult(NodeSetImporter.ReadDocument(amlPath), info, description.LoadedModels,
            description.Warnings, TimeSpan.FromSeconds(description.DurationSeconds), description.FromCache);
        return (result, description.CatalogNotes);
    }
}
