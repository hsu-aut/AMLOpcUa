using Aml.Engine.CAEX;
using OpcUaAml.Import;
using OpcUaAml.NodeSets;

namespace OpcUaAml.Tests;

public class VersionTests(BundledDiConversion di) : IClassFixture<BundledDiConversion>
{
    [Fact]
    public void Libraries_record_the_model_they_came_from()
    {
        var suc = di.Result.Document.CAEXFile.SystemUnitClassLib["SUC_http://opcfoundation.org/UA/DI/"];

        var info = OpcUaLibraryInfo.Read(suc);

        Assert.NotNull(info);
        Assert.Equal(Fixtures.DiUri, info!.NamespaceUri);
        Assert.Equal("1.05.0", info.ModelVersion);
        Assert.Equal(new DateTime(2025, 11, 15, 0, 0, 0, DateTimeKind.Utc), info.PublicationDate);
    }

    [Fact]
    public void The_overview_lists_each_namespace_once()
    {
        var overview = NamespaceOverview.Of(di.Result.Document);

        Assert.Equal(new[] { Fixtures.UaUri, Fixtures.DiUri }, overview.Select(e => e.NamespaceUri).ToArray());
        var entry = overview.Single(e => e.NamespaceUri == Fixtures.DiUri);
        Assert.Equal("1.05.0", entry.ModelVersion);
        Assert.Equal(4, entry.Libraries.Count);
    }

    [Fact]
    public void An_older_model_does_not_replace_a_newer_one()
    {
        var target = CAEXDocument.New_CAEXDocument();
        LibraryMerger.Merge(target, di.Result.Document);

        var oldCatalog = NodeSetCatalog.Create(new[] { Fixtures.Path("oracle-inputs") }, includeBundled: false);
        var older = NodeSetImporter.Convert(Fixtures.Path("oracle-inputs", "Opc.Ua.Di.NodeSet2.xml"), oldCatalog);

        var report = LibraryMerger.Merge(target, older.Document);

        var diChanges = report.Changes.Where(c => c.Name.EndsWith("/UA/DI/")).ToList();
        Assert.Equal(4, diChanges.Count);
        Assert.All(diChanges, c =>
        {
            Assert.Equal(LibraryAction.Kept, c.Action);
            Assert.Contains("1.05.0", c.Note);
        });
        Assert.Equal("1.05.0",
            OpcUaLibraryInfo.Read(target.CAEXFile.SystemUnitClassLib["SUC_http://opcfoundation.org/UA/DI/"])!.ModelVersion);

        var forced = LibraryMerger.Merge(target, older.Document, new MergeOptions { AllowDowngrade = true });
        Assert.All(forced.Changes.Where(c => c.Name.EndsWith("/UA/DI/")), c => Assert.Equal(LibraryAction.Replaced, c.Action));
    }
}
