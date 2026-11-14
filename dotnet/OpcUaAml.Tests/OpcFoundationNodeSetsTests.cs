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
        /// <summary>The git blob SHAs of the files, as the tree API gives them.</summary>
        public readonly Dictionary<string, string> Shas;
        public List<string> Requests { get; } = new();
        public bool LimitSpent { get; set; }
        /// <summary>No connection at all, as on a trade fair floor.</summary>
        public bool Unreachable { get; set; }
        /// <summary>Files whose raw requests fail.</summary>
        public HashSet<string> Failing { get; } = new();

        public FakeGitHub()
        {
            Shas = Files.ToDictionary(f => f.Key, f => BlobSha(File.ReadAllBytes(f.Value)));
        }

        public static string BlobSha(byte[] content) =>
            Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
                Encoding.ASCII.GetBytes($"blob {content.Length}\0").Concat(content).ToArray())).ToLowerInvariant();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            if (Unreachable) throw new HttpRequestException("No such host is known.");
            if (Failing.Any(f => Uri.UnescapeDataString(url).EndsWith(f, StringComparison.Ordinal)))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
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

    [Fact]
    public async Task A_file_downloaded_before_is_used_again_while_it_is_current()
    {
        // Every download went to GitHub, even for a file in the cache that
        // had not changed; without a connection the import then failed.
        var github = new FakeGitHub();
        var folder = Folder();
        var models = await new OpcFoundationNodeSets(new HttpClient(github), folder).ModelsAsync();
        var data = models.Single(m => m.ModelUri == DataUri);
        var first = await new OpcFoundationNodeSets(new HttpClient(github), folder).DownloadAsync(data);
        github.Requests.Clear();
        github.Unreachable = true;

        var source = new OpcFoundationNodeSets(new HttpClient(github), folder);
        var again = await source.DownloadAsync(data);

        Assert.Equal(first, again);
        Assert.Empty(github.Requests);
        Assert.Empty(source.Notes);
    }

    [Fact]
    public async Task A_changed_file_is_downloaded_again_and_without_a_connection_the_old_copy_serves()
    {
        var github = new FakeGitHub();
        var folder = Folder();
        var data = (await new OpcFoundationNodeSets(new HttpClient(github), folder).ModelsAsync()).Single(m => m.ModelUri == DataUri);
        await new OpcFoundationNodeSets(new HttpClient(github), folder).DownloadAsync(data);
        github.Shas["FX/Data/opc.ua.fx.data.nodeset2.xml"] = "d2";
        await new OpcFoundationNodeSets(new HttpClient(github), folder).ModelsAsync(refresh: true);
        github.Requests.Clear();

        await new OpcFoundationNodeSets(new HttpClient(github), folder).DownloadAsync(data);
        Assert.Single(github.Requests, r => r.Contains("fx.data"));

        github.Unreachable = true;
        var offline = new OpcFoundationNodeSets(new HttpClient(github), folder);
        var file = await offline.DownloadAsync(data);
        Assert.True(File.Exists(file));
        Assert.Contains(offline.Notes, n => n.Contains("FX/Data/opc.ua.fx.data.nodeset2.xml"));
    }

    [Fact]
    public async Task A_file_that_fails_during_a_refresh_keeps_its_entry_and_the_rest_is_indexed()
    {
        // One failed range request failed the whole list.
        var github = new FakeGitHub();
        var folder = Folder();
        await new OpcFoundationNodeSets(new HttpClient(github), folder).ModelsAsync();
        github.Shas["FX/Data/opc.ua.fx.data.nodeset2.xml"] = "d2";
        github.Shas["FX/AC/opc.ua.fx.ac.nodeset2.xml"] = "a2";
        github.Failing.Add("FX/Data/opc.ua.fx.data.nodeset2.xml");

        var source = new OpcFoundationNodeSets(new HttpClient(github), folder);
        var models = await source.ModelsAsync(refresh: true);

        Assert.Contains(models, m => m.ModelUri == AcUri);
        Assert.Contains(models, m => m.ModelUri == DataUri);
        Assert.Contains(source.Notes, n => n.Contains("FX/Data/opc.ua.fx.data.nodeset2.xml"));

        // The failed file is read again on the next refresh, the other one not.
        github.Failing.Clear();
        github.Requests.Clear();
        await new OpcFoundationNodeSets(new HttpClient(github), folder).ModelsAsync(refresh: true);
        Assert.Equal(2, github.Requests.Count);
        Assert.Contains(github.Requests, r => r.Contains("fx.data"));
    }

    [Fact]
    public async Task Without_a_connection_the_list_from_before_serves_with_a_note()
    {
        var github = new FakeGitHub();
        var folder = Folder();
        await new OpcFoundationNodeSets(new HttpClient(github), folder).ModelsAsync();
        github.Unreachable = true;

        var source = new OpcFoundationNodeSets(new HttpClient(github), folder);
        var models = await source.ModelsAsync(refresh: true);

        Assert.Contains(models, m => m.ModelUri == AcUri);
        Assert.Contains(source.Notes, n => n.Contains("cannot be reached"));
    }

    private static PublishedModel Model(string uri, string path) => new(uri, "1.00", new DateTime(2024, 1, 1), path, Array.Empty<string>());

    private static readonly PublishedModel[] Published =
    {
        Model("http://opcfoundation.org/UA/DI/", "DI/Opc.Ua.Di.NodeSet2.xml"),
        Model("http://opcfoundation.org/UA/PROFINET/", "PROFINET/Opc.Ua.Pn.NodeSet2.xml"),
        Model("http://opcfoundation.org/UA/PNRIO/", "PNRIO/Opc.Ua.PnRio.Nodeset2.xml"),
        Model("http://opcfoundation.org/UA/PlasticsRubber/GeneralTypes/", "PlasticsRubber/GeneralTypes/1.03/Opc.Ua.PlasticsRubber.GeneralTypes.NodeSet2.xml"),
        Model("http://opcfoundation.org/UA/Machinery/", "Machinery/Opc.Ua.Machinery.NodeSet2.xml"),
    };

    [Fact]
    public void A_specification_is_found_by_the_name_it_is_published_under()
    {
        // The repository's folders abbreviate: nobody who types "Device Integration" thinks of "DI".
        Assert.Equal(new[] { "http://opcfoundation.org/UA/DI/" }, OpcFoundationNodeSets.Search(Published, new[] { "Device", "Integration" }).Select(m => m.ModelUri));
        Assert.Equal(new[] { "http://opcfoundation.org/UA/PNRIO/" }, OpcFoundationNodeSets.Search(Published, new[] { "PROFINET", "Remote", "IO" }).Select(m => m.ModelUri));
        Assert.Equal("Devices (Device Integration)", Published[0].Title);
        Assert.Null(Published[4].Title);
    }

    [Fact]
    public void Small_words_of_a_name_do_not_keep_it_from_being_found()
    {
        Assert.Equal(new[] { "http://opcfoundation.org/UA/PlasticsRubber/GeneralTypes/" },
            OpcFoundationNodeSets.Search(Published, new[] { "plastics", "and", "rubber" }).Select(m => m.ModelUri));
    }
}
