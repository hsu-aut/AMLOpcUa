using System.IO.Packaging;
using System.Xml.Linq;
using OpcUaAml.Import;

namespace OpcUaAml.Tests;

/// <summary>
/// A copy of an .amlx container with another root document. "Remove server
/// addresses…" saved an .amlx document as a plain .aml file, and everything
/// else the container held (libraries, geometry) was not in the copy.
/// </summary>
public class ContainerCopyTests
{
    private const string Root = "http://schemas.automationml.org/container/relationship/RootDocument";

    private static string Container(string folder)
    {
        var path = Path.Combine(folder, "plant.amlx");
        using var package = Package.Open(path, FileMode.Create);
        var root = package.CreatePart(new Uri("/plant.aml", UriKind.Relative), "model/vnd.automationml+xml");
        using (var s = root.GetStream()) new XDocument(new XElement("CAEXFile", new XAttribute("FileName", "plant.aml"), "with addresses")).Save(s);
        package.CreateRelationship(root.Uri, TargetMode.Internal, Root);
        var geometry = package.CreatePart(new Uri("/geometry/robot.dae", UriKind.Relative), "model/vnd.collada+xml");
        using (var s = geometry.GetStream()) s.Write("<COLLADA/>"u8);
        return path;
    }

    private static string Read(Package package, string uri)
    {
        using var reader = new StreamReader(package.GetPart(new Uri(uri, UriKind.Relative)).GetStream());
        return reader.ReadToEnd();
    }

    [Fact]
    public void The_copy_has_the_new_root_and_keeps_every_other_part()
    {
        var folder = Directory.CreateTempSubdirectory("opcuaaml-amlx-").FullName;
        try
        {
            var source = Container(folder);
            var target = Path.Combine(folder, "plant-without-addresses.amlx");

            AmlContainers.CopyWithRoot(source, new XDocument(new XElement("CAEXFile", "without addresses")), target);

            using (var copy = Package.Open(target, FileMode.Open, FileAccess.Read))
            {
                Assert.Contains("without addresses", Read(copy, "/plant.aml"));
                Assert.Equal("<COLLADA/>", Read(copy, "/geometry/robot.dae"));
            }
            using var original = Package.Open(source, FileMode.Open, FileAccess.Read);
            Assert.Contains("with addresses", Read(original, "/plant.aml"));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void A_container_without_a_root_document_is_named()
    {
        var folder = Directory.CreateTempSubdirectory("opcuaaml-amlx-").FullName;
        try
        {
            var source = Path.Combine(folder, "empty.amlx");
            using (Package.Open(source, FileMode.Create)) { }

            var error = Assert.Throws<InvalidDataException>(() =>
                AmlContainers.CopyWithRoot(source, new XDocument(new XElement("CAEXFile")), Path.Combine(folder, "copy.amlx")));

            Assert.Contains("empty.amlx", error.Message);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
