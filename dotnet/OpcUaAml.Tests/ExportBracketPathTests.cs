using System.Xml.Linq;
using OpcUaAml.Export;
using OpcUaAml.Roundtrip;

namespace OpcUaAml.Tests;

/// <summary>
/// D13: class paths in CAEX 3.0 bracket notation, which every library of
/// OPC 10000-83 Annex A needs because its name is a namespace URI.
/// </summary>
public class ExportBracketPathTests
{
    private const string Doc = """
        <CAEXFile xmlns="http://www.dke.de/CAEX" SchemaVersion="3.0" FileName="Brackets.aml">
          <SuperiorStandardVersion>AutomationML 2.10</SuperiorStandardVersion>
          <InstanceHierarchy Name="Plant">
            <InternalElement Name="Pump1" ID="c1d7f6a2-0b8e-4c55-9d0e-7d0a2f7f6a01"
                             RefBaseSystemUnitPath="[SUC_http://example.org/Pumps/]/[PumpType]" />
          </InstanceHierarchy>
          <SystemUnitClassLib Name="SUC_http://example.org/Pumps/">
            <SystemUnitClass Name="BaseDeviceType" ID="c1d7f6a2-0b8e-4c55-9d0e-7d0a2f7f6a02" />
            <SystemUnitClass Name="PumpType" ID="c1d7f6a2-0b8e-4c55-9d0e-7d0a2f7f6a03"
                             RefBaseClassPath="[SUC_http://example.org/Pumps/]/[BaseDeviceType]" />
          </SystemUnitClassLib>
        </CAEXFile>
        """;

    private static UaGraph Export(bool compat) =>
        UaGraph.Load(NodeSetExporter.Export(XDocument.Parse(Doc),
            new NodeSetExportOptions { XsltCompatibility = compat, PublicationDate = new DateTime(2026, 9, 18) }));

    [Fact]
    public void Bracketed_paths_resolve_to_the_library_namespace()
    {
        var g = Export(compat: false);
        const string ns = "http://opcfoundation.org/UA/AML/SUC_http://example.org/Pumps/";

        var pump = g.Nodes.Values.Single(n => n.NodeClass == "UAObjectType" && n.BrowseName == "PumpType");
        Assert.Equal(ns + "|s=BaseDeviceType", pump.References.Single(r => r.Type == "HasSubtype" && !r.IsForward).Target);
        var instance = g.Nodes.Values.Single(n => n.NodeClass == "UAObject" && n.BrowseName == "Pump1");
        Assert.Equal(ns + "|s=PumpType", instance.References.Single(r => r.Type == "HasTypeDefinition").Target);
    }

    [Fact]
    public void Compatibility_mode_keeps_the_XSLT_behaviour()
    {
        var g = Export(compat: true);

        var pump = g.Nodes.Values.Single(n => n.NodeClass == "UAObjectType" && n.BrowseName == "PumpType");
        Assert.DoesNotContain("example.org/Pumps/|s=BaseDeviceType", pump.References.Single(r => r.Type == "HasSubtype" && !r.IsForward).Target);
    }
}
