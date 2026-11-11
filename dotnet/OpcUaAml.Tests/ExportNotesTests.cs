using System.Xml.Linq;
using OpcUaAml.Export;

namespace OpcUaAml.Tests;

/// <summary>
/// What the export cannot carry over as the document has it, it says
/// (docs/export.md, D18): the notes come with the NodeSet.
/// </summary>
public class ExportNotesTests
{
    private static readonly XDocument Document = XDocument.Parse("""
        <CAEXFile SchemaVersion="3.0" FileName="Orphans.aml" xmlns="http://www.dke.de/CAEX">
          <SuperiorStandardVersion>AutomationML 2.10</SuperiorStandardVersion>
          <SystemUnitClassLib Name="Lib">
            <SystemUnitClass Name="Pump" ID="11111111-1111-1111-1111-111111111111" RefBaseClassPath="Elsewhere/Machine" />
            <SystemUnitClass Name="Tank" ID="22222222-2222-2222-2222-222222222222" />
          </SystemUnitClassLib>
        </CAEXFile>
        """);

    private static XDocument Export(bool compat = false) =>
        NodeSetExporter.Export(Document, new NodeSetExportOptions { PublicationDate = new DateTime(2026, 9, 19, 0, 0, 0, DateTimeKind.Utc), XsltCompatibility = compat });

    [Fact]
    public void D18_a_base_class_in_no_library_of_the_document_is_named_in_the_notes()
    {
        var note = Assert.Single(NodeSetExporter.NotesOf(Export()));

        Assert.Contains("'Pump'", note);
        Assert.Contains("Elsewhere/Machine", note);
    }

    [Fact]
    public void Compatibility_mode_and_files_from_elsewhere_have_no_notes()
    {
        // The XSLT writes "ns=;s=" there and says nothing.
        Assert.Empty(NodeSetExporter.NotesOf(Export(compat: true)));
        Assert.Empty(NodeSetExporter.NotesOf(new XDocument(new XElement("UANodeSet"))));
    }

    [Fact]
    public void Saving_writes_the_NodeSet_the_export_returned()
    {
        var nodeSet = Export();
        var file = Path.Combine(Path.GetTempPath(), "amlopcua-notes-" + Guid.NewGuid().ToString("N")[..6] + ".xml");
        try
        {
            NodeSetExporter.Save(nodeSet, file);
            Assert.Equal(NodeSetExporter.ToXml(nodeSet), File.ReadAllText(file));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
