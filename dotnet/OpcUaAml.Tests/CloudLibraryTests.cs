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

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            Authorization = request.Headers.Authorization?.ToString();
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
}
