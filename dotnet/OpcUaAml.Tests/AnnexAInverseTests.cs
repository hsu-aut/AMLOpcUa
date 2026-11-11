using System.Xml.Linq;
using System.Xml.Schema;
using OpcUaAml.Export;
using OpcUaAml.NodeSets;
using OpcUaAml.Roundtrip;

namespace OpcUaAml.Tests;

/// <summary>
/// The inverse of OPC 10000-83 Annex A, measured as chain C: a NodeSet into AML
/// by Annex A and back. What Annex A carries into AML has to come back; what it
/// never carries is counted apart. A change in these numbers is a change in the
/// inverse or in Opc2Aml and should be looked at.
/// </summary>
public class AnnexAInverseTests(BundledDiConversion di) : IClassFixture<BundledDiConversion>
{
    private static readonly XNamespace Ua = NodeSetExporter.UaNodeSetNamespace;

    private static Criterion C(RoundtripReport r, string name) => r.Criteria.Single(c => c.Name == name);

    private static void AllKept(RoundtripReport r, params string[] names)
    {
        foreach (var name in names) Assert.True(C(r, name).Kept == C(r, name).Total, $"{name}: {C(r, name).Kept}/{C(r, name).Total}");
    }

    /// <summary>Lost only where Annex A never put the node into AML.</summary>
    private static void OnlyAnnexALosses(RoundtripReport r, params string[] names)
    {
        foreach (var name in names) Assert.True(C(r, name).LostExamples.Count == 0, $"{name}: {string.Join(", ", C(r, name).LostExamples.Take(5))}");
    }

    [Fact]
    public void DI_comes_back_as_the_same_nodes()
    {
        var report = RoundtripRunner.UaAmlUaInverse(Path.Combine(NodeSetCatalog.BundledFolder, "Opc.Ua.Di.NodeSet2.xml"), di.Catalog);

        Assert.True(report.Completed, report.Error);
        Assert.Equal(RoundtripRunner.ChainC, report.Chain);
        AllKept(report, "Description kept", "ValueRank and ArrayDimensions kept", "IsAbstract, Symmetric, InverseName kept", "MethodDeclarationId kept");
        // The DataType of a VariableType without a Value attribute (DI i=468) and an
        // empty string (Annex A writes no value) count as lost on the way in.
        OnlyAnnexALosses(report, "Nodes present, with their node class", "BrowseName kept", "DisplayName kept", "ParentNodeId kept", "DataType kept", "Values kept");
        // 447 nodes; the 38 missing are the type dictionaries, their encodings
        // and nodes no type holds. The four JSON encodings DI does have are
        // among them: the inverse writes the encodings a structure needs
        // (Binary and XML) and does not invent a JSON one for a model that
        // never declared it.
        Assert.Equal(447, C(report, "Nodes present, with their node class").Total);
        Assert.Equal(409, C(report, "Nodes present, with their node class").Kept);
        Assert.Equal(0, C(report, "Documentation kept").Kept);
    }

    [Fact]
    public void UAFX_connection_manager_keeps_methods_arguments_and_structure_values()
    {
        var report = RoundtripRunner.UaAmlUaInverse(Fixtures.Path("uafx", "opc.ua.fx.cm.nodeset2.xml"), NodeSetCatalog.Create(new[] { Fixtures.Path("uafx") }));

        Assert.True(report.Completed, report.Error);
        AllKept(report, "MethodDeclarationId kept", "ValueRank and ArrayDimensions kept", "IsAbstract, Symmetric, InverseName kept");
        OnlyAnnexALosses(report, "Nodes present, with their node class", "ParentNodeId kept", "DataType kept");
        Assert.True(C(report, "Values kept").Kept >= 56, $"Values {C(report, "Values kept").Kept}");
        Assert.True(C(report, "DataType definitions kept (per field)").Kept >= 230);
    }

