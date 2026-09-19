using System.Net;
using System.Text;
using System.Text.Json;
using OpcUaAml.NodeSets;

namespace OpcUaAml.Tests;

/// <summary>
/// Against a fake of the Cloud Library REST API (responses shaped like its
/// swagger, v1): the real service needs an account.
/// </summary>
public class CloudLibraryTests
{
    private const string AcUri = "http://opcfoundation.org/UA/FX/AC/";
    private const string DataUri = "http://opcfoundation.org/UA/FX/Data/";

    private sealed class FakeLibrary : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();
        public string? Authorization { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public Func<HttpResponseMessage>? Answer { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            Authorization = request.Headers.Authorization?.ToString();
            if (Answer != null) return Task.FromResult(Answer());
            if (Status != HttpStatusCode.OK) return Task.FromResult(new HttpResponseMessage(Status));

            var path = request.RequestUri.AbsolutePath;
            object? body = path switch
            {
                "/infomodel/find2" when request.RequestUri.Query.Contains(Uri.EscapeDataString(DataUri)) =>
                    new[] { Entry(20, DataUri, withXml: false) },
                "/infomodel/find2" => new[] { Entry(10, AcUri, withXml: false) },
                "/infomodel/download/10" => Entry(10, AcUri, withXml: true),
                "/infomodel/download/20" => Entry(20, DataUri, withXml: true),
                _ => null,
            };
            var response = body == null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
                };
            return Task.FromResult(response);
        }

