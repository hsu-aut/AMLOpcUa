using Aml.Engine.CAEX;
using OpcUaAml.Links;
using OpcUaAml.Types;

namespace OpcUaAml.Tests;

public class Vdi3682LinkTests(DiDocument di) : IClassFixture<DiDocument>
{
    /// <summary>A process description as the FPB mapper writes it, and a UA instance with a method.</summary>
    private (InternalElementType Resource, InternalElementType Operator, InternalElementType Firmware, InternalElementType Clear) Setup(string name)
    {
        var fpd = di.Hierarchy(name + "_FPD");
        var process = fpd.InternalElement.Append("Process");
        var resource = process.InternalElement.Append("Screwdriver");
        resource.RefBaseSystemUnitPath = "VDI_FPD_SystemUnitClassLib/FPD_TechnicalResource";
        var op = process.InternalElement.Append("Tighten");
        op.RefBaseSystemUnitPath = "VDI_FPD_SystemUnitClassLib/FPD_ProcessOperator";

        var ua = di.Hierarchy(name + "_UA");
        ua.InternalElement.Insert(TypeInstantiator.Instantiate(di.Type("SoftwareVersionType"), "Firmware",
            new InstantiationOptions { IncludeOptional = p => p == "Clear" }).Instance, asFirst: false);
        var firmware = ua.InternalElement["Firmware"]!;
        return (resource, op, firmware, firmware.InternalElement["Clear"]!);
    }

    [Fact]
    public void Finds_the_process_description_elements_and_fitting_targets()
    {
        var (resource, op, firmware, clear) = Setup("Find");

        Assert.Contains(Vdi3682Links.FpdElements(di.Document, FpdKind.TechnicalResource), e => e.ID == resource.ID);
        Assert.Contains(Vdi3682Links.FpdElements(di.Document, FpdKind.ProcessOperator), e => e.ID == op.ID);
        Assert.Contains(Vdi3682Links.UaTargets(di.Document, FpdKind.TechnicalResource), e => e.ID == firmware.ID);
        Assert.DoesNotContain(Vdi3682Links.UaTargets(di.Document, FpdKind.TechnicalResource), e => e.ID == clear.ID);
        Assert.Contains(Vdi3682Links.UaTargets(di.Document, FpdKind.ProcessOperator), e => e.ID == clear.ID);
    }

    [Fact]
    public void Links_are_IDREF_attributes_derived_from_refObj()
    {
        var (resource, op, firmware, clear) = Setup("Link");

        var a = Vdi3682Links.Link(resource, firmware, FpdKind.TechnicalResource);
        var b = Vdi3682Links.Link(op, clear, FpdKind.ProcessOperator);

        Assert.Equal(("refOpcUaObject", "xs:IDREF", firmware.ID), (a.Name, a.AttributeDataType, a.Value));
        Assert.Equal("AMLOpcUa_ReferenceAttributeTypeLib/refOpcUaObject", a.RefAttributeType);
        Assert.Equal(clear.ID, b.Value);
        var own = di.Document.CAEXFile.AttributeTypeLib["AMLOpcUa_ReferenceAttributeTypeLib"]!;
        Assert.Equal("AutomationML_ObjectReferences_AttributeTypeLib/refObj", own.AttributeType["refOpcUaMethod"]!.RefAttributeType);
        Assert.NotNull(di.Document.CAEXFile.AttributeTypeLib["AutomationML_ObjectReferences_AttributeTypeLib"]!.AttributeType["refObj"]);

        var links = Vdi3682Links.Links(di.Document);
        Assert.Contains(links, l => l.Source.ID == resource.ID && l.Target?.ID == firmware.ID);
        Assert.Contains(links, l => l.Source.ID == op.ID && l.Target?.ID == clear.ID);
    }

    [Fact]
    public void Wrong_targets_are_refused()
    {
        var (resource, op, firmware, clear) = Setup("Wrong");

        Assert.Throws<LinkException>(() => Vdi3682Links.Link(op, firmware, FpdKind.ProcessOperator));
        Assert.Throws<LinkException>(() => Vdi3682Links.Link(resource, clear, FpdKind.TechnicalResource));
        Assert.Throws<LinkException>(() => Vdi3682Links.Link(firmware, clear, FpdKind.ProcessOperator));
    }

    [Fact]
    public void Relinking_replaces_and_a_removed_target_shows_as_unresolved()
    {
        var (resource, _, firmware, _) = Setup("Relink");
        var other = TypeInstantiator.Instantiate(di.Type("SoftwareVersionType"), "Other").Instance;
        ((InstanceHierarchyType)firmware.CAEXParent).InternalElement.Insert(other, asFirst: false);
        var otherInDoc = ((InstanceHierarchyType)firmware.CAEXParent).InternalElement["Other"]!;

        Vdi3682Links.Link(resource, firmware, FpdKind.TechnicalResource);
        Vdi3682Links.Link(resource, otherInDoc, FpdKind.TechnicalResource);
        Assert.Single(resource.Attribute, a => a.Name == "refOpcUaObject");
        Assert.Equal(otherInDoc.ID, resource.Attribute["refOpcUaObject"]!.Value);

        ((InstanceHierarchyType)otherInDoc.CAEXParent!).InternalElement.RemoveElement(otherInDoc);
        var link = Vdi3682Links.Links(di.Document).Single(l => l.Source.ID == resource.ID);
        Assert.Null(link.Target);
        Assert.Contains(OpcUaAml.Checks.AnnexAChecker.Check(di.Document),
            f => f.Rule == OpcUaAml.Checks.Rules.BrokenVdi3682Link && f.ElementId == resource.ID);
    }

    [Fact]
    public void Links_survive_saving_and_loading()
    {
        var (resource, op, firmware, clear) = Setup("Save");
        Vdi3682Links.Link(resource, firmware, FpdKind.TechnicalResource);
        Vdi3682Links.Link(op, clear, FpdKind.ProcessOperator);
        var file = Path.Combine(Path.GetTempPath(), $"links-{Guid.NewGuid():N}.aml");
        try
        {
            di.Document.SaveToFile(file, true);
            var reloaded = CAEXDocument.LoadFromFile(file);

            var links = Vdi3682Links.Links(reloaded);
            Assert.Contains(links, l => l.Source.Name == "Screwdriver" && l.Target?.Name == "Firmware");
            Assert.Contains(links, l => l.Source.Name == "Tighten" && l.Target?.Name == "Clear");
            Assert.Single(reloaded.CAEXFile.AttributeTypeLib, l => l.Name == "AutomationML_ObjectReferences_AttributeTypeLib");
        }
        finally { File.Delete(file); }
    }
}
