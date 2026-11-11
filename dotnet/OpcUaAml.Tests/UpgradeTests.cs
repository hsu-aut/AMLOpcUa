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
        var before = fw.Node.ToString();

        // The preview names what an upgrade adds and changes nothing.
        var preview = InstanceUpgrader.PreviewDocument(di.Document).Where(c => c.ElementPath.StartsWith("Upgrade/")).ToList();
        Assert.Equal(new UpgradeChange("Upgrade/Firmware", "Manufacturer"), Assert.Single(preview));
        Assert.Equal(before, fw.Node.ToString());

        var changes = InstanceUpgrader.AddMissingMandatory(fw);

        Assert.Equal(new UpgradeChange("Upgrade/Firmware", "Manufacturer"), Assert.Single(changes));
        Assert.NotNull(fw.InternalElement["Manufacturer"]);
        Assert.Equal("2.1", fw.InternalElement["SoftwareRevision"]!.Attribute["Value"]!.Value);
        Assert.Empty(di.FindingsIn(ih));
        Assert.Contains(fw.InternalLink, l => l.Name == "Manufacturer");
    }

    [Fact]
    public void A_missing_child_below_an_overriding_declaration_is_added()
    {
        // PubSubDiagnosticsWriterGroupType inherits StateError below Counters
        // from PubSubDiagnosticsType; Counters itself is a BaseObjectType.
        var ih = di.Hierarchy("UpgradeNested");
        ih.InternalElement.Insert(TypeInstantiator.Instantiate(di.UaType("PubSubDiagnosticsWriterGroupType"), "Diagnostics").Instance, asFirst: false);
        var counters = ih.InternalElement["Diagnostics"]!.InternalElement["Counters"]!;
        InstanceTests.RemoveChild(counters, "StateError");
        Assert.NotEmpty(di.FindingsIn(ih));

        var preview = InstanceUpgrader.PreviewDocument(di.Document).Where(c => c.ElementPath.StartsWith("UpgradeNested/")).ToList();
        var changes = InstanceUpgrader.UpgradeDocument(di.Document).Where(c => c.ElementPath.StartsWith("UpgradeNested/")).ToList();

        var expected = new UpgradeChange("UpgradeNested/Diagnostics/Counters", "StateError");
        Assert.Equal(expected, Assert.Single(preview));
        Assert.Equal(expected, Assert.Single(changes));
        Assert.Contains("DiagnosticsLevel", counters.InternalElement["StateError"]!.InternalElement.Select(c => c.Name));
        Assert.Contains(counters.InternalLink, l => l.Name == "StateError");
        Assert.Empty(di.FindingsIn(ih));
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
