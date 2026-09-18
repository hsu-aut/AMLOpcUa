// Where NodeSets come from. An import names one file; Opc2Aml then asks for
// every model it requires by URI, and the catalog answers from the folders it
// was given plus the NodeSets that ship with this library.

namespace OpcUaAml.NodeSets;

public sealed class NodeSetCatalog
{
    private readonly Dictionary<string, NodeSetInfo> _byUri = new(StringComparer.Ordinal);
    private readonly List<string> _warnings = new();

    /// <summary>Problems met while scanning, e.g. two files claiming one model.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>All known models, one entry per model URI.</summary>
    public IReadOnlyCollection<NodeSetInfo> NodeSets => _byUri.Values.Distinct().ToList();

    /// <summary>The folder with the NodeSets bundled next to this assembly.</summary>
    public static string BundledFolder =>
        Path.Combine(AppContext.BaseDirectory, "nodesets");

    /// <summary>
    /// A catalog over the given folders. Folders listed first win when two
    /// files declare the same model with the same publication date; otherwise
    /// the newer publication wins. The bundled NodeSets come last, so a user's
    /// own copy of the base model or DI takes precedence.
    /// </summary>
    public static NodeSetCatalog Create(IEnumerable<string> folders, bool includeBundled = true)
    {
        var catalog = new NodeSetCatalog();
        foreach (var folder in folders) catalog.AddFolder(folder);
        if (includeBundled && Directory.Exists(BundledFolder)) catalog.AddFolder(BundledFolder);
        return catalog;
    }

    public void AddFolder(string folder)
    {
        if (!Directory.Exists(folder))
        {
            _warnings.Add($"Folder '{folder}' does not exist.");
            return;
        }
        foreach (var file in Directory.EnumerateFiles(folder, "*.xml").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            AddFile(file);
    }

    /// <summary>Adds one file. Returns its header, or null if it is not a NodeSet.</summary>
    public NodeSetInfo? AddFile(string file)
    {
        var info = NodeSetInfo.TryRead(file);
        if (info == null) return null;
        foreach (var model in info.Models)
        {
            var uri = model.Model.ModelUri;
            if (_byUri.TryGetValue(uri, out var existing))
            {
                var existingDate = existing.Models.First(m => m.Model.ModelUri == uri).Model.PublicationDate ?? DateTime.MinValue;
                var newDate = model.Model.PublicationDate ?? DateTime.MinValue;
                if (newDate > existingDate)
                {
                    _warnings.Add($"Model '{uri}': using '{file}' ({newDate:yyyy-MM-dd}) over '{existing.FilePath}' ({existingDate:yyyy-MM-dd}).");
                    _byUri[uri] = info;
                }
                else if (!SamePath(existing.FilePath, file))
                {
                    _warnings.Add($"Model '{uri}': keeping '{existing.FilePath}', ignoring '{file}'.");
                }
                continue;
            }
            _byUri[uri] = info;
        }
        return info;
    }

    public NodeSetInfo? Find(string modelUri) =>
        _byUri.TryGetValue(modelUri, out var info) ? info : null;

    /// <summary>
    /// The models the given NodeSet needs, transitively, that no known file
    /// provides. An import with a non-empty result would fail inside Opc2Aml
    /// with a less helpful message.
    /// </summary>
    public IReadOnlyList<ModelRef> MissingDependencies(NodeSetInfo root)
    {
        var missing = new List<ModelRef>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<NodeSetInfo>();
        queue.Enqueue(root);
        foreach (var m in root.Models) seen.Add(m.Model.ModelUri);

        while (queue.Count > 0)
        {
            var info = queue.Dequeue();
            foreach (var decl in info.Models)
            {
                foreach (var req in decl.RequiredModels)
                {
                    if (!seen.Add(req.ModelUri)) continue;
                    var provider = Find(req.ModelUri);
                    if (provider == null) missing.Add(req);
                    else queue.Enqueue(provider);
                }
            }
        }
        return missing;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