        private static object Entry(int id, string uri, bool withXml)
        {
            var file = uri == AcUri ? "opc.ua.fx.ac.nodeset2.xml" : "opc.ua.fx.data.nodeset2.xml";
            var required = uri == AcUri
                ? new object[]
                {
                    new { namespaceUri = "http://opcfoundation.org/UA/", version = "1.05.06" },
                    new { namespaceUri = "http://opcfoundation.org/UA/DI/", version = "1.05.0" },
                    new { namespaceUri = DataUri, version = "1.00.04" },
                }
                : new object[] { new { namespaceUri = "http://opcfoundation.org/UA/", version = "1.05.06" } };
            return new
            {
                title = uri == AcUri ? "UAFX AC" : "UAFX Data",
                nodeset = new
                {
                    identifier = id,
                    namespaceUri = uri,
                    version = "1.00.04",
                    publicationDate = "2026-07-22T00:00:00Z",
                    nodesetXml = withXml ? File.ReadAllText(Fixtures.Path("uafx", file)) : null,
                    requiredModels = required,
                },
            };
        }
    }

    [Fact]
    public async Task Search_sends_keywords_and_credentials_and_reads_the_models()
    {
        var fake = new FakeLibrary();
        var client = new CloudLibraryClient(new HttpClient(fake), "user", "secret");

        var models = await client.SearchAsync(new[] { "FX", "AC" });

        var m = Assert.Single(models);
        Assert.Equal(AcUri, m.NamespaceUri);
        Assert.Equal(10, m.Identifier);
        Assert.Equal(3, m.RequiredModels.Count);
        Assert.Contains("keywords=FX&keywords=AC", fake.Requests[0]);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("user:secret")), fake.Authorization);
    }

    [Fact]
    public async Task Download_fetches_missing_dependencies_and_the_import_then_works()
    {
        var fake = new FakeLibrary();
        var client = new CloudLibraryClient(new HttpClient(fake), apiKey: "key");
        var folder = Directory.CreateTempSubdirectory("cloudlib-").FullName;
        var catalog = NodeSetCatalog.Create(new[] { folder });

        var files = await client.DownloadWithDependenciesAsync(10, folder, catalog);

        // UA and DI are bundled; only FX Data had to come from the library,
        // found through a search by namespace because no identifier was given.
        Assert.Equal(new[] { "opcfoundation.org_UA_FX_AC.NodeSet2.xml", "opcfoundation.org_UA_FX_Data.NodeSet2.xml" },
            files.Select(Path.GetFileName));
        Assert.Contains(fake.Requests, r => r.StartsWith("/infomodel/find2") && r.Contains(Uri.EscapeDataString(DataUri)));
        Assert.Empty(catalog.MissingDependencies(catalog.Find(AcUri)!));
    }

    [Fact]
    public async Task Refused_credentials_say_what_is_needed()
    {
        var fake = new FakeLibrary { Status = HttpStatusCode.Unauthorized };
        var client = new CloudLibraryClient(new HttpClient(fake));

        var ex = await Assert.ThrowsAsync<CloudLibraryException>(() => client.SearchAsync(new[] { "DI" }));

        Assert.Contains("account or an API key", ex.Message);
    }

    private sealed class FakeUpload : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string? Body { get; private set; }
        public HttpRequestMessage? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(Status) { Content = new StringContent(Status == HttpStatusCode.OK ? "\"4711\"" : "The nodeset failed verification") };
        }
    }

    [Fact]
    public async Task Uploads_a_model_with_the_fields_the_library_requires()
    {
        var fake = new FakeUpload();
        var client = new CloudLibraryClient(new HttpClient(fake), apiKey: "key");
        var xml = File.ReadAllText(Fixtures.Path("uafx", "opc.ua.fx.data.nodeset2.xml"));

        var answer = await client.UploadAsync(xml, new CloudUpload("FX Data", "Data types of UAFX", "(c) OPC Foundation")
        {
            Keywords = new[] { "UAFX", " ", "Data" },
        }, overwrite: true);

        Assert.Equal("4711", answer);
        Assert.Equal(HttpMethod.Put, fake.Request!.Method);
        Assert.Equal("/infomodel/upload?overwrite=true", fake.Request.RequestUri!.PathAndQuery);
        Assert.Equal("key", fake.Request.Headers.GetValues("X-API-Key").Single());
        using var json = JsonDocument.Parse(fake.Body!);
        var root = json.RootElement;
        Assert.Equal("FX Data", root.GetProperty("title").GetString());
        Assert.Equal("MIT", root.GetProperty("license").GetString());
        Assert.Equal("(c) OPC Foundation", root.GetProperty("copyrightText").GetString());
        Assert.Equal(new[] { "UAFX", "Data" }, root.GetProperty("keywords").EnumerateArray().Select(k => k.GetString()));
        Assert.Equal(xml, root.GetProperty("nodeset").GetProperty("nodesetXml").GetString());
    }

    [Fact]
    public async Task An_upload_that_is_refused_says_why()
    {
        var xml = "<UANodeSet/>";
        var meta = new CloudUpload("T", "D", "C");
        await Assert.ThrowsAsync<CloudLibraryException>(() => new CloudLibraryClient(new HttpClient(new FakeUpload())).UploadAsync(xml, meta with { Title = " " }));
        await Assert.ThrowsAsync<CloudLibraryException>(() => new CloudLibraryClient(new HttpClient(new FakeUpload())).UploadAsync(xml, meta with { License = "GPL" }));
        var conflict = await Assert.ThrowsAsync<CloudLibraryException>(() =>
            new CloudLibraryClient(new HttpClient(new FakeUpload { Status = HttpStatusCode.Conflict })).UploadAsync(xml, meta));
        Assert.Contains("already holds", conflict.Message);
        var invalid = await Assert.ThrowsAsync<CloudLibraryException>(() =>
            new CloudLibraryClient(new HttpClient(new FakeUpload { Status = HttpStatusCode.NotFound })).UploadAsync(xml, meta));
        Assert.Contains("failed verification", invalid.Message);
    }

    [Fact]
    public async Task A_garbled_answer_or_a_redirect_is_a_library_error()
    {
        var garbled = new FakeLibrary { Answer = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>maintenance</html>") } };
        var redirect = new FakeLibrary
        {
            Answer = () => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://elsewhere.example/") } },
        };

        await Assert.ThrowsAsync<CloudLibraryException>(() => new CloudLibraryClient(new HttpClient(garbled)).SearchAsync(new[] { "DI" }));
        var ex = await Assert.ThrowsAsync<CloudLibraryException>(() => new CloudLibraryClient(new HttpClient(redirect), apiKey: "key").SearchAsync(new[] { "DI" }));
        Assert.Contains("elsewhere.example", ex.Message);
    }
}