    [Fact]
    public void The_export_is_a_valid_NodeSet_of_the_one_namespace()
    {
        var nodeSet = NodeSetExporter.Export(di.Result.Document, new NodeSetExportOptions { Mode = ExportMode.AnnexAInverse });

        var schemas = new XmlSchemaSet();
        schemas.Add(NodeSetExporter.UaNodeSetNamespace, Path.Combine(AppContext.BaseDirectory, "UANodeSet.xsd"));
        var errors = new List<string>();
        nodeSet.Validate(schemas, (_, e) => errors.Add(e.Message));
        Assert.Empty(errors);

        var root = nodeSet.Root!;
        Assert.Equal(Fixtures.DiUri, root.Element(Ua + "NamespaceUris")!.Elements().First().Value);
        Assert.Equal(Fixtures.DiUri, (string?)root.Element(Ua + "Models")!.Element(Ua + "Model")!.Attribute("ModelUri"));
        // Methods, DataTypes and ReferenceTypes are nodes of their own class again.
        Assert.Contains(root.Elements(Ua + "UAMethod"), m => (string?)m.Attribute("BrowseName") == "1:InitLock");
        Assert.Contains(root.Elements(Ua + "UADataType"), d => (string?)d.Attribute("BrowseName") == "1:DeviceHealthEnumeration");
        Assert.Contains(root.Elements(Ua + "UAReferenceType"), r => (string?)r.Attribute("BrowseName") == "1:ConnectsTo");
        // Every NodeId once.
        var ids = root.Elements().Where(e => e.Name.LocalName.StartsWith("UA", StringComparison.Ordinal)).Select(e => (string)e.Attribute("NodeId")!).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void A_document_with_several_models_needs_the_namespace_named()
    {
        // Opc2Aml's container for FX AC also carries the models it requires.
        var document = OpcUaAml.Import.NodeSetImporter.ReadContainer(Fixtures.Path("uafx", "opc.ua.fx.ac.nodeset2.xml.amlx"));
        var root = document.CAEXFile.Node;
        Assert.Contains("http://opcfoundation.org/UA/FX/AC/", AnnexAInverse.Namespaces(root));
        Assert.Contains(Fixtures.DiUri, AnnexAInverse.Namespaces(root));

        var error = Assert.Throws<ArgumentException>(() => NodeSetExporter.Export(document, new NodeSetExportOptions { Mode = ExportMode.AnnexAInverse }));
        Assert.Contains("http://opcfoundation.org/UA/FX/AC/", error.Message);

        var nodeSet = NodeSetExporter.Export(document, new NodeSetExportOptions { Mode = ExportMode.AnnexAInverse, NamespaceUri = "http://opcfoundation.org/UA/FX/AC/" });
        Assert.Equal("http://opcfoundation.org/UA/FX/AC/", nodeSet.Root!.Element(Ua + "NamespaceUris")!.Elements().First().Value);
    }

    /// <summary>A small NodeSet of <paramref name="uri"/> into AML by Annex A, changed there if asked, and written back.</summary>
    private XDocument Back(string uri, string nodes, Action<Aml.Engine.CAEX.CAEXDocument>? edit = null)
    {
        var folder = Directory.CreateTempSubdirectory("opcuaaml-inverse-").FullName;
        try
        {
            var file = Path.Combine(folder, "Test.NodeSet2.xml");
            File.WriteAllText(file, $"""
                <UANodeSet xmlns="http://opcfoundation.org/UA/2011/03/UANodeSet.xsd">
                  <NamespaceUris><Uri>{uri}</Uri></NamespaceUris>
                  <Models><Model ModelUri="{uri}" Version="1.0.0" PublicationDate="2026-09-19T00:00:00Z">
                    <RequiredModel ModelUri="http://opcfoundation.org/UA/" Version="1.05.02" PublicationDate="2022-11-01T00:00:00Z" /></Model></Models>
                {nodes}
                </UANodeSet>
                """);
            var result = OpcUaAml.Import.NodeSetImporter.Convert(file, di.Catalog);
            edit?.Invoke(result.Document);
            return NodeSetExporter.Export(result.Document, new NodeSetExportOptions { Mode = ExportMode.AnnexAInverse, NamespaceUri = uri });
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static XElement ByBrowseName(XDocument nodeSet, string browseName) =>
        nodeSet.Root!.Elements().Single(e => (string?)e.Attribute("BrowseName") == browseName);

    [Fact]
    public void A_time_keeps_its_fractions_of_a_second()
    {
        var nodeSet = Back("urn:test:times", """
            <UAObjectType NodeId="ns=1;i=1000" BrowseName="1:ClockType">
              <DisplayName>ClockType</DisplayName>
              <References>
                <Reference ReferenceType="i=45" IsForward="false">i=58</Reference>
                <Reference ReferenceType="i=46">ns=1;i=1001</Reference>
                <Reference ReferenceType="i=46">ns=1;i=1002</Reference>
              </References>
            </UAObjectType>
            <UAVariable NodeId="ns=1;i=1001" BrowseName="1:Started" ParentNodeId="ns=1;i=1000" DataType="i=13">
              <DisplayName>Started</DisplayName>
              <References>
                <Reference ReferenceType="i=46" IsForward="false">ns=1;i=1000</Reference>
                <Reference ReferenceType="i=40">i=68</Reference>
                <Reference ReferenceType="i=37">i=78</Reference>
              </References>
              <Value><DateTime xmlns="http://opcfoundation.org/UA/2008/02/Types.xsd">2026-09-18T12:34:56.789Z</DateTime></Value>
            </UAVariable>
            <UAVariable NodeId="ns=1;i=1002" BrowseName="1:Stopped" ParentNodeId="ns=1;i=1000" DataType="i=13">
              <DisplayName>Stopped</DisplayName>
              <References>
                <Reference ReferenceType="i=46" IsForward="false">ns=1;i=1000</Reference>
                <Reference ReferenceType="i=40">i=68</Reference>
                <Reference ReferenceType="i=37">i=78</Reference>
              </References>
              <Value><DateTime xmlns="http://opcfoundation.org/UA/2008/02/Types.xsd">2026-09-18T12:34:56Z</DateTime></Value>
            </UAVariable>
            """,
            // Opc2Aml already drops the fraction on the way in; here it is a time given in AML.
            document => document.CAEXFile.Node.Descendants()
                .Single(e => e.Name.LocalName == "Attribute" && (string?)e.Attribute("Name") == "Value" && (string?)e.Parent?.Attribute("Name") == "Started")
                .Elements().Single(v => v.Name.LocalName == "Value").Value = "2026-09-18T12:34:56.789Z");

        XNamespace types = NodeSetExporter.UaTypesNamespace;
        Assert.Equal("2026-09-18T12:34:56.789Z", ByBrowseName(nodeSet, "1:Started").Element(Ua + "Value")!.Element(types + "DateTime")!.Value);
        // A whole second stays without a fraction.
        Assert.Equal("2026-09-18T12:34:56Z", ByBrowseName(nodeSet, "1:Stopped").Element(Ua + "Value")!.Element(types + "DateTime")!.Value);
    }

    [Fact]
    public void A_structure_value_writes_the_fields_of_its_base_structure_first()
    {
        // The XML encoding of MotorConfig : BaseConfig is Name (from BaseConfig), then Speed.
        var nodeSet = Back("urn:test:motor", """
            <UADataType NodeId="ns=1;i=3000" BrowseName="1:BaseConfig">
              <DisplayName>BaseConfig</DisplayName>
              <References><Reference ReferenceType="i=45" IsForward="false">i=22</Reference></References>
              <Definition Name="1:BaseConfig"><Field Name="Name" DataType="i=12" /></Definition>
            </UADataType>
            <UADataType NodeId="ns=1;i=3001" BrowseName="1:MotorConfig">
              <DisplayName>MotorConfig</DisplayName>
              <References>
                <Reference ReferenceType="i=45" IsForward="false">ns=1;i=3000</Reference>
                <Reference ReferenceType="i=38">ns=1;i=5001</Reference>
              </References>
              <Definition Name="1:MotorConfig"><Field Name="Speed" DataType="i=11" /></Definition>
            </UADataType>
            <UAObject NodeId="ns=1;i=5001" BrowseName="Default XML" SymbolicName="DefaultXml">
              <DisplayName>Default XML</DisplayName>
              <References>
                <Reference ReferenceType="i=38" IsForward="false">ns=1;i=3001</Reference>
                <Reference ReferenceType="i=40">i=76</Reference>
              </References>
            </UAObject>
            <UAObjectType NodeId="ns=1;i=1000" BrowseName="1:MotorType">
              <DisplayName>MotorType</DisplayName>
              <References>
                <Reference ReferenceType="i=45" IsForward="false">i=58</Reference>
                <Reference ReferenceType="i=46">ns=1;i=1001</Reference>
              </References>
            </UAObjectType>
            <UAVariable NodeId="ns=1;i=1001" BrowseName="1:Config" ParentNodeId="ns=1;i=1000" DataType="ns=1;i=3001">
              <DisplayName>Config</DisplayName>
              <References>
                <Reference ReferenceType="i=46" IsForward="false">ns=1;i=1000</Reference>
                <Reference ReferenceType="i=40">i=68</Reference>
                <Reference ReferenceType="i=37">i=78</Reference>
              </References>
              <Value>
                <ExtensionObject xmlns="http://opcfoundation.org/UA/2008/02/Types.xsd">
                  <TypeId><Identifier>ns=1;i=5001</Identifier></TypeId>
                  <Body><MotorConfig xmlns="urn:test:motor/Types.xsd"><Name>M1</Name><Speed>1500</Speed></MotorConfig></Body>
                </ExtensionObject>
              </Value>
            </UAVariable>
            """);

        XNamespace types = NodeSetExporter.UaTypesNamespace;
        var body = ByBrowseName(nodeSet, "1:Config").Element(Ua + "Value")?.Element(types + "ExtensionObject")?.Element(types + "Body")?.Elements().Single();
        Assert.NotNull(body);
        Assert.Equal(["Name", "Speed"], body.Elements().Select(f => f.Name.LocalName));
        Assert.Equal(["M1", "1500"], body.Elements().Select(f => f.Value));
    }
}
