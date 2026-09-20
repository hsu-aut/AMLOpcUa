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

    /// <summary>A type of the base model, which DI brings along.</summary>
    public SystemUnitFamilyType UaType(string name) =>
        (SystemUnitFamilyType)Document.FindByPath($"[SUC_http://opcfoundation.org/UA/]/[{name}]")!;

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
    public void An_overridden_declaration_keeps_what_it_inherits()
    {
        // 3DFrameType declares CartesianCoordinates of a narrower type, which
        // replaces FrameType's declaration. In OPC UA the node is replaced, not
        // the hierarchy below it, so LengthUnit stays (OPC 10000-3, the fully
        // inherited instance declaration hierarchy); AML's own flattening drops it.
        var result = TypeInstantiator.Instantiate(di.UaType("3DFrameType"), "Frame",
            new InstantiationOptions { IncludeOptional = _ => true });

        var coordinates = result.Instance.InternalElement.Single(c => c.Name == "CartesianCoordinates");
        Assert.Contains("LengthUnit", ChildNames(coordinates));
        Assert.Contains("X", ChildNames(coordinates));
        Assert.Contains("CartesianCoordinates/LengthUnit", result.Included);
        Assert.Contains("Orientation/AngleUnit", result.Included);
    }

    [Fact]
    public void Every_link_of_an_instance_ends_inside_it()
    {
        // Copying a declaration in brings its own links along; a copy whose
        // links still pointed at the type would leave the document broken.
        foreach (var (_, type) in UaTypes.AllTypes(di.Document).Where(t => !UaTypes.IsAbstract(t.Type)))
        {
            var result = TypeInstantiator.Instantiate(type, "Probe",
                new InstantiationOptions { IncludeOptional = _ => true });
            var ids = result.Instance.Descendants<ExternalInterfaceType>().Select(ei => ei.ID)
                .Concat(result.Instance.ExternalInterface.Select(ei => ei.ID)).ToHashSet(StringComparer.Ordinal);
            foreach (var owner in new SystemUnitClassType[] { result.Instance }.Concat(result.Instance.Descendants<InternalElementType>()))
            {
                foreach (var link in owner.InternalLink)
                {
                    Assert.True(ids.Contains(link.RefPartnerSideA) && ids.Contains(link.RefPartnerSideB),
                        $"{type.Name}: the link '{link.Name}' leaves the instance.");
                }
            }
        }
    }

    [Fact]
    public void An_inherited_child_is_linked_the_way_it_was_declared()
    {
        var result = TypeInstantiator.Instantiate(di.UaType("3DFrameType"), "Frame",
            new InstantiationOptions { IncludeOptional = _ => true });

        var coordinates = result.Instance.InternalElement.Single(c => c.Name == "CartesianCoordinates");
        var unit = coordinates.InternalElement.Single(c => c.Name == "LengthUnit");
        var end = unit.ExternalInterface.Single(ei => ei.Name == "PropertyOf");
        var link = Assert.Single(coordinates.InternalLink, l => l.Name == "LengthUnit");
        Assert.Equal(end.ID, link.RefPartnerSideB);
        // The other end belongs to the parent, not to the type it was copied from.
        Assert.Contains(coordinates.ExternalInterface, ei => ei.ID == link.RefPartnerSideA);
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
    public void Placeholders_are_filled_with_the_named_children()
    {
        var ih = di.Hierarchy("Filled");
        var result = TypeInstantiator.Instantiate(di.Type("ConfigurableObjectType"), "Modules", new InstantiationOptions
        {
            FillPlaceholder = p => p == "<ObjectIdentifier>" ? new[] { new PlaceholderFill("ModuleA"), new PlaceholderFill("ModuleB") } : Array.Empty<PlaceholderFill>(),
        });
        ih.InternalElement.Insert(result.Instance, asFirst: false);
        var modules = ih.InternalElement["Modules"]!;

        Assert.Equal(new[] { "ModuleA", "ModuleB" }, result.Filled);
        Assert.Empty(result.OmittedPlaceholders);
        Assert.Contains("ModuleA", ChildNames(modules));
        Assert.Contains("ModuleB", ChildNames(modules));
        Assert.DoesNotContain(ChildNames(modules), n => n.StartsWith('<'));
        // Each child is linked to its owner as the placeholder was, through an interface of its own.
        var a = modules.InternalElement["ModuleA"]!.ExternalInterface.Select(e => e.ID).ToHashSet();
        var b = modules.InternalElement["ModuleB"]!.ExternalInterface.Select(e => e.ID).ToHashSet();
        Assert.Empty(a.Intersect(b));
        Assert.Contains(modules.InternalLink, l => l.Name == "ModuleA" && a.Contains(l.RefPartnerSideB));
        Assert.Contains(modules.InternalLink, l => l.Name == "ModuleB" && b.Contains(l.RefPartnerSideB));
        Assert.Empty(di.FindingsIn(ih));
    }

    [Fact]
    public void A_filled_mandatory_placeholder_is_no_finding()
    {
        var ih = di.Hierarchy("NetworkFilled");
        var result = TypeInstantiator.Instantiate(di.Type("NetworkType"), "Net", new InstantiationOptions
        {
            FillPlaceholder = p => p == "<ProfileIdentifier>" ? new[] { new PlaceholderFill("Ethernet") } : Array.Empty<PlaceholderFill>(),
        });
        ih.InternalElement.Insert(result.Instance, asFirst: false);

        Assert.Contains("Ethernet", result.Filled);
        Assert.DoesNotContain(di.FindingsIn(ih), f => f.Rule == Rules.MissingMandatoryPlaceholder);
    }

    [Fact]
    public void A_placeholder_can_be_filled_with_an_instance_of_another_type()
    {
        var ih = di.Hierarchy("Typed");
        var result = TypeInstantiator.Instantiate(di.Type("ConfigurableObjectType"), "Modules", new InstantiationOptions
        {
            FillPlaceholder = p => p == "<ObjectIdentifier>" ? new[] { new PlaceholderFill("Firmware", di.Type("SoftwareVersionType")) } : Array.Empty<PlaceholderFill>(),
        });
        ih.InternalElement.Insert(result.Instance, asFirst: false);
        var firmware = ih.InternalElement["Modules"]!.InternalElement["Firmware"]!;

        Assert.Equal("[SUC_http://opcfoundation.org/UA/DI/]/[SoftwareVersionType]", firmware.RefBaseSystemUnitPath);
        Assert.Contains("Manufacturer", ChildNames(firmware));
        Assert.Contains(ih.InternalElement["Modules"]!.InternalLink, l => l.Name == "Firmware");
        Assert.Empty(di.FindingsIn(ih));
    }

    [Fact]
    public void Two_children_of_one_name_are_refused()
    {
        Assert.Throws<InstantiationException>(() => TypeInstantiator.Instantiate(di.Type("ConfigurableObjectType"), "Modules", new InstantiationOptions
        {
            FillPlaceholder = p => p == "<ObjectIdentifier>" ? new[] { new PlaceholderFill("SupportedTypes") } : Array.Empty<PlaceholderFill>(),
        }));
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
    public void A_filled_placeholder_counts_whatever_the_child_is_called()
    {
        // A placeholder object is filled under a name of its own, a
        // placeholder method keeps the name of its declaration. The check used
        // to reject a child of the declaration's name, so a correct model was
        // reported and a wrong one was not (OPC 10000-3 6.4.4.4).
        var ih = di.Hierarchy("FilledByName");
        var instance = TypeInstantiator.Instantiate(di.Type("NetworkType"), "Net",
            new InstantiationOptions { FillPlaceholder = _ => [new PlaceholderFill("<ProfileIdentifier>", null)] }).Instance;
        ih.InternalElement.Insert(instance, asFirst: false);

        // The child carries the declaration's name and no placeholder rule of its own.
        var filled = instance.InternalElement.Single(e => e.Name == "<ProfileIdentifier>");
        Assert.All(filled.ExternalInterface, ei => Assert.Null(ei.Attribute[UaTypes.ModellingRuleAttribute]));
        Assert.DoesNotContain(di.FindingsIn(ih), f => f.Rule == Rules.MissingMandatoryPlaceholder);
    }

    [Fact]
    public void Two_declarations_of_one_name_in_different_namespaces_are_two_declarations()
    {
        // A BrowseName is unique with its namespace. Keyed by the bare name, a
        // Mandatory declaration of another model was silently dropped and the
        // generated library kept a link to an element nobody wrote.
        var doc = CAEXDocument.New_CAEXDocument();
        var lib = doc.CAEXFile.SystemUnitClassLib.Append("SUC_http://example.org/Two/");
        var type = lib.SystemUnitClass.Append("TwoNamesType");
        foreach (var uri in new[] { "http://example.org/Two/", "http://opcfoundation.org/UA/" })
        {
            var child = type.InternalElement.Append("NodeVersion");
            child.Attribute.Append("BrowseName").Attribute.Append("NamespaceUri").Value = uri;
        }

        var names = UaTypes.Declarations(type).Where(d => d.Name == "NodeVersion").ToList();

        Assert.Equal(2, names.Count);
        Assert.Single(names, d => UaTypes.QualifiedName((SystemUnitClassType)d.Element).StartsWith("http://opcfoundation.org/UA/"));
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
