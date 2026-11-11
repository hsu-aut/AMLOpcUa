using System.Xml.Linq;
using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using OpcUaAml.Import;

namespace OpcUaAml.Tests;

// The instances a model holds (here Pump1 below Objects and Pump2 in the
// folder Hall), taken into a document after its libraries, as the modeler's
// Apply offers it.
public class InstanceTransferTests(BundledDiConversion di) : IClassFixture<BundledDiConversion>
{
    private const string Plant = "http://example.org/Plant/";
    private static readonly string File = Fixtures.Path("instances", "Plant.NodeSet2.xml");

    private (CAEXDocument Target, ConversionResult Conversion) Imported()
    {
        var conversion = NodeSetImporter.Convert(File, di.Catalog);
        var target = CAEXDocument.New_CAEXDocument();
        LibraryMerger.Merge(target, conversion.Document);
        return (target, conversion);
    }

    private static List<string> Ids(CAEXDocument doc) =>
        doc.CAEXFile.Node.Descendants().Attributes().Where(a => a.Name.LocalName == "ID").Select(a => a.Value).ToList();

    [Fact]
    public void The_hierarchy_is_named_after_the_model()
    {
        Assert.Equal("IH_Plant", InstanceTransfer.HierarchyName(Plant));
        Assert.Equal("IH_MPS500", InstanceTransfer.HierarchyName("http://hsu-hh.de/UA/MPS500/"));
        Assert.Equal("IH_Pumps", InstanceTransfer.HierarchyName("urn:example:Pumps"));
    }

    [Fact]
    public void The_instances_of_the_model_go_into_a_hierarchy_of_their_own()
    {
        var (target, conversion) = Imported();

        var result = InstanceTransfer.Take(target, conversion.Document, Plant, new InstanceDestination.OwnHierarchy("IH_Plant"));

        var ih = Assert.Single(target.CAEXFile.InstanceHierarchy);
        Assert.Equal("IH_Plant", ih.Name);
        Assert.Equal(new[] { "Hall", "Pump1" }, ih.InternalElement.Select(e => e.Name).Order());
        Assert.NotNull(ih.InternalElement["Hall"]!.InternalElement["Pump2"]);
        Assert.Equal(2, result.Taken);
        // What the instances are made of is in the libraries the import brought.
        var pump = ih.InternalElement["Pump1"]!;
        Assert.NotNull(target.FindByPath(pump.RefBaseSystemUnitPath));
    }

    [Fact]
    public void Applying_again_replaces_the_hierarchy_it_made()
    {
        var (target, conversion) = Imported();
        InstanceTransfer.Take(target, conversion.Document, Plant, new InstanceDestination.OwnHierarchy("IH_Plant"));
        target.CAEXFile.InstanceHierarchy.Append("Mine");
        var first = Ids(target);

        InstanceTransfer.Take(target, conversion.Document, Plant, new InstanceDestination.OwnHierarchy("IH_Plant"));

        // Same place in the tree, same IDs, nothing twice.
        Assert.Equal(new[] { "IH_Plant", "Mine" }, target.CAEXFile.InstanceHierarchy.Select(h => h.Name));
        Assert.Equal(first.Order(), Ids(target).Order());
    }

