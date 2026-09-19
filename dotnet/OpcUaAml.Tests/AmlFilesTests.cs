using System.IO.Packaging;
using Aml.Engine.CAEX;
using OpcUaAml.Import;

namespace OpcUaAml.Tests;

public class AmlFilesTests
{
    [Fact]
    public void A_document_saved_as_amlx_reads_back()
    {
        var folder = Directory.CreateTempSubdirectory("opcuaaml-amlx-").FullName;
        try
        {
            var doc = CAEXDocument.New_CAEXDocument();
            doc.CAEXFile.InstanceHierarchy.Append("Plant").InternalElement.Append("Pump");
            var path = Path.Combine(folder, "plant.amlx");

            AmlFiles.Save(doc, path);
            var back = AmlFiles.Load(path);

            Assert.Equal("Pump", back.CAEXFile.InstanceHierarchy["Plant"]!.InternalElement.Single().Name);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Saving_into_an_existing_container_keeps_its_other_parts()
    {
        var folder = Directory.CreateTempSubdirectory("opcuaaml-amlx-").FullName;
        try
        {
            // An Opc2Aml container: the root document and the libraries it references.
            var path = Path.Combine(folder, "safety.amlx");
            File.Copy(Fixtures.Path("uafx", "Opc.Ua.Safety.NodeSet2.xml.amlx"), path);
            static List<string> Parts(string p)
            {
                using var package = Package.Open(p, FileMode.Open, FileAccess.Read);
                return package.GetParts().Select(x => x.Uri.ToString()).OrderBy(u => u, StringComparer.Ordinal).ToList();
            }
            var before = Parts(path);

            var doc = AmlFiles.Load(path);
            doc.CAEXFile.InstanceHierarchy.Append("Added");
            AmlFiles.Save(doc, path);

            Assert.Equal(before, Parts(path));
            Assert.NotNull(AmlFiles.Load(path).CAEXFile.InstanceHierarchy["Added"]);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
