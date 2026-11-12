using Aml.Editor.Plugin.OpcUa;
using Aml.Engine.CAEX;
using OpcUaAml.Addressing;

namespace PluginTests;

/// <summary>
/// Bind to element… gave an element of the user's own only the NodeId. Reading
/// and keeping values live take elements with a Value attribute, so a variable
/// bound to an element without one was never read, and nothing said why.
/// </summary>
public class BindTests
{
    private static readonly UaNodeAddress Speed = new("http://hsu-hh.de/UA/MPS500/", UaIdType.Numeric, "6021", "urn:plant");

    private static InternalElementType Element(string? value = null)
    {
        var doc = CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX3_0);
        var ie = doc.CAEXFile.InstanceHierarchy.Append("Plant").InternalElement.Append("Spindle");
        if (value != null) ie.Attribute.Append("Value").Value = value;
        return ie;
    }

    [Fact]
    public void Binding_a_variable_gives_the_element_a_value_to_fill()
    {
        var element = Element();

        OpcUaPlugin.Bind(element, Speed, "Variable");

        Assert.Equal(Speed, AnnexANodeId.Of(element));
        Assert.NotNull(element.Attribute["Value"]);
    }

    [Fact]
    public void Binding_an_object_adds_no_value()
    {
        var element = Element();

        OpcUaPlugin.Bind(element, Speed, "Object");

        Assert.Null(element.Attribute["Value"]);
    }

    [Fact]
    public void A_value_the_element_has_is_kept()
    {
        var element = Element("42");

        OpcUaPlugin.Bind(element, Speed, "Variable");

        Assert.Equal("42", element.Attribute["Value"]!.Value);
    }
}
