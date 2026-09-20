using System.Xml.Linq;
using OpcUaAml.ModelDesign;

namespace OpcUaAml.Tests;

/// <summary>
/// The cases an audit of the writer turned up: names that collide, nodes that
/// hang off nothing, ranks a design cannot spell. Each one produced a file the
/// compiler could not use, or one that lost a NodeId without saying so.
/// </summary>
public class ModelDesignEdgeTests
{
    private static readonly XNamespace Opc = ModelDesignWriter.DesignNamespace;
    private static readonly XNamespace Ua = "http://opcfoundation.org/UA/2011/03/UANodeSet.xsd";
    private const string Uri = "http://example.org/Edge/";

    /// <summary>A NodeSet of the given nodes, with the usual head.</summary>
    private static ModelDesignResult Write(params string[] nodes)
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <UANodeSet xmlns="http://opcfoundation.org/UA/2011/03/UANodeSet.xsd">
              <NamespaceUris><Uri>{Uri}</Uri></NamespaceUris>
              <Models><Model ModelUri="{Uri}" Version="1.0.0" PublicationDate="2026-01-01T00:00:00Z" /></Models>
              {string.Join("\n  ", nodes)}
            </UANodeSet>
            """;
        return ModelDesignWriter.From(XDocument.Parse(xml));
    }

    private static string Type(string id, string browseName, string children = "") => $"""
        <UAObjectType NodeId="ns=1;i={id}" BrowseName="1:{browseName}">
          <DisplayName>{browseName}</DisplayName>
          <References><Reference ReferenceType="i=45" IsForward="false">i=58</Reference>{children}</References>
        </UAObjectType>
        """;

    private static IEnumerable<string> Symbols(XDocument design) =>
        design.Root!.Descendants().Select(e => (string?)e.Attribute("SymbolicName")).Where(s => s != null)!;

    private static List<string[]> Rows(string identifiers) =>
        identifiers.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim().Split(',')).ToList();

    [Fact]
    public void Two_names_that_become_one_symbol_stay_two_nodes()
    {
        // "My Type" and "My-Type" both turn into "My_Type"; so does a DataType
        // called "My_Type". Every one of them needs a path of its own, or the
        // identifier file gives one key two ids.
        var result = Write(
            Type("1000", "My Type"),
            Type("1001", "My-Type"),
            $"""
             <UADataType NodeId="ns=1;i=1002" BrowseName="1:My_Type">
               <DisplayName>My_Type</DisplayName>
               <References><Reference ReferenceType="i=45" IsForward="false">i=22</Reference></References>
             </UADataType>
             """);

        var symbols = Symbols(result.Design).ToList();
        Assert.Equal(symbols.Count, symbols.Distinct().Count());
        var keys = Rows(result.Identifiers).Select(r => r[0]).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.Equal(3, keys.Count);
        // The name that could not be kept is written out, so nothing is lost.
        var renamed = result.Design.Root!.Elements().Where(e => e.Element(Opc + "BrowseName") != null).ToList();
        Assert.Contains(renamed, e => e.Element(Opc + "BrowseName")!.Value == "My Type");
    }

    [Fact]
    public void A_child_two_parents_hold_belongs_to_one_and_is_a_reference_in_the_other()
    {
        var shared = """
            <UAObject NodeId="ns=1;i=1100" BrowseName="1:Shared" ParentNodeId="ns=1;i=1000">
              <DisplayName>Shared</DisplayName>
              <References>
                <Reference ReferenceType="i=47" IsForward="false">ns=1;i=1000</Reference>
                <Reference ReferenceType="i=40">i=58</Reference>
                <Reference ReferenceType="i=37">i=78</Reference>
              </References>
            </UAObject>
            """;
        var result = Write(
            Type("1000", "HolderA", """<Reference ReferenceType="i=47">ns=1;i=1100</Reference>"""),
            Type("1001", "HolderB", """<Reference ReferenceType="i=47">ns=1;i=1100</Reference>"""),
            shared);

        var a = result.Design.Root!.Elements(Opc + "ObjectType").Single(e => (string?)e.Attribute("SymbolicName") == "HolderA");
        var b = result.Design.Root!.Elements(Opc + "ObjectType").Single(e => (string?)e.Attribute("SymbolicName") == "HolderB");
        Assert.Equal("Shared", (string?)a.Element(Opc + "Children")!.Elements().Single().Attribute("SymbolicName"));
        // The second parent keeps the reference instead of losing it.
        Assert.Null(b.Element(Opc + "Children"));
        Assert.Equal("HolderA_Shared", b.Element(Opc + "References")!.Element(Opc + "Reference")!.Element(Opc + "TargetId")!.Value);
    }

    [Fact]
    public void A_node_no_parent_declares_is_written_on_its_own()
    {
        // The child names its parent, the parent does not name the child: it
        // used to fall out of the file while references still pointed at it.
        var result = Write(
            Type("1000", "Holder"),
            """
            <UAVariable NodeId="ns=1;i=1200" BrowseName="1:OnlyInverse" DataType="i=12">
              <DisplayName>OnlyInverse</DisplayName>
              <References>
                <Reference ReferenceType="i=46" IsForward="false">ns=1;i=1000</Reference>
                <Reference ReferenceType="i=40">i=68</Reference>
                <Reference ReferenceType="i=37">i=78</Reference>
              </References>
            </UAVariable>
            """);

        Assert.Contains("OnlyInverse", Symbols(result.Design));
        Assert.Contains(Rows(result.Identifiers), r => r[0] == "OnlyInverse" && r[1] == "1200");
    }

    [Fact]
    public void Two_nodes_that_hold_each_other_both_appear_and_no_reference_dangles()
    {
        var result = Write(
            Type("1000", "CycleA", """<Reference ReferenceType="i=47">ns=1;i=1001</Reference>"""),
            Type("1001", "CycleB", """<Reference ReferenceType="i=47">ns=1;i=1000</Reference>"""));

        var symbols = Symbols(result.Design).ToList();
        Assert.Contains("CycleA", symbols);
        Assert.Contains("CycleB", symbols);
        // Every target of a reference is a name the file knows.
        var targets = result.Design.Root!.Descendants(Opc + "TargetId").Select(t => t.Value)
            .Where(v => !v.Contains(':')).ToList();
        foreach (var target in targets) Assert.Contains(target.Split('_')[0], symbols);
    }

    [Fact]
    public void A_matrix_becomes_a_rank_the_design_can_spell()
    {
        var result = Write($"""
            <UAVariableType NodeId="ns=1;i=1300" BrowseName="1:MatrixType" DataType="i=11" ValueRank="2" ArrayDimensions="0,0">
              <DisplayName>MatrixType</DisplayName>
              <References><Reference ReferenceType="i=45" IsForward="false">i=63</Reference></References>
            </UAVariableType>
            """);

        var type = result.Design.Root!.Elements(Opc + "VariableType").Single();
        Assert.Equal("OneOrMoreDimensions", (string?)type.Attribute("ValueRank"));
        Assert.Equal("0,0", (string?)type.Attribute("ArrayDimensions"));
    }

    [Fact]
    public void A_string_NodeId_is_carried_on_the_node_and_no_empty_identifier_file_is_written()
    {
        var result = Write("""
            <UAObjectType NodeId="ns=1;s=StringIdType" BrowseName="1:StringIdType">
              <DisplayName>StringIdType</DisplayName>
              <References><Reference ReferenceType="i=45" IsForward="false">i=58</Reference></References>
            </UAObjectType>
            """);

        Assert.Equal("StringIdType", (string?)result.Design.Root!.Elements(Opc + "ObjectType").Single().Attribute("StringId"));
        Assert.Equal("", result.Identifiers);

        var folder = Directory.CreateTempSubdirectory("uaaml-design-empty-");
        try
        {
            var design = Path.Combine(folder.FullName, "Edge.xml");
            Assert.Null(result.Save(design));
            Assert.True(File.Exists(design));
            Assert.False(File.Exists(Path.ChangeExtension(design, ".csv")));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_name_with_a_comma_cannot_break_the_identifier_file()
    {
        // The encodings of a structure are named after the encoding node.
        var result = Write(
            """
            <UADataType NodeId="ns=1;i=1400" BrowseName="1:Vector">
              <DisplayName>Vector</DisplayName>
              <References><Reference ReferenceType="i=45" IsForward="false">i=22</Reference></References>
              <Definition Name="1:Vector"><Field Name="X" DataType="i=11" /></Definition>
            </UADataType>
            """,
            """
            <UAObject NodeId="ns=1;i=1401" BrowseName="1:Default,Binary">
              <DisplayName>Default,Binary</DisplayName>
              <References>
                <Reference ReferenceType="i=38" IsForward="false">ns=1;i=1400</Reference>
                <Reference ReferenceType="i=40">i=76</Reference>
              </References>
            </UAObject>
            """);

        Assert.All(Rows(result.Identifiers), row => Assert.Equal(3, row.Length));
        Assert.Contains(Rows(result.Identifiers), r => r[0] == "Vector_Encoding_Default_Binary" && r[1] == "1401");
    }

    [Fact]
    public void The_base_model_is_not_listed_twice_when_it_is_the_model_being_written()
    {
        var catalog = OpcUaAml.NodeSets.NodeSetCatalog.Create(Array.Empty<string>());
        var ua = catalog.Find(Fixtures.UaUri) ?? throw new InvalidOperationException("The base NodeSet is not bundled.");
        var result = ModelDesignWriter.FromFile(ua.FilePath);

        var uris = result.Design.Root!.Element(Opc + "Namespaces")!.Elements(Opc + "Namespace").Select(n => n.Value).ToList();
        Assert.Equal([ModelDesignWriter.UaNamespace], uris);
    }
}
