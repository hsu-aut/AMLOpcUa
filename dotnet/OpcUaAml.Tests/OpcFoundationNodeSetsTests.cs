using System.Net;
using System.Text;
using System.Text.Json;
using OpcUaAml.NodeSets;

namespace OpcUaAml.Tests;

/// <summary>Against a fake of GitHub (the tree API and raw files), with UAFX NodeSets in place of the repository's.</summary>
public class OpcFoundationNodeSetsTests
{
    private const string AcUri = "http://opcfoundation.org/UA/FX/AC/";
    private const string DataUri = "http://opcfoundation.org/UA/FX/Data/";

    private sealed class FakeGitHub : HttpMessageHandler
    {
        public readonly Dictionary<string, string> Files = new()
        {
            ["FX/AC/opc.ua.fx.ac.nodeset2.xml"] = Fixtures.Path("uafx", "opc.ua.fx.ac.nodeset2.xml"),
            ["FX/Data/opc.ua.fx.data.nodeset2.xml"] = Fixtures.Path("uafx", "opc.ua.fx.data.nodeset2.xml"),
        };
        public readonly Dictionary<string, string> Shas = new() { ["FX/AC/opc.ua.fx.ac.nodeset2.xml"] = "a1", ["FX/Data/opc.ua.fx.data.nodeset2.xml"] = "d1" };
        public List<string> Requests { get; } = new();
        public bool LimitSpent { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            if (LimitSpent)
            {
                var spent = new HttpResponseMessage(HttpStatusCode.Forbidden);
                spent.Headers.Add("X-RateLimit-Remaining", "0");
                return Task.FromResult(spent);
            }
            if (url.StartsWith("https://api.github.com/repos/OPCFoundation/UA-Nodeset/git/trees/latest", StringComparison.Ordinal))
            {
                var tree = new
                {
                    sha = "t",
                    tree = Files.Keys.Select(p => new { path = p, type = "blob", sha = Shas[p] })
                        .Append(new { path = "FX", type = "tree", sha = "x" }).Append(new { path = "README.md", type = "blob", sha = "r" }),
                };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(tree)) });
            }
            var path = Uri.UnescapeDataString(url.Replace("https://raw.githubusercontent.com/OPCFoundation/UA-Nodeset/latest/", ""));
            if (!Files.TryGetValue(path, out var file)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var bytes = File.ReadAllBytes(file);
            if (request.Headers.Range?.Ranges.FirstOrDefault() is { To: { } to } && to + 1 < bytes.Length)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bytes[..(int)(to + 1)]) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }

    private static string Folder() => Directory.CreateTempSubdirectory("opcf-nodesets-").FullName;

    [Fact]
    public async Task Indexes_the_models_of_every_file_from_its_beginning()
    {
        var github = new FakeGitHub();
        var source = new OpcFoundationNodeSets(new HttpClient(github), Folder());

        var models = await source.ModelsAsync();

        var ac = Assert.Single(models, m => m.ModelUri == AcUri);
        Assert.Equal("FX/AC", ac.Folder);
        Assert.Contains(DataUri, ac.RequiredModels);
        Assert.Contains(models, m => m.ModelUri == DataUri);
        Assert.Equal(new[] { AcUri }, OpcFoundationNodeSets.Search(models, new[] { "fx", "AC" }).Select(m => m.ModelUri));
        // Headers only: every file was asked for its beginning, none in full.
        Assert.Equal(1 + 2, github.Requests.Count);
    }

    [Fact]
    public async Task Downloads_a_model_with_the_models_it_requires_and_the_import_then_works()
    {
        var github = new FakeGitHub();
        var source = new OpcFoundationNodeSets(new HttpClient(github), Folder());
        var catalog = NodeSetCatalog.Create(Array.Empty<string>());
        var missing = new List<string>();

        var models = await source.ModelsAsync();
        var files = await source.DownloadWithDependenciesAsync(models.Single(m => m.ModelUri == AcUri), catalog, missing);

        // UA and DI are bundled; FX Data had to come from the repository.
        Assert.Equal(new[] { "opc.ua.fx.ac.nodeset2.xml", "opc.ua.fx.data.nodeset2.xml" }, files.Select(Path.GetFileName));
        Assert.Empty(missing);
        Assert.Empty(catalog.MissingDependencies(catalog.Find(AcUri)!));
    }

    [Fact]
    public async Task The_index_is_reused_and_refreshed_only_for_files_that_changed()
    {
        var github = new FakeGitHub();
        var folder = Folder();
        await new OpcFoundationNodeSets(new HttpClient(github), folder).ModelsAsync();
        github.Requests.Clear();

        await new OpcFoundationNodeSets(new HttpClient(github), folder).ModelsAsync();
        Assert.Empty(github.Requests);

        github.Shas["FX/Data/opc.ua.fx.data.nodeset2.xml"] = "d2";
        await new OpcFoundationNodeSets(new HttpClient(github), folder).ModelsAsync(refresh: true);
        Assert.Equal(2, github.Requests.Count); // the list, and the one file that changed
        Assert.Contains(github.Requests, r => r.Contains("fx.data"));
    }

    [Fact]
    public async Task A_spent_hourly_limit_is_explained_and_an_old_index_still_serves()
    {
        var github = new FakeGitHub();
        var folder = Folder();
        var fresh = new OpcFoundationNodeSets(new HttpClient(new FakeGitHub { LimitSpent = true }), Folder());
        var ex = await Assert.ThrowsAsync<OpcFoundationNodeSetsException>(() => fresh.ModelsAsync());
        Assert.Contains("60 requests an hour", ex.Message);

        await new OpcFoundationNodeSets(new HttpClient(github), folder).ModelsAsync();
        github.LimitSpent = true;
        var models = await new OpcFoundationNodeSets(new HttpClient(github), folder).ModelsAsync(refresh: true);
        Assert.Contains(models, m => m.ModelUri == AcUri);
    }
}
