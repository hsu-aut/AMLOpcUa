using OpcUaAml.Checks;
using OpcUaAml.Types;

namespace OpcUaAml.Tests;

public class UpgradeTests(DiDocument di) : IClassFixture<DiDocument>
{
    [Fact]
    public void Missing_mandatory_children_are_added_and_the_check_passes_again()
    {
        var ih = di.Hierarchy("Upgrade");
        ih.InternalElement.Insert(TypeInstantiator.Instantiate(di.Type("SoftwareVersionType"), "Firmware").Instance, asFirst: false);
        var fw = ih.InternalElement["Firmware"]!;
        // An instance made against an older version of the type, which did not
        // declare Manufacturer yet: simulated by removing it.
        foreach (var link in fw.InternalLink.Where(l => l.Name == "Manufacturer").ToList()) fw.InternalLink.RemoveElement(link);
        fw.InternalElement.RemoveElement(fw.InternalElement["Manufacturer"]!);
        fw.InternalElement["SoftwareRevision"]!.Attribute["Value"]!.Value = "2.1";
        Assert.Contains(di.FindingsIn(ih), f => f.Rule == Rules.MissingMandatory);

        var changes = InstanceUpgrader.AddMissingMandatory(fw);

        Assert.Equal(new UpgradeChange("Upgrade/Firmware", "Manufacturer"), Assert.Single(changes));
        Assert.NotNull(fw.InternalElement["Manufacturer"]);
        Assert.Equal("2.1", fw.InternalElement["SoftwareRevision"]!.Attribute["Value"]!.Value);
        Assert.Empty(di.FindingsIn(ih));
        Assert.Contains(fw.InternalLink, l => l.Name == "Manufacturer");
    }

    [Fact]
    public void An_up_to_date_instance_is_left_alone()
    {
        var ih = di.Hierarchy("Current");
        ih.InternalElement.Insert(TypeInstantiator.Instantiate(di.Type("SoftwareType"), "Sw").Instance, asFirst: false);
        var before = ih.InternalElement["Sw"]!.Node.ToString();

        var changes = InstanceUpgrader.UpgradeDocument(di.Document)
            .Where(c => c.ElementPath.StartsWith("Current/")).ToList();

        Assert.Empty(changes);
        Assert.Equal(before, ih.InternalElement["Sw"]!.Node.ToString());
    }
}