    [Fact]
    public void Instances_below_a_chosen_element_get_IDs_of_their_own_each_time()
    {
        var (target, conversion) = Imported();
        var station = target.CAEXFile.InstanceHierarchy.Append("Plant").InternalElement.Append("Station");

        InstanceTransfer.Take(target, conversion.Document, Plant, new InstanceDestination.Below(station));
        InstanceTransfer.Take(target, conversion.Document, Plant, new InstanceDestination.Below(station));

        Assert.Equal(4, station.InternalElement.Count);
        var ids = Ids(target);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    private static readonly XNamespace Ua = OpcUaAml.Export.NodeSetExporter.UaNodeSetNamespace;

    /// <summary>The model written back as the nodes it was (the inverse of Annex A).</summary>
    private static XDocument Exported(CAEXDocument target) =>
        OpcUaAml.Export.NodeSetExporter.Export(target, new OpcUaAml.Export.NodeSetExportOptions
        {
            Mode = OpcUaAml.Export.ExportMode.AnnexAInverse,
            NamespaceUri = Plant,
        });

    private static List<XElement> Named(XDocument nodeSet, string browseName) =>
        nodeSet.Root!.Elements().Where(e => (string?)e.Attribute("BrowseName") == browseName).ToList();

    /// <summary>Whether the node hangs below Objects (i=85), as the instances of a model do.</summary>
    private static bool BelowObjects(XElement node) =>
        node.Element(Ua + "References")!.Elements().Any(r =>
            (string?)r.Attribute("IsForward") == "false" && r.Value == "i=85"
            && (string?)r.Attribute("ReferenceType") is "Organizes" or "i=35");

    /// <summary>References and parents in the model's namespace (ns=1) that lead to no node of the file.</summary>
    private static List<string> Dangling(XDocument nodeSet)
    {
        var nodes = nodeSet.Root!.Elements().Select(e => (string?)e.Attribute("NodeId")).Where(id => id != null).ToHashSet();
        return nodeSet.Root.Elements()
            .SelectMany(e => e.Element(Ua + "References")?.Elements().Select(r => r.Value) ?? [])
            .Concat(nodeSet.Root.Elements().Select(e => (string?)e.Attribute("ParentNodeId") ?? ""))
            .Where(target => target.StartsWith("ns=1;", StringComparison.Ordinal) && !nodes.Contains(target))
            .Distinct().ToList();
    }

    [Fact]
    public void Instances_in_their_own_hierarchy_are_written_below_Objects()
    {
        var (target, conversion) = Imported();
        InstanceTransfer.Take(target, conversion.Document, Plant, new InstanceDestination.OwnHierarchy("IH_Plant"));
        // One more, added by hand: it has no NodeId yet.
        var ih = target.CAEXFile.InstanceHierarchy["IH_Plant"]!;
        var added = ih.InternalElement.Append("Pump3");
        added.RefBaseSystemUnitPath = ih.InternalElement["Pump1"]!.RefBaseSystemUnitPath;

        var nodeSet = Exported(target);

        var pump = Assert.Single(Named(nodeSet, "1:Pump1"));
        Assert.Equal("ns=1;i=1002", (string?)pump.Attribute("NodeId"));
        Assert.True(BelowObjects(pump));
        Assert.True(BelowObjects(Assert.Single(Named(nodeSet, "1:Hall"))));
        Assert.Single(Named(nodeSet, "1:Pump2"));
        var third = Assert.Single(Named(nodeSet, "1:Pump3"));
        Assert.True(BelowObjects(third));
        Assert.StartsWith("ns=1;i=", (string?)third.Attribute("NodeId"));
        Assert.Empty(Dangling(nodeSet));
    }

    [Fact]
    public void Instances_below_a_chosen_element_are_written_below_Objects_each_with_NodeIds_of_its_own()
    {
        var (target, conversion) = Imported();
        var station = target.CAEXFile.InstanceHierarchy.Append("Plant").InternalElement.Append("Station");
        InstanceTransfer.Take(target, conversion.Document, Plant, new InstanceDestination.Below(station));
        InstanceTransfer.Take(target, conversion.Document, Plant, new InstanceDestination.Below(station));

        var nodeSet = Exported(target);

        // Two Applies are two pumps, in AML and in OPC UA.
        var pumps = Named(nodeSet, "1:Pump1").Concat(Named(nodeSet, "1:Pump1 (2)")).ToList();
        Assert.Equal(2, pumps.Count);
        Assert.All(pumps, p => Assert.True(BelowObjects(p)));
        Assert.Equal(2, pumps.Select(p => (string?)p.Attribute("NodeId")).Distinct().Count());
        // Each pump with its own Speed: Pump1 and Pump2 (in Hall), twice.
        Assert.Equal(2, Named(nodeSet, "1:Hall").Concat(Named(nodeSet, "1:Hall (2)")).Count());
        Assert.Equal(4, Named(nodeSet, "1:Speed").Count(s => (string?)s.Attribute("ParentNodeId") != "ns=1;i=1000"));
        var ids = nodeSet.Root!.Elements().Select(e => (string?)e.Attribute("NodeId")).Where(id => id != null).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Empty(Dangling(nodeSet));
    }

    [Fact]
    public void A_reference_inside_a_second_copy_stays_inside_that_copy()
    {
        const string cells = "urn:test:cells";
        var folder = Directory.CreateTempSubdirectory("opcuaaml-cells-").FullName;
        try
        {
            var file = Path.Combine(folder, "Cells.NodeSet2.xml");
            System.IO.File.WriteAllText(file, $"""
                <UANodeSet xmlns="http://opcfoundation.org/UA/2011/03/UANodeSet.xsd">
                  <NamespaceUris><Uri>{cells}</Uri></NamespaceUris>
                  <Models><Model ModelUri="{cells}" Version="1.0.0" PublicationDate="2026-09-19T00:00:00Z">
                    <RequiredModel ModelUri="http://opcfoundation.org/UA/" Version="1.05.02" PublicationDate="2022-11-01T00:00:00Z" /></Model></Models>
                  <UAReferenceType NodeId="ns=1;i=4000" BrowseName="1:PairedWith" Symmetric="true">
                    <DisplayName>PairedWith</DisplayName>
                    <References><Reference ReferenceType="i=45" IsForward="false">i=32</Reference></References>
                  </UAReferenceType>
                  <UAObjectType NodeId="ns=1;i=1000" BrowseName="1:CellType">
                    <DisplayName>CellType</DisplayName>
                    <References><Reference ReferenceType="i=45" IsForward="false">i=58</Reference></References>
                  </UAObjectType>
                  <UAObject NodeId="ns=1;i=5000" BrowseName="1:Cell1">
                    <DisplayName>Cell1</DisplayName>
                    <References>
                      <Reference ReferenceType="i=40">ns=1;i=1000</Reference>
                      <Reference ReferenceType="i=35" IsForward="false">i=85</Reference>
                      <Reference ReferenceType="i=47">ns=1;i=5001</Reference>
                      <Reference ReferenceType="i=47">ns=1;i=5002</Reference>
                    </References>
                  </UAObject>
                  <UAObject NodeId="ns=1;i=5001" BrowseName="1:Left" ParentNodeId="ns=1;i=5000">
                    <DisplayName>Left</DisplayName>
                    <References>
                      <Reference ReferenceType="i=47" IsForward="false">ns=1;i=5000</Reference>
                      <Reference ReferenceType="i=40">i=58</Reference>
                      <Reference ReferenceType="ns=1;i=4000">ns=1;i=5002</Reference>
                    </References>
                  </UAObject>
                  <UAObject NodeId="ns=1;i=5002" BrowseName="1:Right" ParentNodeId="ns=1;i=5000">
                    <DisplayName>Right</DisplayName>
                    <References>
                      <Reference ReferenceType="i=47" IsForward="false">ns=1;i=5000</Reference>
                      <Reference ReferenceType="i=40">i=58</Reference>
                    </References>
                  </UAObject>
                </UANodeSet>
                """);
            var conversion = NodeSetImporter.Convert(file, di.Catalog);
            var target = CAEXDocument.New_CAEXDocument();
            LibraryMerger.Merge(target, conversion.Document);
            var station = target.CAEXFile.InstanceHierarchy.Append("Plant").InternalElement.Append("Station");
            InstanceTransfer.Take(target, conversion.Document, cells, new InstanceDestination.Below(station));
            InstanceTransfer.Take(target, conversion.Document, cells, new InstanceDestination.Below(station));

            var nodeSet = OpcUaAml.Export.NodeSetExporter.Export(target, new OpcUaAml.Export.NodeSetExportOptions
            {
                Mode = OpcUaAml.Export.ExportMode.AnnexAInverse,
                NamespaceUri = cells,
            });

            var lefts = Named(nodeSet, "1:Left");
            Assert.Equal(2, lefts.Count);
            foreach (var left in lefts)
            {
                // PairedWith is symmetric: Left names Right, and Right names Left back.
                var paired = left.Element(Ua + "References")!.Elements()
                    .Where(r => (string?)r.Attribute("ReferenceType") == "ns=1;i=4000").Select(r => r.Value).Distinct().ToList();
                var right = nodeSet.Root!.Elements().Single(e => (string?)e.Attribute("NodeId") == Assert.Single(paired));
                Assert.Equal("1:Right", (string?)right.Attribute("BrowseName"));
                Assert.Equal((string?)left.Attribute("ParentNodeId"), (string?)right.Attribute("ParentNodeId"));
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Every_link_of_what_is_taken_ends_at_an_element_of_the_document()
    {
        var (target, conversion) = Imported();
        var station = target.CAEXFile.InstanceHierarchy.Append("Plant").InternalElement.Append("Station");
        InstanceTransfer.Take(target, conversion.Document, Plant, new InstanceDestination.Below(station));

        var ids = Ids(target).ToHashSet();
        var ends = target.CAEXFile.Node.Descendants().Where(e => e.Name.LocalName == "InternalLink")
            .SelectMany(l => new[] { (string?)l.Attribute("RefPartnerSideA"), (string?)l.Attribute("RefPartnerSideB") })
            .Select(end => end!.Split(':')[0])
            .ToList();
        Assert.NotEmpty(ends);
        Assert.All(ends, end => Assert.Contains(end, ids));
    }
}
