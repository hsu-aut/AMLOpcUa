// Conversions kept on disk, so a NodeSet converted before is not converted again.
//
// Opc2Aml turns the whole UA base model into libraries on every run, which
// takes 10 to 20 seconds even for a small NodeSet. The result depends only on
// the files it reads and on the converter itself, so the container it writes
// is kept under a key made of both: the content of the NodeSet and of every
// file it requires, and the build of Opc2Aml. A changed file or a rebuilt
// converter gives a new key; old entries are dropped when the cache grows
// beyond its limit. Format counts changes to how NodeSetImporter runs it
// (such as the ID service); raise it with such a change.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpcUaAml.NodeSets;

namespace OpcUaAml.Import;

public sealed class ConversionCache
{
    private const string Format = "2";

    /// <summary>The folder holding the cached containers.</summary>
    public string Folder { get; }

    /// <summary>How many conversions are kept; the least recently used go first.</summary>
    public int Capacity { get; init; } = 40;

    public ConversionCache(string folder) => Folder = folder;

    /// <summary>The cache below the user's local application data.</summary>
    public static ConversionCache Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AMLOpcUa", "conversions"));

    /// <summary>The key of a conversion: the files read and the converter's build.</summary>
    public static string Key(NodeSetInfo nodeSet, NodeSetCatalog catalog)
    {
        using var sha = SHA256.Create();
        var text = new StringBuilder(Format).Append('\n');
        text.Append(typeof(MarkdownProcessor.NodeSetToAML).Assembly.ManifestModule.ModuleVersionId).Append('\n');
        // The primary file first; the others in a stable order, since the catalog's order may vary.
        text.Append(FileHash(nodeSet.FilePath)).Append('\n');
        foreach (var hash in catalog.Dependencies(nodeSet).Select(d => d.PrimaryModel.Model.ModelUri + " " + FileHash(d.FilePath)).OrderBy(s => s, StringComparer.Ordinal))
            text.Append(hash).Append('\n');
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())))[..32].ToLowerInvariant();
    }

    private static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>The cached container and what the conversion reported, or null.</summary>
    public (string Container, IReadOnlyList<string> LoadedModels, IReadOnlyList<string> Warnings)? Find(string key)
    {
        var container = Path.Combine(Folder, key + ".amlx");
        var meta = Path.Combine(Folder, key + ".json");
        if (!File.Exists(container) || !File.Exists(meta)) return null;
        try
        {
            var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(meta));
            if (entry == null) return null;
            File.SetLastAccessTimeUtc(container, DateTime.UtcNow);
            File.SetLastWriteTimeUtc(meta, DateTime.UtcNow);
            return (container, entry.LoadedModels, entry.Warnings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Keeps a conversion. A cache that cannot be written is no error: the next run converts again.</summary>
    public void Store(string key, string container, IReadOnlyList<string> loadedModels, IReadOnlyList<string> warnings)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            // Written under a temporary name and moved, so a parallel reader never sees half a file.
            var temp = Path.Combine(Folder, $"{key}.{Guid.NewGuid():N}.tmp");
            File.Copy(container, temp);
            File.Move(temp, Path.Combine(Folder, key + ".amlx"), overwrite: true);
            File.WriteAllText(temp, JsonSerializer.Serialize(new Entry(loadedModels.ToList(), warnings.ToList())));
            File.Move(temp, Path.Combine(Folder, key + ".json"), overwrite: true);
            Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Removes every cached conversion.</summary>
    public void Clear()
    {
        if (!Directory.Exists(Folder)) return;
        foreach (var file in Directory.EnumerateFiles(Folder))
        {
            try { File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void Trim()
    {
        var entries = new DirectoryInfo(Folder).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Skip(Capacity);
        foreach (var meta in entries)
        {
            try
            {
                File.Delete(Path.ChangeExtension(meta.FullName, ".amlx"));
                meta.Delete();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record Entry(List<string> LoadedModels, List<string> Warnings);
}
