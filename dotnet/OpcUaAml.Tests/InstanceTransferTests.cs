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
