using OpcUaAml.Import;
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

    [Fact]
    public void A_file_that_cannot_be_read_is_skipped_with_a_warning()
    {
        // A file another program holds open, or one without read rights, threw
        // out of the folder scan: the whole catalog failed, the import with it.
        var folder = Directory.CreateTempSubdirectory("catalog-locked-").FullName;
        try
        {
            File.Copy(Fixtures.Path("uafx", "opc.ua.fx.data.nodeset2.xml"), Path.Combine(folder, "opc.ua.fx.data.nodeset2.xml"));
            var locked = Path.Combine(folder, "locked.nodeset2.xml");
            File.Copy(Fixtures.Path("uafx", "opc.ua.fx.ac.nodeset2.xml"), locked);
            using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var catalog = NodeSetCatalog.Create(new[] { folder });

                Assert.NotNull(catalog.Find("http://opcfoundation.org/UA/FX/Data/"));
                Assert.Null(catalog.Find("http://opcfoundation.org/UA/FX/AC/"));
                Assert.Contains(catalog.Warnings, w => w.Contains("locked.nodeset2.xml") && w.Contains("could not be read"));
                // Imported directly, the file is refused the way the import refuses files.
                var ex = Assert.Throws<ImportException>(() => NodeSetImporter.Convert(locked, catalog));
                Assert.Contains("could not be read", ex.Message);
            }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void The_bundled_folder_is_next_to_the_library_not_the_program()
    {
        // As in the AML Editor: the program runs from its own folder, the
        // plugin's libraries and their nodesets folder lie elsewhere.
        var folder = Directory.CreateTempSubdirectory("bundled-").FullName;
        try
        {
            var copy = Path.Combine(folder, "OpcUaAml.Core.dll");
            File.Copy(typeof(NodeSetCatalog).Assembly.Location, copy);
            var context = new System.Runtime.Loader.AssemblyLoadContext("elsewhere", isCollectible: true);
            try
            {
                var catalog = context.LoadFromAssemblyPath(copy).GetType(typeof(NodeSetCatalog).FullName!)!;
                var bundled = (string)catalog.GetProperty(nameof(NodeSetCatalog.BundledFolder))!.GetValue(null)!;

                Assert.Equal(Path.Combine(folder, "nodesets"), bundled);
            }
            finally { context.Unload(); }
        }
        finally
        {
            try { Directory.Delete(folder, true); }
            catch (UnauthorizedAccessException) { /* still loaded until collected */ }
        }
    }
}
