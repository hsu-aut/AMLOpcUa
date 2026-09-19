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
    public void A_structure_containing_itself_is_converted()
    {
        // Patch 0006: a tree node with a list of tree nodes used to overflow the stack.
        var folder = Directory.CreateTempSubdirectory("opcuaaml-tree-").FullName;
        try
        {
            var file = Path.Combine(folder, "Tree.NodeSet2.xml");
            File.WriteAllText(file, """
                <UANodeSet xmlns="http://opcfoundation.org/UA/2011/03/UANodeSet.xsd">
                  <NamespaceUris><Uri>urn:test:tree</Uri></NamespaceUris>
                  <Models><Model ModelUri="urn:test:tree" Version="1.0.0" PublicationDate="2026-09-19T00:00:00Z">
                    <RequiredModel ModelUri="http://opcfoundation.org/UA/" Version="1.05.02" PublicationDate="2022-11-01T00:00:00Z" /></Model></Models>
                  <Aliases><Alias Alias="HasSubtype">i=45</Alias><Alias Alias="String">i=12</Alias></Aliases>
                  <UADataType NodeId="ns=1;i=3000" BrowseName="1:TreeNode">
                    <DisplayName>TreeNode</DisplayName>
                    <References><Reference ReferenceType="HasSubtype" IsForward="false">i=22</Reference></References>
                    <Definition Name="1:TreeNode">
                      <Field Name="Label" DataType="String" />
                      <Field Name="Children" DataType="ns=1;i=3000" ValueRank="1" ArrayDimensions="0" />
                    </Definition>
                  </UADataType>
                </UANodeSet>
                """);
            var result = NodeSetImporter.Convert(file, di.Catalog);

            var tree = result.Document.CAEXFile.AttributeTypeLib["ATL_urn:test:tree"]!.AttributeType["TreeNode"]!;
            Assert.NotNull(tree.Attribute["Label"]);
            Assert.NotNull(tree.Attribute["Children"]);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_reference_to_a_node_no_model_defines_is_skipped_with_a_warning()
    {
        // Patch 0007: dictionary entries of IRDI live outside every NodeSet.
        var folder = Directory.CreateTempSubdirectory("opcuaaml-dangling-").FullName;
        try
        {
            var file = Path.Combine(folder, "Dangling.NodeSet2.xml");
            File.WriteAllText(file, """
                <UANodeSet xmlns="http://opcfoundation.org/UA/2011/03/UANodeSet.xsd">
                  <NamespaceUris><Uri>urn:test:dangling</Uri></NamespaceUris>
                  <Models><Model ModelUri="urn:test:dangling" Version="1.0.0" PublicationDate="2026-09-19T00:00:00Z">
                    <RequiredModel ModelUri="http://opcfoundation.org/UA/" Version="1.05.02" PublicationDate="2022-11-01T00:00:00Z" /></Model></Models>
                  <UAObjectType NodeId="ns=1;i=1000" BrowseName="1:PumpType">
                    <DisplayName>PumpType</DisplayName>
                    <References>
                      <Reference ReferenceType="i=45" IsForward="false">i=58</Reference>
                      <Reference ReferenceType="i=17597">ns=1;s=0112/2///61987#ABB271#007</Reference>
                    </References>
                  </UAObjectType>
                </UANodeSet>
                """);
            var result = NodeSetImporter.Convert(file, di.Catalog);

            Assert.NotNull(result.Document.CAEXFile.SystemUnitClassLib["SUC_urn:test:dangling"]!.SystemUnitClass["PumpType"]);
            Assert.Contains(result.Warnings, w => w.Contains("0112/2///61987#ABB271#007", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_symmetric_reference_between_declarations_is_converted()
    {
        // Patch 0008: a symmetric reference type has no InverseName.
        var folder = Directory.CreateTempSubdirectory("opcuaaml-symmetric-").FullName;
        try
        {
            var file = Path.Combine(folder, "Symmetric.NodeSet2.xml");
            File.WriteAllText(file, """
                <UANodeSet xmlns="http://opcfoundation.org/UA/2011/03/UANodeSet.xsd">
                  <NamespaceUris><Uri>urn:test:symmetric</Uri></NamespaceUris>
                  <Models><Model ModelUri="urn:test:symmetric" Version="1.0.0" PublicationDate="2026-09-19T00:00:00Z">
                    <RequiredModel ModelUri="http://opcfoundation.org/UA/" Version="1.05.02" PublicationDate="2022-11-01T00:00:00Z" /></Model></Models>
                  <UAReferenceType NodeId="ns=1;i=4000" BrowseName="1:PairedWith" Symmetric="true">
                    <DisplayName>PairedWith</DisplayName>
                    <References><Reference ReferenceType="i=45" IsForward="false">i=32</Reference></References>
                  </UAReferenceType>
                  <UAObjectType NodeId="ns=1;i=1000" BrowseName="1:CellType">
                    <DisplayName>CellType</DisplayName>
                    <References>
                      <Reference ReferenceType="i=45" IsForward="false">i=58</Reference>
                      <Reference ReferenceType="i=47">ns=1;i=5001</Reference>
                      <Reference ReferenceType="i=47">ns=1;i=5002</Reference>
                    </References>
                  </UAObjectType>
                  <UAObject NodeId="ns=1;i=5001" BrowseName="1:Left" ParentNodeId="ns=1;i=1000">
                    <DisplayName>Left</DisplayName>
                    <References>
                      <Reference ReferenceType="i=47" IsForward="false">ns=1;i=1000</Reference>
                      <Reference ReferenceType="i=40">i=58</Reference>
                      <Reference ReferenceType="i=37">i=78</Reference>
                      <Reference ReferenceType="ns=1;i=4000">ns=1;i=5002</Reference>
                    </References>
                  </UAObject>
                  <UAObject NodeId="ns=1;i=5002" BrowseName="1:Right" ParentNodeId="ns=1;i=1000">
                    <DisplayName>Right</DisplayName>
                    <References>
                      <Reference ReferenceType="i=47" IsForward="false">ns=1;i=1000</Reference>
                      <Reference ReferenceType="i=40">i=58</Reference>
                      <Reference ReferenceType="i=37">i=78</Reference>
                    </References>
                  </UAObject>
                </UANodeSet>
                """);
            var result = NodeSetImporter.Convert(file, di.Catalog);

            var cell = result.Document.CAEXFile.SystemUnitClassLib["SUC_urn:test:symmetric"]!.SystemUnitClass["CellType"]!;
            Assert.Contains(cell.InternalElement["Right"]!.ExternalInterface, ei => ei.Name == "PairedWith");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
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
