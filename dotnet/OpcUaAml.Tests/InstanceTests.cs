using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using OpcUaAml.Checks;
using OpcUaAml.Import;
using OpcUaAml.Types;

namespace OpcUaAml.Tests;

/// <summary>A document holding the DI libraries, shared by the instance tests.</summary>
public sealed class DiDocument
{
    public CAEXDocument Document { get; } = CAEXDocument.New_CAEXDocument();

    public DiDocument()
    {
        var di = new BundledDiConversion();
        LibraryMerger.Merge(Document, di.Result.Document);
    }

    public SystemUnitFamilyType Type(string name) =>
        (SystemUnitFamilyType)Document.FindByPath($"[SUC_http://opcfoundation.org/UA/DI/]/[{name}]")!;

    /// <summary>A fresh hierarchy per test; findings are filtered by its name.</summary>
    public InstanceHierarchyType Hierarchy(string name) => Document.CAEXFile.InstanceHierarchy.Append(name);

    public IReadOnlyList<Finding> FindingsIn(InstanceHierarchyType ih) =>
        AnnexAChecker.Check(Document).Where(f => f.ElementPath.StartsWith(ih.Name + "/", StringComparison.Ordinal)).ToList();
}

public class InstanceTests(DiDocument di) : IClassFixture<DiDocument>
{
    private static IEnumerable<string> ChildNames(InternalElementType ie) => ie.InternalElement.Select(c => c.Name);

    [Fact]
    public void Mandatory_children_are_created_optional_ones_are_not()
    {
        var result = TypeInstantiator.Instantiate(di.Type("SoftwareVersionType"), "Firmware");

        Assert.Subset(ChildNames(result.Instance).ToHashSet(), new HashSet<string> { "Manufacturer", "ManufacturerUri", "SoftwareRevision" });
        Assert.DoesNotContain("PatchIdentifiers", ChildNames(result.Instance));
        Assert.Contains("PatchIdentifiers", result.OmittedOptional);
        Assert.Equal("Firmware", result.Instance.Name);
        Assert.Equal("[SUC_http://opcfoundation.org/UA/DI/]/[SoftwareVersionType]", result.Instance.RefBaseSystemUnitPath);
    }

    [Fact]
    public void Chosen_optional_children_are_created()
    {
        var result = TypeInstantiator.Instantiate(di.Type("SoftwareVersionType"), "Firmware",
            new InstantiationOptions { IncludeOptional = p => p == "ReleaseDate" });

        Assert.Contains("ReleaseDate", ChildNames(result.Instance));
        Assert.DoesNotContain("Hash", ChildNames(result.Instance));
    }

    [Fact]
    public void Inherited_mandatory_children_are_created()
    {
        // SoftwareType derives from ComponentType, which derives from
        // TopologyElementType; its Mandatory children come from all three.
        var result = TypeInstantiator.Instantiate(di.Type("SoftwareType"), "Sw");

        Assert.Subset(ChildNames(result.Instance).ToHashSet(), new HashSet<string> { "Manufacturer", "Model", "SoftwareRevision" });
    }

    [Fact]
    public void Placeholders_are_never_copied()
    {
        var result = TypeInstantiator.Instantiate(di.Type("ConfigurableObjectType"), "Modules");

        Assert.Contains("SupportedTypes", ChildNames(result.Instance));
        Assert.DoesNotContain(ChildNames(result.Instance), n => n.StartsWith('<'));
        Assert.Contains("<ObjectIdentifier>", result.OmittedPlaceholders);
    }

    [Fact]
    public void Abstract_types_are_refused_unless_allowed()
    {
        var ex = Assert.Throws<InstantiationException>(() => TypeInstantiator.Instantiate(di.Type("DeviceType"), "Dev"));
        Assert.Contains("abstract", ex.Message);

        var forced = TypeInstantiator.Instantiate(di.Type("DeviceType"), "Dev", new InstantiationOptions { AllowAbstract = true });
        Assert.NotNull(forced.Instance);
    }

    [Fact]
    public void Type_only_information_is_removed()
    {
        var instance = TypeInstantiator.Instantiate(di.Type("SoftwareVersionType"), "Firmware").Instance;
        var all = new SystemUnitClassType[] { instance }.Concat(instance.Descendants<InternalElementType>()).ToList();

        Assert.Null(instance.Attribute["IsAbstract"]);
        Assert.Null(instance.Attribute["BrowseName"]);
        Assert.All(all, e => Assert.Null(e.Attribute["NodeId"]));
        Assert.All(all.SelectMany(e => e.ExternalInterface), ei => Assert.Null(ei.Attribute[UaTypes.ModellingRuleAttribute]));
        Assert.NotEqual(di.Type("SoftwareVersionType").ID, instance.ID);
    }

