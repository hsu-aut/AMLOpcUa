using Aml.Engine.CAEX;
using OpcUaAml.Import;

namespace OpcUaAml.Tests;

public class ImportTests(BundledDiConversion di) : IClassFixture<BundledDiConversion>
{
    private static readonly string[] GeneratedDiLibraries =
    {
        "ATL_OpcAmlMetaModel", "ATL_http://opcfoundation.org/UA/", "ATL_http://opcfoundation.org/UA/DI/",
        "ICL_http://opcfoundation.org/UA/", "ICL_http://opcfoundation.org/UA/DI/",
        "RCL_OpcAmlMetaModel", "RCL_http://opcfoundation.org/UA/", "RCL_http://opcfoundation.org/UA/DI/",
        "SUC_OpcAmlMetaModel", "SUC_http://opcfoundation.org/UA/", "SUC_http://opcfoundation.org/UA/DI/",
    };

    private static IEnumerable<string> LibraryNames(CAEXDocument doc) =>
        doc.CAEXFile.AttributeTypeLib.Select(l => l.Name)
            .Concat(doc.CAEXFile.InterfaceClassLib.Select(l => l.Name))
            .Concat(doc.CAEXFile.RoleClassLib.Select(l => l.Name))
            .Concat(doc.CAEXFile.SystemUnitClassLib.Select(l => l.Name));

    [Fact]
    public void Conversion_yields_one_library_set_per_namespace_in_CAEX_3()
    {
        var doc = di.Result.Document;
        Assert.Equal("3.0", doc.CAEXFile.SchemaVersion);
        Assert.Subset(LibraryNames(doc).ToHashSet(), GeneratedDiLibraries.ToHashSet());
    }

    [Fact]
    public void DI_types_become_system_unit_classes()
    {
        var suc = di.Result.Document.CAEXFile.SystemUnitClassLib["SUC_http://opcfoundation.org/UA/DI/"];
        Assert.NotNull(suc);
        Assert.NotNull(suc.SystemUnitClass["DeviceType"]);
        Assert.NotNull(suc.SystemUnitClass["TopologyElementType"]);
    }

    [Fact]
    public void DI_105_ConnectsTo_placeholders_are_skipped_with_a_warning()
    {
        // DI 1.05.0 made ConnectsTo non-hierarchical; the two placeholders it
        // points at have no element to carry the interface (patch 0001).
        Assert.Equal(2, di.Result.Warnings.Count);
        Assert.All(di.Result.Warnings, w => Assert.Contains("ConnectsTo", w));
        Assert.Contains(di.Result.Warnings, w => w.Contains("<CPIdentifier>"));
        Assert.Contains(di.Result.Warnings, w => w.Contains("<NetworkIdentifier>"));
    }

    [Fact]
    public void Merge_adds_every_library_to_an_empty_document()
    {
        var target = CAEXDocument.New_CAEXDocument();

        var report = LibraryMerger.Merge(target, di.Result.Document);

        Assert.Equal(0, report.Replaced);
        Assert.Subset(LibraryNames(target).ToHashSet(), GeneratedDiLibraries.ToHashSet());
        Assert.Equal(LibraryNames(di.Result.Document).Count(), LibraryNames(target).Count());
    }

    [Fact]
    public void Merge_keeps_IDs_so_a_second_import_hits_the_same_classes()
    {
        var target = CAEXDocument.New_CAEXDocument();
        LibraryMerger.Merge(target, di.Result.Document);

        var original = di.Result.Document.CAEXFile.SystemUnitClassLib["SUC_http://opcfoundation.org/UA/DI/"]
            .SystemUnitClass["DeviceType"].ID;
        var merged = target.CAEXFile.SystemUnitClassLib["SUC_http://opcfoundation.org/UA/DI/"]
            .SystemUnitClass["DeviceType"].ID;

        Assert.Equal(original, merged);
    }

    [Fact]
    public void Importing_twice_replaces_instead_of_duplicating()
    {
        var target = CAEXDocument.New_CAEXDocument();
        LibraryMerger.Merge(target, di.Result.Document);
        var countAfterFirst = LibraryNames(target).Count();

        var second = LibraryMerger.Merge(target, di.Result.Document);

        Assert.Equal(countAfterFirst, LibraryNames(target).Count());
        Assert.Equal(GeneratedDiLibraries.Length, second.Replaced);
        Assert.Equal(0, second.Added);
        Assert.All(LibraryNames(target).GroupBy(n => n), g => Assert.Single(g));
    }

    [Fact]
    public void AutomationML_base_libraries_are_never_replaced()
    {
        var target = CAEXDocument.New_CAEXDocument();
        LibraryMerger.Merge(target, di.Result.Document);

        var second = LibraryMerger.Merge(target, di.Result.Document);

        Assert.All(second.Changes.Where(c => c.Name.StartsWith("AutomationML")),
            c => Assert.Equal(LibraryAction.Kept, c.Action));
    }

    [Fact]
    public void Keep_policy_leaves_existing_generated_libraries_alone()
    {
        var target = CAEXDocument.New_CAEXDocument();
        LibraryMerger.Merge(target, di.Result.Document);

        var second = LibraryMerger.Merge(target, di.Result.Document, new MergeOptions { ReplaceGeneratedLibraries = false });

        Assert.Equal(0, second.Replaced);
        Assert.All(second.Changes, c => Assert.Equal(LibraryAction.Kept, c.Action));
    }

    [Fact]
    public void A_replaced_library_stays_in_place()
    {
        var target = CAEXDocument.New_CAEXDocument();
        LibraryMerger.Merge(target, di.Result.Document);
        var before = target.CAEXFile.SystemUnitClassLib.Select(l => l.Name).ToList();

        LibraryMerger.Merge(target, di.Result.Document);

        Assert.Equal(before, target.CAEXFile.SystemUnitClassLib.Select(l => l.Name).ToList());
    }

    [Fact]
    public void A_CAEX_2_15_document_is_refused_before_any_conversion()
    {
        var target = CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX2_15);
        var diFile = di.Catalog.Find(Fixtures.DiUri)!.FilePath;

        var ex = Assert.Throws<ImportException>(() => OpcUaImport.ImportInto(target, diFile, di.Catalog));

        Assert.Contains("CAEX 2.15", ex.Message);
        Assert.Empty(target.CAEXFile.SystemUnitClassLib);
    }

    [Fact]
    public void A_file_that_is_not_a_NodeSet_is_refused()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "<root/>");
            var ex = Assert.Throws<ImportException>(() => NodeSetImporter.Convert(file, di.Catalog));
            Assert.Contains("not an OPC UA NodeSet", ex.Message);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void A_missing_dependency_is_named_before_Opc2Aml_runs()
    {
        var ac = Fixtures.Path("uafx", "opc.ua.fx.ac.nodeset2.xml");

        var ex = Assert.Throws<ImportException>(() => NodeSetImporter.Convert(ac, di.Catalog));

        Assert.Contains("http://opcfoundation.org/UA/FX/Data/", ex.Message);
    }
}
