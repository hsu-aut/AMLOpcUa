using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Xml.Schema;
using Aml.Engine.CAEX;
using OpcUaAml.Compare;
using OpcUaAml.Export;
using Xunit.Abstractions;

namespace OpcUaAml.Tests;

/// <summary>
/// The export against the unit tests of AML-UA-XSLT (commit a144dcc): each
/// AML input exported by us and compared, as a graph, with the NodeSet the
/// XSLT produced for it (Fixtures/aml-ua-xslt). In compatibility mode the
/// graphs must be equal; by default they may differ only by the deviations
/// documented in docs/export.md, each of which is listed here.
/// </summary>
public class ExportConformanceTests(ITestOutputHelper output)
{
    private static readonly DateTime Date = new(2026, 9, 17, 0, 0, 0, DateTimeKind.Utc);

    private static IEnumerable<string> Names() =>
        Directory.GetFiles(Fixtures.Path("aml-ua-xslt", "AML"), "*.aml")
            .Select(Path.GetFileNameWithoutExtension)
            .OrderBy(n => n, StringComparer.Ordinal)!;

    public static TheoryData<string> Pairs()
    {
        var data = new TheoryData<string>();
        foreach (var name in Names()) data.Add(name);
        return data;
    }

    public static TheoryData<string, bool> PairsInBothModes()
    {
        var data = new TheoryData<string, bool>();
        foreach (var name in Names())
        {
            data.Add(name, false);
            data.Add(name, true);
        }
        return data;
    }

    internal static string AmlPath(string name) => Fixtures.Path("aml-ua-xslt", "AML", name + ".aml");
    private static XDocument Expected(string name) => XDocument.Load(Fixtures.Path("aml-ua-xslt", "OPC_UA", name + ".xml"));

    internal static XDocument Export(string name, bool compat) =>
        NodeSetExporter.ExportFile(AmlPath(name), new NodeSetExportOptions { PublicationDate = Date, XsltCompatibility = compat });

    private IReadOnlyList<Difference> Report(IReadOnlyList<Difference> diffs)
    {
        foreach (var d in diffs.Take(200)) output.WriteLine(d.ToString());
        return diffs;
    }

    [Fact]
    public void All_fifteen_pairs_are_present() => Assert.Equal(15, Names().Count());

    [Theory]
    [MemberData(nameof(Pairs))]
    public void In_compatibility_mode_the_graph_equals_the_XSLT_output(string name)
    {
        var diffs = Report(new NodeSetComparer().Compare(Export(name, compat: true), Expected(name)));
        Assert.Empty(diffs);
    }

    /// <summary>
    /// A difference between our default output (left) and the XSLT output
    /// (right) that a documented deviation explains.
    /// </summary>
    private sealed record Deviation(string File, string Id, string Why, Func<Difference, bool> Explains);

    private static bool Is(Difference d, DifferenceKind kind, string pattern) =>
        d.Kind == kind && Regex.IsMatch(d.Path, pattern);

    private const string IhVersionReference = @";s=InstanceHierarchy_(\w+)/ref:i=46>.*;s=InstanceHierarchy_\1_Version$";

