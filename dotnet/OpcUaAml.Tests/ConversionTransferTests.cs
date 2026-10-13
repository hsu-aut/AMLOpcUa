using OpcUaAml.Import;

namespace OpcUaAml.Tests;

/// <summary>
/// The plugin converts in a process of its own and reads the result back: the
/// document as an AML file, the rest of the result beside it.
/// </summary>
public class ConversionTransferTests(BundledDiConversion di) : IClassFixture<BundledDiConversion>
{
    [Fact]
    public void A_result_written_by_one_process_reads_back_the_same()
    {
        var folder = Directory.CreateTempSubdirectory("opcuaaml-transfer-").FullName;
        try
        {
            var aml = Path.Combine(folder, "DI.aml");
            var original = di.Result with { Warnings = ["first warning", "second warning"], FromCache = true };

            ConversionTransfer.Write(original, ["catalog note"], aml);
            var (read, notes) = ConversionTransfer.Read(aml, di.Catalog.Find(Fixtures.DiUri)!.FilePath);

            Assert.Equal(original.LoadedModels, read.LoadedModels);
            Assert.Equal(original.Warnings, read.Warnings);
            Assert.Equal(original.Duration.TotalMilliseconds, read.Duration.TotalMilliseconds, 3);
            Assert.True(read.FromCache);
            Assert.Equal(original.NodeSet.PrimaryModel.Model.ModelUri, read.NodeSet.PrimaryModel.Model.ModelUri);
            Assert.Equal(["catalog note"], notes);
            Assert.Equal(
                original.Document.CAEXFile.SystemUnitClassLib.Select(l => $"{l.Name}:{l.SystemUnitClass.Count}"),
                read.Document.CAEXFile.SystemUnitClassLib.Select(l => $"{l.Name}:{l.SystemUnitClass.Count}"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_result_without_its_description_is_an_import_error()
    {
        var folder = Directory.CreateTempSubdirectory("opcuaaml-transfer-").FullName;
        try
        {
            var aml = Path.Combine(folder, "DI.aml");
            File.WriteAllText(aml, "<CAEXFile/>");

            var error = Assert.Throws<ImportException>(() => ConversionTransfer.Read(aml, di.Catalog.Find(Fixtures.DiUri)!.FilePath));
            Assert.Contains("DI.aml.json", error.Message);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
