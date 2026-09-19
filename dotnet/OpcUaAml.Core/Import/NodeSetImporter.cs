// UA to AML according to OPC 10000-83 Annex A, by way of Opc2Aml.
//
// Opc2Aml writes an .amlx container next to a path it is given. This class
// runs it against a private temporary folder, reads the container back and
// hands out the CAEX document, so callers never see the file round trip.

using Aml.Engine.AmlObjects;
using Aml.Engine.CAEX;
using MarkdownProcessor;
using OpcUaAml.NodeSets;

namespace OpcUaAml.Import;

public sealed class ImportException : Exception
{
    public ImportException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>The libraries generated for one NodeSet.</summary>
public sealed record ConversionResult(
    CAEXDocument Document,
    NodeSetInfo NodeSet,
    IReadOnlyList<string> LoadedModels,
    IReadOnlyList<string> Warnings,
    TimeSpan Duration,
    bool FromCache = false);

public static class NodeSetImporter
{
    /// <summary>
    /// Where conversions are kept (<see cref="ConversionCache.Default"/>); null
    /// converts every time.
    /// </summary>
    public static ConversionCache? Cache { get; set; } = ConversionCache.Default;

    /// <summary>
    /// Opc2Aml runs one at a time in a process, and no document is loaded
    /// meanwhile: Aml.Engine keeps static state while it copies classes and
    /// resolves paths (the ID map of XmlOperation, the loaded documents), and a
    /// conversion fails with "Collection was modified" when another thread
    /// changes it. Loading a document takes the same lock (ReadContainer,
    /// AmlFiles.Load).
    /// </summary>
    internal static readonly object EngineGate = new();

    /// <summary>
    /// Converts one NodeSet into a CAEX 3.0 document holding the AML libraries
    /// of Annex A: the metamodel libraries and one ATL/ICL/RCL/SUC library per
    /// model, the model itself and everything it requires.
    /// </summary>
    /// <exception cref="ImportException">
    /// The file is not a NodeSet, a required model is missing from the catalog,
    /// or Opc2Aml failed.
    /// </exception>
    public static ConversionResult Convert(string nodeSetPath, NodeSetCatalog catalog)
    {
        var fullPath = Path.GetFullPath(nodeSetPath);
        var info = NodeSetInfo.TryRead(fullPath)
            ?? throw new ImportException($"'{nodeSetPath}' is not an OPC UA NodeSet (no UANodeSet root element).");
        if (info.Models.Count == 0)
            throw new ImportException($"'{nodeSetPath}' declares no model, so its namespace cannot be identified.");

        var missing = catalog.MissingDependencies(info);
        if (missing.Count > 0)
        {
            var list = string.Join(", ", missing.Select(m => m.ModelUri));
            throw new ImportException(
                $"'{Path.GetFileName(nodeSetPath)}' requires models that are not available: {list}. " +
                "Add a folder that contains their NodeSet files.");
        }

        var started = DateTime.UtcNow;
        var cache = Cache;
        var key = cache == null ? null : ConversionCache.Key(info, catalog);
        if (cache != null && cache.Find(key!) is { } hit)
        {
            try
            {
                return new ConversionResult(ReadContainer(hit.Container), info, hit.LoadedModels, hit.Warnings, DateTime.UtcNow - started, FromCache: true);
            }
            catch (Exception ex) when (ex is IOException or ImportException or System.Xml.XmlException or InvalidDataException)
            {
                // A damaged entry: convert again and replace it.
            }
        }

        var loaded = new List<string>();
        var manager = new ModelManager();
        manager.ModelRequired += (_, e) =>
        {
            var provider = catalog.Find(e.ModelUri);
            if (provider == null)
                throw new ImportException($"Opc2Aml asked for model '{e.ModelUri}', which the catalog does not know.");
            e.ModelFilePath = provider.FilePath;
            loaded.Add(e.ModelUri);
        };

        var workDir = Directory.CreateTempSubdirectory("opcuaaml-");
        try
        {
            // Opc2Aml names the container after the path it is given and stores
            // the root document inside under that file name.
            var baseName = Path.Combine(workDir.FullName, SafeFileName(Path.GetFileNameWithoutExtension(fullPath)));
            var converter = new NodeSetToAML(manager);
            try
            {
                lock (EngineGate)
                    using (ConversionIds.Use()) converter.CreateAML(fullPath, baseName);
            }
            catch (ImportException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ImportException($"Opc2Aml failed on '{Path.GetFileName(nodeSetPath)}': {ex.Message}", ex);
            }

            var document = ReadContainer(baseName + ".amlx");
            var warnings = converter.Warnings.ToList();
            cache?.Store(key!, baseName + ".amlx", loaded, warnings);
            return new ConversionResult(document, info, loaded, warnings, DateTime.UtcNow - started);
        }
        finally
        {
            try { workDir.Delete(recursive: true); }
            catch (IOException) { /* a scanner holding the file; the OS cleans temp */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Reads the root CAEX document of an .amlx container.</summary>
    public static CAEXDocument ReadContainer(string amlxPath)
    {
        using var container = new AutomationMLContainer(amlxPath, FileMode.Open, FileAccess.Read);
        var root = container.RootDocumentStream()
            ?? throw new ImportException($"'{amlxPath}' has no root AML document.");
        using var copy = new MemoryStream();
        root.CopyTo(copy);
        copy.Position = 0;
        lock (EngineGate) return CAEXDocument.LoadFromStream(copy);
    }

    private static string SafeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }
}
