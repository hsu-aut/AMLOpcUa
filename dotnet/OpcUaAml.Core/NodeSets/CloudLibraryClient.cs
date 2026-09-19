// The OPC Foundation's UA Cloud Library (uacloudlibrary.opcfoundation.org):
// search published information models and download them together with the
// models they require, into a folder the NodeSetCatalog can use.
//
// REST API v1 as described by its swagger: GET /infomodel/find2 and
// GET /infomodel/download/{identifier}, and PUT /infomodel/upload to publish a
// model (the body the server's UANameSpace class reads: title, copyrightText
// and description are required, the NodeSet XML goes in nodeset.nodesetXml;
// the OPC Foundation reviews an upload before it is listed). Access needs an
// account (HTTP basic authentication) or an API key; anonymous requests are
// answered with 401.

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

/// <summary>What the Cloud Library asks about a model when it is published.</summary>
public sealed record CloudUpload(string Title, string Description, string CopyrightText)
{
    /// <summary>MIT, ApacheLicense20 or Custom, the values the Cloud Library knows.</summary>
    public string License { get; init; } = "MIT";
    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();
    public Uri? DocumentationUrl { get; init; }
    public Uri? LicenseUrl { get; init; }
}

public sealed class CloudLibraryClient
{
    public const string DefaultBaseUrl = "https://uacloudlibrary.opcfoundation.org/";

    private readonly HttpClient _http;
    private readonly AuthenticationHeaderValue? _authorization;
    private readonly string? _apiKey;

    /// <summary>
    /// A client for the Cloud Library that follows no redirect: the credentials
    /// are sent with every request, and a redirect to another host would carry
    /// the API key there.
    /// </summary>
    public static HttpClient CreateHttp() =>
        new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };

    /// <param name="http">A client, which may be shared: the credentials go with each request, not into it. Its BaseAddress is set to <paramref name="baseUrl"/> if missing.</param>
    /// <param name="userName">Account for basic authentication, or null when an API key is used.</param>
    public CloudLibraryClient(HttpClient http, string? userName = null, string? password = null, string? apiKey = null,
        string baseUrl = DefaultBaseUrl)
    {
        _http = http;
        _http.BaseAddress ??= new Uri(baseUrl);
        if (userName != null)
            _authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{password}")));
        _apiKey = apiKey;
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
            try
            {
                await File.WriteAllTextAsync(file, xml, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new CloudLibraryException($"'{file}' could not be written: {ex.Message}", ex);
            }
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

    /// <summary>
    /// Publishes a NodeSet with its description. Returns the library's answer.
    /// With <paramref name="overwrite"/> an earlier upload of the same model by
    /// the same contributor is replaced; without it, that is refused.
    /// </summary>
    public async Task<string> UploadAsync(string nodeSetXml, CloudUpload metadata, bool overwrite = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(metadata.Title) || string.IsNullOrWhiteSpace(metadata.Description) || string.IsNullOrWhiteSpace(metadata.CopyrightText))
            throw new CloudLibraryException("The Cloud Library needs a title, a description and a copyright text.");
        if (metadata.License is not ("MIT" or "ApacheLicense20" or "Custom"))
            throw new CloudLibraryException($"The Cloud Library knows the licenses MIT, ApacheLicense20 and Custom, not '{metadata.License}'.");
        var body = new Dictionary<string, object?>
        {
            ["title"] = metadata.Title.Trim(),
            ["license"] = metadata.License,
            ["copyrightText"] = metadata.CopyrightText.Trim(),
            ["description"] = metadata.Description.Trim(),
            ["keywords"] = metadata.Keywords.Where(k => k.Trim().Length > 0).Select(k => k.Trim()).ToArray(),
            ["documentationUrl"] = metadata.DocumentationUrl,
            ["licenseUrl"] = metadata.LicenseUrl,
            ["nodeset"] = new Dictionary<string, object?> { ["nodesetXml"] = nodeSetXml },
        };
        using var request = new HttpRequestMessage(HttpMethod.Put, $"infomodel/upload?overwrite={(overwrite ? "true" : "false")}")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = _authorization;
        if (_apiKey != null) request.Headers.Add("X-API-Key", _apiKey);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new CloudLibraryException($"The Cloud Library cannot be reached: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new CloudLibraryException($"The Cloud Library did not answer within {_http.Timeout.TotalSeconds:0} s.", ex);
        }
        using (response)
        {
            var text = (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim().Trim('"');
            return response.StatusCode switch
            {
                HttpStatusCode.OK or HttpStatusCode.Created => text.Length > 0 ? text : "Uploaded.",
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    throw new CloudLibraryException("The Cloud Library refused the credentials. Publishing needs an account or an API key (uacloudlibrary.opcfoundation.org)."),
                HttpStatusCode.Conflict =>
                    throw new CloudLibraryException("The Cloud Library already holds this model, uploaded by you or another contributor. Replace your earlier upload, or publish a new version."),
                HttpStatusCode.BadRequest or HttpStatusCode.NotFound =>
                    throw new CloudLibraryException($"The Cloud Library did not accept the NodeSet: {(text.Length > 0 ? text : response.ReasonPhrase)}"),
                _ when (int)response.StatusCode is >= 300 and < 400 =>
                    throw new CloudLibraryException($"The Cloud Library redirects to {response.Headers.Location}; it is not followed, the credentials would go along."),
                _ => throw new CloudLibraryException($"The Cloud Library answered {(int)response.StatusCode} {response.ReasonPhrase}{(text.Length > 0 ? ": " + text : "")}."),
            };
        }
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
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = _authorization;
            if (_apiKey != null) request.Headers.Add("X-API-Key", _apiKey);
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new CloudLibraryException($"The Cloud Library cannot be reached: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new CloudLibraryException($"The Cloud Library did not answer within {_http.Timeout.TotalSeconds:0} s.", ex);
        }
        using (response)
        {
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new CloudLibraryException($"The Cloud Library redirects to {response.Headers.Location}; it is not followed, the credentials would go along.");
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new CloudLibraryException("The Cloud Library refused the credentials. It needs an account or an API key (uacloudlibrary.opcfoundation.org).");
            if (response.StatusCode == HttpStatusCode.NotFound) return default;
            if (!response.IsSuccessStatusCode)
                throw new CloudLibraryException($"The Cloud Library answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            try
            {
                var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw new CloudLibraryException($"The Cloud Library answered something that is not a model description: {ex.Message}", ex);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                throw new CloudLibraryException($"The answer of the Cloud Library broke off: {ex.Message}", ex);
            }
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
