using OpcUaAml.Documentation;

namespace OpcUaAml.Tests;

public class DocumentationTests(DiDocument di) : IClassFixture<DiDocument>
{
    [Fact]
    public void Documents_a_model_with_its_types_declarations_diagrams_and_data_types()
    {
        var html = ModelDocumentation.Html(di.Document, Fixtures.DiUri);

        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("<h1>DI</h1>", html);
        Assert.Contains("id=\"type-DeviceType\"", html);
        Assert.Matches("<tr><td>SerialNumber</td><td>Variable</td><td>PropertyType</td><td>Mandatory</td><td>HasProperty</td></tr>", html);
        Assert.Contains("<svg", html);
        Assert.Contains("id=\"data-DeviceHealthEnumeration\"", html);
        Assert.Contains("Subtype of <a href=\"#type-ComponentType\">ComponentType</a>", html);
        // One self-contained file: no external styles, scripts or pictures.
        Assert.DoesNotContain("<link", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("src=", html);
    }

    [Fact]
    public void A_model_the_document_lacks_is_named()
    {
        var ex = Assert.Throws<ArgumentException>(() => ModelDocumentation.Html(di.Document, "http://example.org/None/"));
        Assert.Contains("http://example.org/None/", ex.Message);
    }
}