    private static readonly Deviation[] Deviations =
    {
        // D1: display name "RefSematic".
        new("3_IE_Attribute", "D1", "RefSemantic display name",
            d => Is(d, DifferenceKind.Changed, "_RefSemantic/@DisplayName$") && d.Detail == "'RefSemantic' vs 'RefSematic'"),
        new("10_RefSemantic", "D1", "RefSemantic display name",
            d => Is(d, DifferenceKind.Changed, "_RefSemantic/@DisplayName$") && d.Detail == "'RefSemantic' vs 'RefSematic'"),

        // D2, D3: the AttributeTypes of test 8 carry an ID and a Version. The
        // XSLT names their properties by ID ("AttributeTypeClass" typo) and,
        // the document being CAEX 3.0, never references the Version. We name
        // them after the class and reference both.
        new("8_AMLAttributeLibrary", "D2/D3", "AttributeType properties named after the class, Version referenced",
            d => Regex.IsMatch(d.Path,
                @"AutomationMLTestAttributeTypeLib;s=(OrganizedAttributeType|AttributeType2|5164931f-b43f-4050-a34e-3add8b03bd01|5396f3d9-fcb8-4c4e-9d4c-803aa9ca6bda)(_AML_ID|_Version|/ref:i=46>.*(_AML_ID|_Version)$)")),

        // D5: every collection folder gets the descriptions of all libraries.
        new("1_AMLBaseLibraries", "D5", "collection description from its own children",
            d => Is(d, DifferenceKind.Changed, @";s=(InterfaceClassLibs|RoleClassLibs)/@Description$")),
        new("8_AMLAttributeLibrary", "D5", "collection description from its own children",
            d => Is(d, DifferenceKind.OnlyRight, @";s=InstanceHierarchies/@Description$")),
        new("11_Constraints", "D5", "collection description from its own children",
            d => Is(d, DifferenceKind.OnlyRight, @";s=InstanceHierarchies/@Description$")
                 || Is(d, DifferenceKind.Changed, @";s=(InterfaceClassLibs|AttributeTypeLibs)/@Description$")),

        // D12: the InstanceHierarchy's Version node exists, nothing references it.
        new("5_SUC", "D12", "InstanceHierarchy references its Version", d => Is(d, DifferenceKind.OnlyLeft, IhVersionReference)),
        new("10_RefSemantic", "D12", "InstanceHierarchy references its Version", d => Is(d, DifferenceKind.OnlyLeft, IhVersionReference)),
        new("11_Constraints", "D12", "InstanceHierarchy references its Version", d => Is(d, DifferenceKind.OnlyLeft, IhVersionReference)),
    };

    // D14: every file requires the models of the AutomationML standard
    // libraries it uses; the XSLT requires none of them.
    private static bool IsD14(Difference d) =>
        Is(d, DifferenceKind.OnlyLeft, @"/requires:http://opcfoundation\.org/UA/AML/.+$");

    [Fact]
    public void D14_shows_in_the_files_that_use_the_standard_libraries()
    {
        var diffs = Report(new NodeSetComparer().Compare(Export("5_SUC", compat: false), Expected("5_SUC")));
        Assert.Contains(diffs, d => IsD14(d) && d.Path.EndsWith("requires:http://opcfoundation.org/UA/AML/AutomationMLBaseRoleClassLib"));
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void By_default_the_graph_differs_only_by_documented_deviations(string name)
    {
        var diffs = Report(new NodeSetComparer().Compare(Export(name, compat: false), Expected(name)))
            .Where(d => !IsD14(d)).ToList();
        var deviations = Deviations.Where(d => d.File == name).ToList();

        Assert.Empty(diffs.Where(d => !deviations.Any(dev => dev.Explains(d))));
        foreach (var dev in deviations)
            Assert.True(diffs.Any(dev.Explains), $"{dev.Id} ({dev.Why}) no longer shows in {name}; take it off the list.");
    }

    [Fact]
    public void Nine_pairs_match_exactly_by_default()
    {
        var withDeviations = Deviations.Select(d => d.File).ToHashSet();
        Assert.Equal(6, withDeviations.Count);
        Assert.Equal(9, Names().Count(n => !withDeviations.Contains(n)));
    }

    [Theory]
    [MemberData(nameof(PairsInBothModes))]
    public void Output_is_valid_against_the_NodeSet_schema(string name, bool compat)
    {
        var schemas = new XmlSchemaSet();
        schemas.Add(NodeSetExporter.UaNodeSetNamespace, Path.Combine(AppContext.BaseDirectory, "UANodeSet.xsd"));
        var errors = new List<string>();
        Export(name, compat).Validate(schemas, (_, e) => errors.Add(e.Message));
        Assert.Empty(errors);
    }

    [Theory]
    [MemberData(nameof(PairsInBothModes))]
    public void Output_loads_with_the_OPC_UA_stack(string name, bool compat)
    {
        var nodeSet = Export(name, compat);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(NodeSetExporter.ToXml(nodeSet)));

        var loaded = Opc.Ua.Export.UANodeSet.Read(stream);

        var ua = XNamespace.Get(NodeSetExporter.UaNodeSetNamespace);
        Assert.Equal(nodeSet.Root!.Elements().Count(e => e.Name.LocalName.StartsWith("UA")), loaded.Items.Length);
        Assert.Equal(nodeSet.Root.Element(ua + "NamespaceUris")!.Elements().Count(), loaded.NamespaceUris.Length);
        Assert.Equal(nodeSet.Root.Element(ua + "Aliases")!.Elements().Count(), loaded.Aliases.Length);

        // Import resolves aliases, NodeIds and QualifiedNames into NodeStates.
        var context = new Opc.Ua.SystemContext(Opc.Ua.DefaultTelemetry.Create(_ => { }))
        {
            NamespaceUris = new Opc.Ua.NamespaceTable(),
            ServerUris = new Opc.Ua.StringTable(),
        };
        var states = new Opc.Ua.NodeStateCollection();
        loaded.Import(context, states);
        Assert.Equal(loaded.Items.Length, states.Count);
    }

