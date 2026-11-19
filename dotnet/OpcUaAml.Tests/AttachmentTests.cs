using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using OpcUaAml.Import;

namespace OpcUaAml.Tests;

/// <summary>
/// A file attached to an element: an ExternalInterface of the class
/// ExternalDataReference (AutomationMLInterfaceClassLib) with the file's URI
/// and MIME type, as AutomationML references any external document.
/// </summary>
public class AttachmentTests
{
    private static InternalElementType Plant(CAEXDocument doc) =>
        doc.CAEXFile.InstanceHierarchy.Append("Plant").InternalElement.Append("Line");

    [Fact]
    public void An_attachment_is_an_external_data_reference_with_its_uri()
    {
        var doc = CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX3_0);
        var line = Plant(doc);

        var attached = Attachments.Attach(line, "Plant.events-20261119-104112.csv", "text/csv", "Events 2026-11-19 10:41");

        Assert.Equal(Attachments.ExternalDataReference, attached.RefBaseClassPath);
        Assert.Equal("Plant.events-20261119-104112.csv", attached.Attribute["refURI"]!.Value);
        Assert.Equal("xs:anyURI", attached.Attribute["refURI"]!.AttributeDataType);
        Assert.Equal("text/csv", attached.Attribute["MIMEType"]!.Value);
        Assert.NotNull(doc.FindByPath(Attachments.ExternalDataReference));
    }

    [Fact]
    public void Each_save_makes_a_new_attachment()
    {
        var doc = CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX3_0);
        var line = Plant(doc);

        Attachments.Attach(line, "a.csv", "text/csv", "Events");
        Attachments.Attach(line, "b.csv", "text/csv", "Events");

        Assert.Equal(["Events", "Events 2"], line.ExternalInterface.Select(e => e.Name));
    }

    [Fact]
    public void The_file_is_named_after_the_document_and_the_time()
    {
        Assert.Equal("Plant.events-20261119-104112.csv",
            Attachments.FileName(Path.Combine("C:", "x", "Plant.amlx"), new DateTime(2026, 11, 19, 10, 41, 12)));
    }

    [Fact]
    public void A_document_names_the_file_beside_it_relatively_a_container_by_its_full_path()
    {
        var folder = Path.Combine(Path.GetTempPath(), "my plant");

        Assert.Equal("Plant.events-1.csv", Attachments.UriFor(Path.Combine(folder, "Plant.aml"), Path.Combine(folder, "Plant.events-1.csv")));
        Assert.Equal(new Uri(Path.Combine(folder, "Plant.events-1.csv")).AbsoluteUri,
            Attachments.UriFor(Path.Combine(folder, "Plant.amlx"), Path.Combine(folder, "Plant.events-1.csv")));
    }
}
