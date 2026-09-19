using System.Xml.Linq;
using OpcUaAml.Diagram;
using OpcUaAml.Types;

namespace OpcUaAml.Tests;

public class DiagramTests(DiDocument di) : IClassFixture<DiDocument>
{
    [Fact]
    public void A_type_shows_its_declarations_with_kind_rule_and_reference()
    {
        var d = DiagramLayout.Apply(DiagramBuilder.Build(di.Type("SoftwareVersionType")));

        var root = d.Nodes[0];
        Assert.Equal(UaNodeKind.ObjectType, root.Kind);
        Assert.Equal("BaseObjectType", root.SupertypeName);
        var manufacturer = d.Nodes.Single(n => n.Name == "Manufacturer");
        Assert.Equal(UaNodeKind.Variable, manufacturer.Kind);
        Assert.Equal("M", manufacturer.ModellingRule);
        Assert.Equal("PropertyType", manufacturer.TypeName);
        var clear = d.Nodes.Single(n => n.Name == "Clear");
        Assert.Equal(UaNodeKind.Method, clear.Kind);
        Assert.Null(clear.TypeName);
        Assert.Contains(d.Edges, e => e.To == manufacturer.Id && e.ReferenceType == "HasProperty" && e.Hierarchical);
        Assert.Contains(d.Edges, e => e.To == clear.Id && e.ReferenceType == "HasComponent");
    }

    [Fact]
    public void A_method_with_arguments_hangs_by_HasComponent_and_is_drawn_so()
    {
        // The method's own HasProperty interface leads to its arguments; its
        // end of the reference from the type is [HasComponent]/[ComponentOf].
        var d = DiagramLayout.Apply(DiagramBuilder.Build(di.Type("LockingServicesType")));
        var initLock = d.Nodes.Single(n => n.Name == "InitLock");
        var toMethod = d.Edges.Single(e => e.To == initLock.Id);
        var toArguments = d.Edges.First(e => e.From == initLock.Id);

        Assert.Equal("HasComponent", toMethod.ReferenceType);
        Assert.Equal("HasProperty", toArguments.ReferenceType);
        var line = new[] { (0.0, 0.0), (100.0, 0.0) };
        Assert.Single(EdgeGlyphs.For(EdgeGlyphs.NotationOf(toMethod), line));
        Assert.Equal(2, EdgeGlyphs.For(EdgeGlyphs.NotationOf(toArguments), line).Count);
        Assert.False(EdgeGlyphs.Labelled(EdgeGlyphs.NotationOf(toMethod)));
        Assert.Equal(2, EdgeGlyphs.For(EdgeNotation.Symmetric, line).Count(g => g.Closed && g.Filled));
    }

    [Fact]
    public void Inherited_declarations_are_part_of_the_picture()
    {
        var d = DiagramBuilder.Build(di.Type("SoftwareType"), maxDepth: 1);

        Assert.Contains(d.Nodes, n => n.Name == "Model");
        Assert.Contains(d.Nodes, n => n.Name == "SerialNumber");
    }

    [Fact]
    public void An_instance_shows_its_type_names()
    {
        var instance = TypeInstantiator.Instantiate(di.Type("SoftwareVersionType"), "Firmware").Instance;
        di.Hierarchy("DiagramInstance").InternalElement.Insert(instance, asFirst: false);

        var d = DiagramBuilder.Build(instance);

        Assert.Equal("SoftwareVersionType", d.Nodes[0].TypeName);
        Assert.All(d.Nodes.Skip(1), n => Assert.Null(n.ModellingRule));
    }

    [Fact]
    public void Depth_and_node_limits_cut_the_picture()
    {
        var shallow = DiagramBuilder.Build(di.Type("DeviceType"), maxDepth: 1);
        var capped = DiagramBuilder.Build(di.Type("DeviceType"), maxNodes: 5);

        Assert.All(shallow.Nodes, n => Assert.True(n.Depth <= 1));
        Assert.Equal(5, capped.Nodes.Count);
        Assert.True(capped.Truncated);
    }

    [Fact]
    public void Layout_places_children_right_of_their_parent_without_overlap()
    {
        var d = DiagramLayout.Apply(DiagramBuilder.Build(di.Type("DeviceType"), maxDepth: 2));
        var byId = d.Nodes.ToDictionary(n => n.Id);

        foreach (var e in d.Edges.Where(e => e.Hierarchical))
            Assert.True(byId[e.To].X > byId[e.From].X + byId[e.From].Width);
        foreach (var column in d.Nodes.GroupBy(n => n.Depth))
        {
            var ordered = column.OrderBy(n => n.Y).ToList();
            for (var i = 1; i < ordered.Count; i++)
                Assert.True(ordered[i].Y >= ordered[i - 1].Y + ordered[i - 1].Height, $"{ordered[i].Name} overlaps {ordered[i - 1].Name}");
        }
        Assert.True(d.Width > 0 && d.Height > 0);
    }

    [Fact]
    public void SVG_is_well_formed_and_draws_every_node()
    {
        var d = DiagramLayout.Apply(DiagramBuilder.Build(di.Type("SoftwareVersionType")));

        var svg = XDocument.Parse(SvgWriter.Write(d));

        XNamespace ns = "http://www.w3.org/2000/svg";
        var texts = svg.Descendants(ns + "text").Select(t => t.Value).ToList();
        Assert.All(d.Nodes, n => Assert.Contains(texts, t => t.StartsWith(n.Name)));
        Assert.Single(svg.Descendants(ns + "ellipse"));
    }
}

