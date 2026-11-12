using System.IO;
using System.IO.Packaging;
using System.Xml.Linq;
using Aml.Editor.Plugin.OpcUa;

namespace PluginTests;

/// <summary>
/// "Copy without server addresses…" (before 0.3.1 "Remove server addresses…") on an .amlx document: the copy was written as a
/// plain .aml file, so what else the container held was not passed on.
/// </summary>
public class StripAddressesTests
{
    private const string Root = "http://schemas.automationml.org/container/relationship/RootDocument";

    private static readonly XDocument Plant = new(new XElement("{http://www.dke.de/CAEX}CAEXFile",
        new XElement("{http://www.dke.de/CAEX}InstanceHierarchy", new XAttribute("Name", "Plant"),
            new XElement("{http://www.dke.de/CAEX}InternalElement", new XAttribute("Name", "Server"),
                new XElement("{http://www.dke.de/CAEX}Attribute", new XAttribute("Name", "EndpointUrl"),
                    new XElement("{http://www.dke.de/CAEX}Value", "opc.tcp://plc-17.factory.example:4840"))))));

    [Fact]
    public void A_container_is_passed_on_as_a_container_with_all_its_parts()
    {
        var folder = Directory.CreateTempSubdirectory("opcuaaml-strip-").FullName;
        try
        {
            var source = Path.Combine(folder, "plant.amlx");
            using (var package = Package.Open(source, FileMode.Create))
            {
                var root = package.CreatePart(new Uri("/plant.aml", UriKind.Relative), "model/vnd.automationml+xml");
                using (var s = root.GetStream()) Plant.Save(s);
                package.CreateRelationship(root.Uri, TargetMode.Internal, Root);
                var geometry = package.CreatePart(new Uri("/geometry/robot.dae", UriKind.Relative), "model/vnd.collada+xml");
                using (var s = geometry.GetStream()) s.Write("<COLLADA/>"u8);
            }
            var target = OpcUaPlugin.CopyWithoutAddressesName(source);

            OpcUaPlugin.SaveCopyWithoutAddresses(new XDocument(Plant), source, target);

            Assert.EndsWith("plant-without-addresses.amlx", target);
            using var copy = Package.Open(target, FileMode.Open, FileAccess.Read);
            Assert.True(copy.PartExists(new Uri("/geometry/robot.dae", UriKind.Relative)));
            using var reader = new StreamReader(copy.GetPart(new Uri("/plant.aml", UriKind.Relative)).GetStream());
            Assert.DoesNotContain("plc-17.factory.example", reader.ReadToEnd());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void A_plain_file_stays_a_plain_file()
    {
        var folder = Directory.CreateTempSubdirectory("opcuaaml-strip-").FullName;
        try
        {
            var source = Path.Combine(folder, "plant.aml");
            Plant.Save(source);
            var target = OpcUaPlugin.CopyWithoutAddressesName(source);

            OpcUaPlugin.SaveCopyWithoutAddresses(new XDocument(Plant), source, target);

            Assert.EndsWith("plant-without-addresses.aml", target);
            Assert.DoesNotContain("plc-17.factory.example", File.ReadAllText(target));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