    [Fact]
    public void The_schema_check_catches_an_invalid_NodeSet()
    {
        var nodeSet = Export("2_IE", compat: false);
        var ua = XNamespace.Get(NodeSetExporter.UaNodeSetNamespace);
        nodeSet.Root!.Element(ua + "UAObject")!.AddFirst(new XElement(ua + "Unknown"));
        var schemas = new XmlSchemaSet();
        schemas.Add(NodeSetExporter.UaNodeSetNamespace, Path.Combine(AppContext.BaseDirectory, "UANodeSet.xsd"));
        var errors = new List<string>();
        nodeSet.Validate(schemas, (_, e) => errors.Add(e.Message));
        Assert.NotEmpty(errors);
    }

    /// <summary>
    /// Every reference into a namespace the document defines nodes in points
    /// to a node that exists. The XSLT output has dangling references
    /// (deviations D3, D4, D8, D12); the default output of the fifteen inputs has none.
    /// </summary>
    [Theory]
    [MemberData(nameof(Pairs))]
    public void By_default_references_into_own_namespaces_resolve(string name)
    {
        var dangling = DanglingReferences(Export(name, compat: false));
        foreach (var d in dangling) output.WriteLine(d);
        Assert.Empty(dangling);
    }

    /// <summary>
    /// References and ParentNodeIds into a namespace the document defines
    /// nodes in, whose target node does not exist. The XSLT output has such
    /// references (deviations D3, D4, D8, D12).
    /// </summary>
    internal static List<string> DanglingReferences(XDocument nodeSet)
    {
        var ua = XNamespace.Get(NodeSetExporter.UaNodeSetNamespace);
        var aliases = nodeSet.Root!.Element(ua + "Aliases")!.Elements().ToDictionary(a => (string)a.Attribute("Alias")!, a => a.Value);
        var nodes = nodeSet.Root.Elements().Where(e => e.Name.LocalName.StartsWith("UA")).ToList();
        var ids = nodes.Select(n => (string)n.Attribute("NodeId")!).ToHashSet();
        var ownNamespaces = ids.Select(Namespace).ToHashSet();

        var dangling = new List<string>();
        foreach (var node in nodes)
        {
            var targets = node.Descendants(ua + "Reference").Select(r => r.Value)
                .Append((string?)node.Attribute("ParentNodeId"))
                .OfType<string>()
                .Select(t => aliases.TryGetValue(t, out var a) ? a : t);
            dangling.AddRange(targets.Where(t => ownNamespaces.Contains(Namespace(t)) && !ids.Contains(t))
                .Select(t => $"{node.Attribute("NodeId")!.Value} -> {t}"));
        }
        return dangling;

        static string Namespace(string nodeId) => nodeId.StartsWith("ns=") && nodeId.Contains(';') ? nodeId[..nodeId.IndexOf(';')] : "ns=0";
    }

    [Fact]
    public void A_CAEXDocument_exports_like_its_file()
    {
        const string name = "9_ExtInt_IntLink";
        var fromDocument = NodeSetExporter.Export(CAEXDocument.LoadFromFile(AmlPath(name)),
            new NodeSetExportOptions { PublicationDate = Date });

        Assert.Empty(Report(new NodeSetComparer().Compare(fromDocument, Export(name, compat: false))));
    }

    [Fact]
    public void The_publication_date_comes_from_the_options()
    {
        var dates = Export("5_SUC", compat: false).Descendants()
            .Where(e => e.Name.LocalName is "Model" or "RequiredModel")
            .Where(e => e.Attribute("PublicationDate") != null) // D14 requires a library's model without a date
            .Where(e => ((string)e.Attribute("ModelUri")!).StartsWith("http://opcfoundation.org/UA/AML/")
                        && (string)e.Attribute("ModelUri")! != "http://opcfoundation.org/UA/AML/")
            .Select(e => (string)e.Attribute("PublicationDate")!)
            .Distinct();

        Assert.Equal(new[] { "2026-09-17T00:00:00Z" }, dates);
    }
}
