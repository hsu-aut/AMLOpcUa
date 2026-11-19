using System.Xml.Linq;
using Aml.Engine.CAEX;
using OpcUaAml.Alarms;
using OpcUaAml.Export;
using OpcUaAml.Import;
using OpcUaAml.Server;
using OpcUaAml.Types;

namespace OpcUaAml.Tests;

/// <summary>
/// An alarm's InputNode and NormalState name their elements by ID in the
/// document; exported as a NodeSet (Annex A, inverse), they hold the NodeIds
/// the export gives those elements.
/// </summary>
public class ExportAlarmTests(DiDocument di) : IClassFixture<DiDocument>
{
    private const string Ns = "http://example.org/AlarmPlant/";
    private static readonly XNamespace Ua = "http://opcfoundation.org/UA/2011/03/UANodeSet.xsd";
    private static readonly XNamespace Types = "http://opcfoundation.org/UA/2008/02/Types.xsd";

    private static InternalElementType Variable(SystemUnitFamilyType type, IInternalElementContainer parent, string name, string xsType, string value)
    {
        parent.InternalElement.Insert(TypeInstantiator.Instantiate(type, name).Instance, asFirst: false);
        var element = parent.InternalElement[name]!;
        var attribute = element.Attribute["Value"] ?? element.Attribute.Append("Value");
        attribute.AttributeDataType = xsType;
        attribute.Value = value;
        return element;
    }

    [Fact]
    public void The_exported_input_node_names_the_exported_value()
    {
        var doc = CAEXDocument.LoadFromString(di.Document.XDocument.ToString());
        foreach (var other in doc.CAEXFile.InstanceHierarchy.ToList()) doc.CAEXFile.InstanceHierarchy.RemoveElement(other);
        var ih = doc.CAEXFile.InstanceHierarchy.Append(InstanceTransfer.HierarchyName(Ns));
        SystemUnitFamilyType UaType(string name) => (SystemUnitFamilyType)Aml.Engine.CAEX.Extensions.CAEXDocumentExtensions.FindByPath(doc, $"[SUC_http://opcfoundation.org/UA/]/[{name}]")!;
        ih.InternalElement.Insert(TypeInstantiator.Instantiate(UaType("BaseObjectType"), "Drill").Instance, asFirst: false);
        var drill = ih.InternalElement["Drill"]!;
        var speed = Variable(UaType("BaseDataVariableType"), drill, "SpindleSpeed", "xs:double", "1080");
        var empty = Variable(UaType("BaseDataVariableType"), drill, "MagazineEmpty", "xs:boolean", "false");
        AlarmElements.Create(speed, AlarmKind.High, 1200, null, 800, "Spindle overloaded", "SpindleSpeedHigh");
        AlarmElements.Create(empty, AlarmKind.State, null, false, 500, "Magazine empty", "MagazineEmptyAlarm");

        var nodeSet = AnnexAInverse.Export(doc.XDocument, new AnnexAInverseOptions { NamespaceUri = Ns });

        var nodes = nodeSet.Root!.Elements().Where(e => e.Name.LocalName.StartsWith("UA", StringComparison.Ordinal)).ToList();
        // The base model's BrowseNames (InputNode) stand without a namespace index, the model's own with one (1:Drill).
        static bool Named(XElement node, string name) => (string?)node.Attribute("BrowseName") is { } b && (b == name || b.EndsWith(":" + name, StringComparison.Ordinal));
        string IdOf(string browseName, string? parentBrowseName = null) =>
            (string)nodes.Single(n => Named(n, browseName)
                && (parentBrowseName == null || nodes.SingleOrDefault(p => (string?)p.Attribute("NodeId") == (string?)n.Attribute("ParentNodeId")) is { } parent && Named(parent, parentBrowseName)))
                .Attribute("NodeId")!;
        string? ValueOf(string browseName, string parentBrowseName)
        {
            var id = IdOf(browseName, parentBrowseName);
            return nodes.Single(n => (string?)n.Attribute("NodeId") == id).Element(Ua + "Value")?.Element(Types + "NodeId")?.Element(Types + "Identifier")?.Value;
        }

        Assert.Equal(IdOf("SpindleSpeed"), ValueOf("InputNode", "SpindleSpeedHigh"));
        Assert.Equal(IdOf("MagazineEmpty"), ValueOf("InputNode", "MagazineEmptyAlarm"));
        Assert.Equal(IdOf("NormalValue", "MagazineEmptyAlarm"), ValueOf("NormalState", "MagazineEmptyAlarm"));
    }
}
