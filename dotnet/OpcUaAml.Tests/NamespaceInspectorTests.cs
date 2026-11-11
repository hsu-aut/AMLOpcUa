using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using OpcUaAml.Import;
using OpcUaAml.Types;

namespace OpcUaAml.Tests;

public class NamespaceInspectorTests(BundledDiConversion di) : IClassFixture<BundledDiConversion>
{
    private CAEXDocument Fresh()
    {
        var doc = CAEXDocument.New_CAEXDocument();
        LibraryMerger.Merge(doc, di.Result.Document);
        return doc;
    }

    [Fact]
    public void Counts_what_DI_brings_and_what_it_builds_on()
    {
        var doc = Fresh();

        var details = NamespaceInspector.Of(doc, Fixtures.DiUri);
        var ua = NamespaceInspector.Of(doc, Fixtures.UaUri);

        // As the NodeSet declares them: 42 ObjectTypes, 2 VariableTypes, 9 DataTypes, 5 ReferenceTypes.
        Assert.Equal((42, 2, 9, 5), (details.ObjectTypes, details.VariableTypes, details.DataTypes, details.ReferenceTypes));
        Assert.Equal(new[] { Fixtures.UaUri }, details.BuildsOn);
        Assert.Contains(Fixtures.DiUri, ua.UsedBy);
        Assert.Equal(0, details.Instances);
    }

    [Fact]
    public void A_namespace_goes_only_when_nothing_needs_it()
    {
        var doc = Fresh();
        var ih = doc.CAEXFile.InstanceHierarchy.Append("Plant");
        var type = (SystemUnitFamilyType)doc.FindByPath($"[SUC_{Fixtures.DiUri}]/[SoftwareVersionType]")!;
        ih.Insert(TypeInstantiator.Instantiate(type, "Firmware").Instance);

        Assert.Contains(NamespaceInspector.RemovalBlockers(doc, Fixtures.UaUri), b => b.Contains(Fixtures.DiUri));
        Assert.Contains(NamespaceInspector.RemovalBlockers(doc, Fixtures.DiUri), b => b.Contains("instance hierarchies"));
        Assert.Throws<InvalidOperationException>(() => NamespaceInspector.Remove(doc, Fixtures.DiUri));

        ih.Remove();
        var removed = NamespaceInspector.Remove(doc, Fixtures.DiUri);

        Assert.Equal(4, removed.Count);
        Assert.DoesNotContain(NamespaceOverview.Of(doc), e => e.NamespaceUri == Fixtures.DiUri);
        Assert.Contains(NamespaceOverview.Of(doc), e => e.NamespaceUri == Fixtures.UaUri);
    }

    [Fact]
    public void Paths_into_a_namespace_from_anywhere_keep_it()
    {
        // No element typed by DI, yet an interface, an attribute and a role of
        // an instance and a class of the user's own library point into its
        // libraries. Removing DI deleted the libraries and left those paths
        // dangling.
        var doc = Fresh();
        var pump = doc.CAEXFile.InstanceHierarchy.Append("Plant").InternalElement.Append("Pump");
        pump.ExternalInterface.Append("Peer").RefBaseClassPath = $"[ICL_{Fixtures.DiUri}]/[ConnectsTo]";
        pump.Attribute.Append("Health").RefAttributeType = $"[ATL_{Fixtures.DiUri}]/[DeviceHealthEnumeration]";
        pump.RoleRequirements.Append().RefBaseRoleClassPath = $"[RCL_{Fixtures.DiUri}]/[IVendorNameplateType]";
        var own = doc.CAEXFile.SystemUnitClassLib.Append("MyLib").SystemUnitClass.Append("MyDevice");
        own.RefBaseClassPath = $"[SUC_{Fixtures.DiUri}]/[DeviceType]";
        Assert.All(new[] { pump.ExternalInterface["Peer"]!.RefBaseClassPath, pump.Attribute["Health"]!.RefAttributeType, own.RefBaseClassPath },
            p => Assert.NotNull(doc.FindByPath(p)));

        var blockers = NamespaceInspector.RemovalBlockers(doc, Fixtures.DiUri);

        Assert.Contains(blockers, b => b.Contains("3 ") && b.Contains("instance hierarchies"));
        Assert.Contains(blockers, b => b.Contains("'MyLib'"));
        Assert.Throws<InvalidOperationException>(() => NamespaceInspector.Remove(doc, Fixtures.DiUri));
        Assert.Contains(NamespaceOverview.Of(doc), e => e.NamespaceUri == Fixtures.DiUri);

        doc.CAEXFile.InstanceHierarchy["Plant"]!.Remove();
        doc.CAEXFile.SystemUnitClassLib["MyLib"]!.Remove();
        Assert.Empty(NamespaceInspector.RemovalBlockers(doc, Fixtures.DiUri));
        NamespaceInspector.Remove(doc, Fixtures.DiUri);
        // The metamodel libraries of Annex A belong to no namespace and point
        // into the base model's, so they keep it too.
        Assert.Contains(NamespaceInspector.RemovalBlockers(doc, Fixtures.UaUri), b => b.Contains("'SUC_OpcAmlMetaModel'"));
    }
}
