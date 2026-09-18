// The OPC Foundation's UA Cloud Library (uacloudlibrary.opcfoundation.org):
// search published information models and download them together with the
// models they require, into a folder the NodeSetCatalog can use.
//
// REST API v1 as described by its swagger: GET /infomodel/find2 and
// GET /infomodel/download/{identifier}. Access needs an account (HTTP basic
// authentication) or an API key; anonymous requests are answered with 401.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpcUaAml.NodeSets;

public sealed class CloudLibraryException : Exception
{
    public CloudLibraryException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>A published information model as the Cloud Library lists it.</summary>
public sealed record CloudModel(
    int Identifier,
    string NamespaceUri,
    string? Title,
    string? Version,
    DateTime? PublicationDate,
    IReadOnlyList<CloudRequiredModel> RequiredModels);

public sealed record CloudRequiredModel(string NamespaceUri, string? Version, DateTime? PublicationDate, int? AvailableIdentifier);

public sealed class CloudLibraryClient
{
    public const string DefaultBaseUrl = "https://uacloudlibrary.opcfoundation.org/";

    private readonly HttpClient _http;

    /// <param name="http">A client; its BaseAddress is set to <paramref name="baseUrl"/> if missing.</param>
    /// <param name="userName">Account for basic authentication, or null when an API key is used.</param>
    public CloudLibraryClient(HttpClient http, string? userName = null, string? password = null, string? apiKey = null,
        string baseUrl = DefaultBaseUrl)
    {
        _http = http;
        _http.BaseAddress ??= new Uri(baseUrl);
        if (userName != null)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{password}")));
        if (apiKey != null)
            _http.DefaultRequestHeaders.Add("X-API-Key", apiKey);
    }

    /// <summary>Models matching all keywords (<c>*</c> lists everything), optionally one namespace.</summary>
    public async Task<IReadOnlyList<CloudModel>> SearchAsync(IEnumerable<string> keywords, string? namespaceUri = null,
        int limit = 50, CancellationToken ct = default)
    {
        var query = new StringBuilder("infomodel/find2?");
        foreach (var k in keywords.Where(k => !string.IsNullOrWhiteSpace(k)))
            query.Append("keywords=").Append(Uri.EscapeDataString(k.Trim())).Append('&');
        if (namespaceUri != null) query.Append("namespaceUri=").Append(Uri.EscapeDataString(namespaceUri)).Append('&');
        query.Append("limit=").Append(limit);

        var entries = await GetAsync<List<NameSpaceDto>>(query.ToString(), ct).ConfigureAwait(false);
        return entries?.Where(e => e.Nodeset != null).Select(ToModel).ToList() ?? new List<CloudModel>();
    }

    /// <summary>The NodeSet XML of one model.</summary>
    public async Task<(CloudModel Model, string NodeSetXml)> DownloadAsync(int identifier, CancellationToken ct = default)
    {
        var entry = await GetAsync<NameSpaceDto>($"infomodel/download/{identifier}", ct).ConfigureAwait(false)
            ?? throw new CloudLibraryException($"Model {identifier} not found in the Cloud Library.");
        if (entry.Nodeset?.NodesetXml is not { Length: > 0 } xml)
            throw new CloudLibraryException($"Model {identifier} came without NodeSet XML.");
        return (ToModel(entry), xml);
    }

    /// <summary>
    /// Downloads a model and, transitively, every required model the catalog
    /// does not know yet, into <paramref name="folder"/>. Returns the files
    /// written, the requested model first.
    /// </summary>
    public async Task<IReadOnlyList<string>> DownloadWithDependenciesAsync(int identifier, string folder, NodeSetCatalog catalog,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        var written = new List<string>();
        var pending = new Queue<int>();
        var seen = new HashSet<int>();
        pending.Enqueue(identifier);

        while (pending.Count > 0)
        {
            var id = pending.Dequeue();
            if (!seen.Add(id)) continue;
            var (model, xml) = await DownloadAsync(id, ct).ConfigureAwait(false);
            var file = Path.Combine(folder, FileNameFor(model));
            await File.WriteAllTextAsync(file, xml, ct).ConfigureAwait(false);
            catalog.AddFile(file);
            written.Add(file);

            foreach (var req in model.RequiredModels)
            {
                if (catalog.Find(req.NamespaceUri) != null) continue;
                var next = req.AvailableIdentifier
                    ?? (await SearchAsync(new[] { "*" }, req.NamespaceUri, 10, ct).ConfigureAwait(false))
                        .OrderByDescending(m => m.PublicationDate).FirstOrDefault()?.Identifier
                    ?? throw new CloudLibraryException($"'{model.NamespaceUri}' requires '{req.NamespaceUri}', which the Cloud Library does not offer.");
                pending.Enqueue(next);
            }
        }
        return written;
    }

    /// <summary>A file name from the namespace URI, e.g. <c>opcfoundation.org_UA_Machinery.NodeSet2.xml</c>.</summary>
    public static string FileNameFor(CloudModel model)
    {
        var uri = model.NamespaceUri;
        var start = uri.IndexOf("://", StringComparison.Ordinal);
        var name = (start >= 0 ? uri[(start + 3)..] : uri).TrimEnd('/');
        foreach (var c in Path.GetInvalidFileNameChars().Append('/')) name = name.Replace(c, '_');
        return name + ".NodeSet2.xml";
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(path, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new CloudLibraryException($"The Cloud Library cannot be reached: {ex.Message}", ex);
        }
        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new CloudLibraryException("The Cloud Library refused the credentials. It needs an account or an API key (uacloudlibrary.opcfoundation.org).");
            if (response.StatusCode == HttpStatusCode.NotFound) return default;
            if (!response.IsSuccessStatusCode)
                throw new CloudLibraryException($"The Cloud Library answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct).ConfigureAwait(false);
        }
    }

    private static CloudModel ToModel(NameSpaceDto e) => new(
        e.Nodeset!.Identifier,
        e.Nodeset.NamespaceUri ?? "",
        e.Title,
        e.Nodeset.Version,
        e.Nodeset.PublicationDate,
        (e.Nodeset.RequiredModels ?? new()).Select(r => new CloudRequiredModel(
            r.NamespaceUri ?? "", r.Version, r.PublicationDate, r.AvailableModel?.Identifier)).ToList());

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed class NameSpaceDto
    {
        public string? Title { get; set; }
        public NodesetDto? Nodeset { get; set; }
    }

    private sealed class NodesetDto
    {
        public string? NodesetXml { get; set; }
        public int Identifier { get; set; }
        public string? NamespaceUri { get; set; }
        public string? Version { get; set; }
        public DateTime? PublicationDate { get; set; }
        public List<RequiredDto>? RequiredModels { get; set; }
    }

    private sealed class RequiredDto
    {
        public string? NamespaceUri { get; set; }
        public string? Version { get; set; }
        public DateTime? PublicationDate { get; set; }
        [JsonPropertyName("availableModel")]
        public NodesetDto? AvailableModel { get; set; }
    }
}
