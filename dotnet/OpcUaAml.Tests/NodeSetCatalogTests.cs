using OpcUaAml.NodeSets;

namespace OpcUaAml.Tests;

public class NodeSetCatalogTests
{
    [Fact]
    public void Reads_the_header_of_a_NodeSet()
    {
        var info = NodeSetInfo.Read(Path.Combine(NodeSetCatalog.BundledFolder, "Opc.Ua.Di.NodeSet2.xml"));

        var model = Assert.Single(info.Models);
        Assert.Equal(Fixtures.DiUri, model.Model.ModelUri);
        Assert.Equal("1.05.0", model.Model.Version);
        Assert.Equal(new DateTime(2025, 11, 15, 0, 0, 0, DateTimeKind.Utc), model.Model.PublicationDate);
        Assert.Equal(Fixtures.UaUri, Assert.Single(model.RequiredModels).ModelUri);
    }

    [Fact]
    public void Rejects_files_that_are_not_NodeSets()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "<CAEXFile SchemaVersion=\"3.0\" />");
            Assert.Null(NodeSetInfo.TryRead(file));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Bundles_the_base_model_and_DI()
    {
        var catalog = NodeSetCatalog.Create(Array.Empty<string>());
        Assert.NotNull(catalog.Find(Fixtures.UaUri));
        Assert.NotNull(catalog.Find(Fixtures.DiUri));
    }

    [Fact]
    public void Reports_missing_dependencies_transitively()
    {
        // FX AC needs FX Data, DI and the base model. Only the base model and
        // DI are bundled, so FX Data is the one gap.
        var catalog = NodeSetCatalog.Create(Array.Empty<string>());
        var ac = catalog.AddFile(Fixtures.Path("uafx", "opc.ua.fx.ac.nodeset2.xml"))!;

        var missing = catalog.MissingDependencies(ac);

        Assert.Equal("http://opcfoundation.org/UA/FX/Data/", Assert.Single(missing).ModelUri);
    }

    [Fact]
    public void A_folder_closes_the_gap()
    {
        var catalog = NodeSetCatalog.Create(new[] { Fixtures.Path("uafx") });
        var ac = catalog.Find("http://opcfoundation.org/UA/FX/AC/")!;

        Assert.Empty(catalog.MissingDependencies(ac));
    }

    [Fact]
    public void The_newer_publication_wins()
    {
        // oracle-inputs holds UA 1.05.05, the bundle 1.05.07.
        var catalog = NodeSetCatalog.Create(new[] { Fixtures.Path("oracle-inputs") });

        var ua = catalog.Find(Fixtures.UaUri)!;
        Assert.Equal("1.05.07", ua.PrimaryModel.Model.Version);
        Assert.Contains(catalog.Warnings, w => w.Contains(Fixtures.UaUri));
    }

    [Fact]
    public void A_missing_folder_is_a_warning_not_an_error()
    {
        var catalog = NodeSetCatalog.Create(new[] { @"Z:\does\not\exist" });
        Assert.Contains(catalog.Warnings, w => w.Contains("does not exist"));
    }
}
