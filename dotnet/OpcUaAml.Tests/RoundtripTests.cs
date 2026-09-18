using OpcUaAml.NodeSets;
using OpcUaAml.Roundtrip;

namespace OpcUaAml.Tests;

/// <summary>
/// The round trip through both standardized mappings. These pin down what the
/// chains keep and lose today; a change in the numbers is a change in one of
/// the mappings and should be looked at, not waved through.
/// </summary>
public class RoundtripTests
{
    private static Criterion C(RoundtripReport r, string name) => r.Criteria.Single(c => c.Name == name);

    [Fact]
    public void AML_to_UA_to_AML_keeps_instances_classes_IDs_and_units()
    {
        var report = RoundtripRunner.AmlUaAml(Fixtures.Path("aml-ua-xslt", "AML", "5_SUC.aml"), NodeSetCatalog.Create(Array.Empty<string>()));

        Assert.True(report.Completed, report.Error);
        Assert.Equal((1, 1), (C(report, "InstanceHierarchies present, as element").Total, C(report, "InstanceHierarchies present, as element").Kept));
        Assert.Equal(2, C(report, "InternalElements present, by path").Kept);
        Assert.Equal(5, C(report, "Classes present, by library and name").Kept);
        Assert.Equal(5, C(report, "Class ID recoverable").Kept);
        Assert.Equal(4, C(report, "Base class kept").Kept);
        Assert.Equal((1, 1), (C(report, "Attribute units recoverable").Total, C(report, "Attribute units recoverable").Kept));
    }

    [Fact]
    public void Role_and_interface_classes_come_back_as_system_unit_classes()
    {
        var report = RoundtripRunner.AmlUaAml(Fixtures.Path("aml-ua-xslt", "AML", "6_RCL.aml"), NodeSetCatalog.Create(Array.Empty<string>()));

        Assert.True(report.Completed, report.Error);
        var kind = C(report, "Class keeps its kind");
        Assert.Equal(0, kind.Kept);
        Assert.All(kind.LostExamples, e => Assert.Contains("RoleClass -> SystemUnitClass", e));
    }

    [Fact]
    public void Mirror_objects_stop_the_chain_at_the_Annex_A_import()
    {
        // The AML-UA-XSLT rules give a mirror object no HasTypeDefinition.
        var report = RoundtripRunner.AmlUaAml(Fixtures.Path("aml-ua-xslt", "AML", "12_MirrorObject.aml"), NodeSetCatalog.Create(Array.Empty<string>()));

        Assert.False(report.Completed);
        Assert.Equal("Annex A import (Opc2Aml)", report.FailedStep);
    }

    [Fact]
    public void UA_to_AML_to_UA_keeps_names_hierarchy_and_rules_as_attributes_only()
    {
        var report = RoundtripRunner.UaAmlUa(Fixtures.Path("uafx", "opc.ua.fx.data.nodeset2.xml"),
            NodeSetCatalog.Create(new[] { Fixtures.Path("uafx") }));

        Assert.True(report.Completed, report.Error);
        Assert.Equal(C(report, "Types present, by name").Total, C(report, "Types present, by name").Kept);
        Assert.Equal(C(report, "Supertype kept").Total, C(report, "Supertype kept").Kept);
        Assert.Equal(C(report, "Original NodeId recoverable").Total, C(report, "Original NodeId recoverable").Kept);
        Assert.Equal(0, C(report, "ModellingRule as HasModellingRule").Kept);
        Assert.Equal(C(report, "ModellingRule recoverable").Total, C(report, "ModellingRule recoverable").Kept);
        Assert.Equal(0, C(report, "DataTypes stay DataTypes").Kept);
        Assert.True(C(report, "DataTypes stay DataTypes").Total > 0);
    }

    [Fact]
    public void Names_from_required_models_and_declarations_named_like_AML_properties_are_matched()
    {
        // FX CM types reference DI types (FunctionalGroupType, LockingServicesType)
        // and declare a Property "Version", a name the export also uses for AML.
        var report = RoundtripRunner.UaAmlUa(Fixtures.Path("uafx", "opc.ua.fx.cm.nodeset2.xml"),
            NodeSetCatalog.Create(new[] { Fixtures.Path("uafx") }));

        Assert.True(report.Completed, report.Error);
        foreach (var name in new[] { "Supertype kept", "Instance declarations present, by name", "Declaration keeps its type definition" })
            Assert.Equal(C(report, name).Total, C(report, name).Kept);
    }

    [Fact]
    public void A_report_renders_as_a_markdown_table()
    {
        var report = new RoundtripReport("x.aml", RoundtripRunner.ChainB,
            new[] { new Criterion("Classes present", 4, 3, new[] { "Lib/A" }) }, null, null, TimeSpan.FromSeconds(1), Array.Empty<string>());

        var md = report.ToMarkdown();

        Assert.Contains("| Classes present | 4 | 3 | 75", md);
        Assert.Contains("`Lib/A`", md);
    }
}

public class BundledNodeSetTests
{
    [Fact]
    public void The_bundled_AML_standard_libraries_are_the_export_of_the_working_group_base_libraries()
    {
        // nodesets/Opc.Ua.AMLStandardLibraries.NodeSet2.xml is generated with
        // uaaml export 1_AMLBaseLibraries.aml --date 2026-01-01; this keeps it
        // in step with the exporter.
        var fresh = OpcUaAml.Export.NodeSetExporter.Export(
            System.Xml.Linq.XDocument.Load(Fixtures.Path("aml-ua-xslt", "AML", "1_AMLBaseLibraries.aml")),
            new OpcUaAml.Export.NodeSetExportOptions { PublicationDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
        var bundled = System.Xml.Linq.XDocument.Load(Path.Combine(NodeSetCatalog.BundledFolder, "Opc.Ua.AMLStandardLibraries.NodeSet2.xml"));

        Assert.Empty(new OpcUaAml.Compare.NodeSetComparer { ComparePublicationDates = true }.Compare(fresh, bundled));
    }
}
