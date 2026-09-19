// The NodeSets the OPC Foundation publishes on GitHub (OPCFoundation/UA-Nodeset,
// branch "latest"): every released companion specification, readable without
// an account, unlike the UA Cloud Library.
//
// The repository's folders do not name the namespaces reliably (ISA-95 holds
// http://www.OPCFoundation.org/UA/2013/01/ISA95), so an index says which models
// each file declares. It is built from the start of each file, up to the end
// of <Models>, fetched with a Range request, and kept on disk with the file's
// blob SHA: a later refresh reads only the files that changed. The file list
// costs one call to GitHub's API, which allows 60 an hour without login; it is
// asked at most once a day.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace OpcUaAml.NodeSets;

/// <summary>A model one file of the repository declares.</summary>
public sealed record PublishedModel(string ModelUri, string? Version, DateTime? PublicationDate, string Path, IReadOnlyList<string> RequiredModels)
{
    /// <summary>The folder of the file, which names the specification in most cases ("Machinery", "IJT/Tightening").</summary>
    public string Folder => System.IO.Path.GetDirectoryName(Path)?.Replace('\\', '/') ?? "";
}

public sealed class OpcFoundationNodeSetsException : Exception
{
    public OpcFoundationNodeSetsException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class OpcFoundationNodeSets
{
    public const string Repository = "OPCFoundation/UA-Nodeset";
    public const string Branch = "latest";
    private static readonly XNamespace Ua = "http://opcfoundation.org/UA/2011/03/UANodeSet.xsd";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    private readonly HttpClient _http;
    private readonly string _folder;

    /// <param name="http">A client; requests carry their own headers, so it may be shared.</param>
    /// <param name="folder">Where the index and the downloaded files are kept.</param>
    public OpcFoundationNodeSets(HttpClient http, string folder)
    {
        _http = http;
        _folder = folder;
    }

    /// <summary>How long a file list from GitHub is taken as current.</summary>
    public TimeSpan ListValidFor { get; init; } = TimeSpan.FromDays(1);

    private string IndexPath => Path.Combine(_folder, "index.json");

    /// <summary>Every model the repository declares, from the index, refreshed when it is older than <see cref="ListValidFor"/> or when asked.</summary>
    public async Task<IReadOnlyList<PublishedModel>> ModelsAsync(bool refresh = false, CancellationToken ct = default)
    {
        var index = ReadIndex();
        if (index != null && !refresh && DateTime.UtcNow - index.Checked < ListValidFor) return index.Models();

        List<(string Path, string Sha)> files;
        try
        {
            files = await ListAsync(ct).ConfigureAwait(false);
        }
        catch (OpcFoundationNodeSetsException) when (index != null)
        {
            // GitHub unreachable or its hourly limit spent: the index on disk still serves.
            return index.Models();
        }

        var known = index?.Files.ToDictionary(f => f.Path, StringComparer.Ordinal) ?? new();
        var entries = new IndexFile[files.Count];
        using var gate = new SemaphoreSlim(8);
        await Task.WhenAll(files.Select(async (f, i) =>
        {
            if (known.TryGetValue(f.Path, out var old) && old.Sha == f.Sha) { entries[i] = old; return; }
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { entries[i] = new IndexFile(f.Path, f.Sha, await ModelsOfAsync(f.Path, ct).ConfigureAwait(false)); }
            finally { gate.Release(); }
        })).ConfigureAwait(false);

        index = new Index(DateTime.UtcNow, entries.ToList());
        WriteIndex(index);
        return index.Models();
    }

    /// <summary>Models whose URI, folder or file name contains every keyword, newest publication first.</summary>
    public static IReadOnlyList<PublishedModel> Search(IEnumerable<PublishedModel> models, IEnumerable<string> keywords)
    {
        var words = keywords.Where(k => !string.IsNullOrWhiteSpace(k) && k != "*").Select(k => k.Trim()).ToList();
        return models
            .Where(m => words.All(w => m.ModelUri.Contains(w, StringComparison.OrdinalIgnoreCase) || m.Path.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(m => m.Folder, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(m => m.PublicationDate)
            .ToList();
    }

    /// <summary>The newest file that declares a model; null when the repository has none.</summary>
    public static PublishedModel? Find(IEnumerable<PublishedModel> models, string modelUri) =>
        models.Where(m => string.Equals(m.ModelUri, modelUri, StringComparison.Ordinal))
            .OrderByDescending(m => m.PublicationDate).ThenBy(m => m.Path.Length).FirstOrDefault();

    /// <summary>
    /// Downloads a model's file and, transitively, the files of the models it
    /// requires that <paramref name="catalog"/> lacks. Returns the files
    /// written, the model's first; models the repository does not have are
    /// named in <paramref name="missing"/>.
    /// </summary>
    public async Task<IReadOnlyList<string>> DownloadWithDependenciesAsync(PublishedModel model, NodeSetCatalog catalog, List<string> missing,
        CancellationToken ct = default)
    {
        var models = await ModelsAsync(false, ct).ConfigureAwait(false);
        var written = new List<string>();
        var pending = new Queue<PublishedModel>(new[] { model });
        var seen = new HashSet<string>(StringComparer.Ordinal) { model.Path };
        while (pending.Count > 0)
        {
            var next = pending.Dequeue();
            var file = await DownloadAsync(next, ct).ConfigureAwait(false);
            catalog.AddFile(file);
            written.Add(file);
            foreach (var required in next.RequiredModels)
            {
                if (catalog.Find(required) != null) continue;
                if (Find(models, required) is not { } found) { if (!missing.Contains(required)) missing.Add(required); continue; }
                if (seen.Add(found.Path)) pending.Enqueue(found);
            }
        }
        return written;
    }

    /// <summary>Downloads the models of <paramref name="modelUris"/> the repository has, with what they require; for an import that misses them.</summary>
    public async Task<IReadOnlyList<string>> DownloadModelsAsync(IEnumerable<string> modelUris, NodeSetCatalog catalog, List<string> missing,
        CancellationToken ct = default)
    {
        var models = await ModelsAsync(false, ct).ConfigureAwait(false);
        var written = new List<string>();
        foreach (var uri in modelUris)
        {
            if (catalog.Find(uri) != null) continue;
            if (Find(models, uri) is not { } model) { missing.Add(uri); continue; }
            written.AddRange(await DownloadWithDependenciesAsync(model, catalog, missing, ct).ConfigureAwait(false));
        }
        return written;
    }

    /// <summary>The whole file of a model, kept in the folder under its repository path.</summary>
    public async Task<string> DownloadAsync(PublishedModel model, CancellationToken ct = default)
    {
        var target = Path.Combine(_folder, "files", model.Path.Replace('/', Path.DirectorySeparatorChar));
        if (!Path.GetFullPath(target).StartsWith(Path.GetFullPath(Path.Combine(_folder, "files")), StringComparison.OrdinalIgnoreCase))
            throw new OpcFoundationNodeSetsException($"'{model.Path}' is not a path inside the repository.");
        var bytes = await GetBytesAsync(RawUrl(model.Path), null, ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, bytes, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new OpcFoundationNodeSetsException($"'{target}' could not be written: {ex.Message}", ex);
        }
        return target;
    }

    private static string RawUrl(string path) =>
        $"https://raw.githubusercontent.com/{Repository}/{Branch}/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}";

    private async Task<List<(string Path, string Sha)>> ListAsync(CancellationToken ct)
    {
        var bytes = await GetBytesAsync($"https://api.github.com/repos/{Repository}/git/trees/{Branch}?recursive=1", null, ct).ConfigureAwait(false);
        try
        {
            var tree = JsonSerializer.Deserialize<TreeDto>(bytes, Json);
            return (tree?.Tree ?? new())
                .Where(e => e.Type == "blob" && e.Path != null && e.Sha != null && e.Path.EndsWith(".nodeset2.xml", StringComparison.OrdinalIgnoreCase))
                .Select(e => (e.Path!, e.Sha!)).ToList();
        }
        catch (JsonException ex)
        {
            throw new OpcFoundationNodeSetsException("GitHub answered with something that is not the repository's file list.", ex);
        }
    }

    /// <summary>The models a file declares, from its beginning: a NodeSet names them before its nodes.</summary>
    private async Task<List<IndexModel>> ModelsOfAsync(string path, CancellationToken ct)
    {
        foreach (var length in new[] { 32 * 1024, 256 * 1024, 0 })
        {
            var bytes = await GetBytesAsync(RawUrl(path), length == 0 ? null : length, ct).ConfigureAwait(false);
            var text = Encoding.UTF8.GetString(bytes);
            var end = text.IndexOf("</Models>", StringComparison.Ordinal);
            if (end < 0)
            {
                if (length == 0 || bytes.Length < length) return new();
                continue;
            }
            try
            {
                // The file up to </Models>, closed again: the root and what comes before the nodes.
                var head = XDocument.Parse(text[..(end + "</Models>".Length)].TrimStart('﻿') + "</UANodeSet>");
                return head.Root!.Element(Ua + "Models")!.Elements(Ua + "Model").Select(m => new IndexModel(
                    (string?)m.Attribute("ModelUri") ?? "",
                    (string?)m.Attribute("Version"),
                    DateTime.TryParse((string?)m.Attribute("PublicationDate"), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) ? d : null,
                    m.Elements(Ua + "RequiredModel").Select(r => (string?)r.Attribute("ModelUri") ?? "").Where(u => u.Length > 0).ToList()))
                    .Where(m => m.ModelUri.Length > 0).ToList();
            }
            catch (System.Xml.XmlException)
            {
                return new();
            }
        }
        return new();
    }

    private async Task<byte[]> GetBytesAsync(string url, int? first, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("AMLOpcUa");
        if (first is { } n) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, n - 1);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new OpcFoundationNodeSetsException($"GitHub cannot be reached: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new OpcFoundationNodeSetsException("GitHub did not answer in time.", ex);
        }
        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                && response.Headers.TryGetValues("X-RateLimit-Remaining", out var left) && left.FirstOrDefault() == "0")
                throw new OpcFoundationNodeSetsException("GitHub allows 60 requests an hour without login and they are used up; try again within the hour.");
            if (!response.IsSuccessStatusCode)
                throw new OpcFoundationNodeSetsException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase} for {url}.");
            return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
    }

    private Index? ReadIndex()
    {
        try
        {
            return File.Exists(IndexPath) ? JsonSerializer.Deserialize<Index>(File.ReadAllText(IndexPath), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void WriteIndex(Index index)
    {
        try
        {
            Directory.CreateDirectory(_folder);
            var temp = IndexPath + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(index, Json));
            File.Move(temp, IndexPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the index on disk the next search builds it again.
        }
    }

    private sealed record IndexModel(string ModelUri, string? Version, DateTime? PublicationDate, List<string> RequiredModels);

    private sealed record IndexFile(string Path, string Sha, List<IndexModel> Models);

    private sealed record Index(DateTime Checked, List<IndexFile> Files)
    {
        public IReadOnlyList<PublishedModel> Models() => Files
            .SelectMany(f => f.Models.Select(m => new PublishedModel(m.ModelUri, m.Version, m.PublicationDate, f.Path, m.RequiredModels)))
            .ToList();
    }

    private sealed class TreeDto
    {
        public List<TreeEntryDto>? Tree { get; set; }
    }

    private sealed class TreeEntryDto
    {
        public string? Path { get; set; }
        public string? Type { get; set; }
        public string? Sha { get; set; }
    }
}