    [Fact]
    public void A_fresh_instance_passes_the_check()
    {
        var ih = di.Hierarchy("Fresh");
        foreach (var name in new[] { "SoftwareVersionType", "SoftwareType", "ConfigurableObjectType", "LockingServicesType", "TransferServicesType" })
            ih.InternalElement.Insert(TypeInstantiator.Instantiate(di.Type(name), name + "_1").Instance, asFirst: false);

        Assert.Empty(di.FindingsIn(ih));
    }

    [Fact]
    public void Links_into_omitted_children_are_removed()
    {
        var ih = di.Hierarchy("Links");
        var instance = TypeInstantiator.Instantiate(di.Type("SoftwareVersionType"), "Firmware").Instance;
        ih.InternalElement.Insert(instance, asFirst: false);

        Assert.DoesNotContain(di.FindingsIn(ih), f => f.Rule == Rules.DanglingLink);
        Assert.NotEmpty(instance.InternalLink);
    }

    [Fact]
    public void A_missing_mandatory_child_is_an_error()
    {
        var ih = di.Hierarchy("Missing");
        var instance = TypeInstantiator.Instantiate(di.Type("SoftwareVersionType"), "Firmware").Instance;
        ih.InternalElement.Insert(instance, asFirst: false);
        var inserted = ih.InternalElement["Firmware"]!;
        foreach (var link in inserted.InternalLink.Where(l => l.Name == "Manufacturer").ToList())
            inserted.InternalLink.RemoveElement(link);
        inserted.InternalElement.RemoveElement(inserted.InternalElement["Manufacturer"]!);

        var finding = Assert.Single(di.FindingsIn(ih));
        Assert.Equal(Rules.MissingMandatory, finding.Rule);
        Assert.Contains("Manufacturer", finding.Message);
    }

    [Fact]
    public void A_mandatory_placeholder_needs_an_instance()
    {
        // NetworkType declares <ProfileIdentifier> as MandatoryPlaceholder.
        var ih = di.Hierarchy("Network");
        ih.InternalElement.Insert(TypeInstantiator.Instantiate(di.Type("NetworkType"), "Net").Instance, asFirst: false);

        var finding = Assert.Single(di.FindingsIn(ih));
        Assert.Equal(Rules.MissingMandatoryPlaceholder, finding.Rule);
        Assert.Contains("<ProfileIdentifier>", finding.Message);
    }

    [Fact]
    public void Abstract_unknown_and_placeholder_elements_are_reported()
    {
        var ih = di.Hierarchy("Odd");
        ih.InternalElement.Insert(TypeInstantiator.Instantiate(di.Type("DeviceType"), "Dev",
            new InstantiationOptions { AllowAbstract = true }).Instance, asFirst: false);
        var unknown = ih.InternalElement.Append("Ghost");
        unknown.RefBaseSystemUnitPath = "[SUC_http://example.org/Nothing/]/[GhostType]";
        ih.InternalElement.Append("<Something>");

        var rules = di.FindingsIn(ih).Select(f => f.Rule).ToHashSet();

        Assert.Contains(Rules.AbstractType, rules);
        Assert.Contains(Rules.UnknownType, rules);
        Assert.Contains(Rules.PlaceholderInInstance, rules);
    }

    [Fact]
    public void Links_are_checked_against_RefClassConnectsToPath()
    {
        var ih = di.Hierarchy("Pairs");
        ih.InternalElement.Insert(TypeInstantiator.Instantiate(di.Type("SoftwareVersionType"), "Firmware").Instance, asFirst: false);
        var fw = ih.InternalElement["Firmware"]!;
        var hasComponent = fw.ExternalInterface.First(e => e.RefBaseClassPath.EndsWith("[HasComponent]") || e.Name.StartsWith("HasComponent"));
        var propertyOf = fw.InternalElement["Manufacturer"]!.ExternalInterface.First(e => e.RefBaseClassPath.EndsWith("[PropertyOf]"));

        var link = fw.InternalLink.Append("Wrong");
        link.RefPartnerSideA = hasComponent.ID;
        link.RefPartnerSideB = propertyOf.ID;
        var dangling = fw.InternalLink.Append("Dangling");
        dangling.RefPartnerSideA = hasComponent.ID;
        dangling.RefPartnerSideB = "no-such-interface";

        var findings = di.FindingsIn(ih);

        Assert.Contains(findings, f => f.Rule == Rules.WrongLinkPair && f.ElementPath.EndsWith("/Wrong"));
        Assert.Contains(findings, f => f.Rule == Rules.DanglingLink && f.ElementPath.EndsWith("/Dangling"));
        Assert.Equal(2, findings.Count);
    }
}
