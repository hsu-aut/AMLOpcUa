// A left-to-right tree layout: the root on the left, each level one column
// further right, leaves stacked top to bottom and parents centred on their
// children. Readable for the deep, narrow structures UA types tend to have,
// and simple enough to be the same in the view and in the SVG.

namespace OpcUaAml.Diagram;

public static class DiagramLayout
{
    public const double NodeHeight = 38;
    public const double ColumnGap = 120;
    public const double RowGap = 12;
    public const double Margin = 20;
    public const double CharWidth = 7.2;

    public static UaDiagram Apply(UaDiagram diagram)
    {
        foreach (var n in diagram.Nodes)
        {
            var text = Math.Max(n.Name.Length + (n.ModellingRule != null ? n.ModellingRule.Length + 3 : 0),
                                Math.Max(n.TypeName?.Length ?? 0, (n.SupertypeName?.Length ?? -11) + 11) + 2);
            n.Width = Math.Max(90, text * CharWidth + 24);
            n.Height = NodeHeight;
        }

        var children = diagram.Edges.Where(e => e.Hierarchical)
            .GroupBy(e => e.From).ToDictionary(g => g.Key, g => g.Select(e => e.To).ToList());
        var byId = diagram.Nodes.ToDictionary(n => n.Id);
        var columnX = new Dictionary<int, double>();
        var x = Margin;
        foreach (var depth in diagram.Nodes.Select(n => n.Depth).Distinct().OrderBy(d => d))
        {
            columnX[depth] = x;
            x += diagram.Nodes.Where(n => n.Depth == depth).Max(n => n.Width) + ColumnGap;
        }

        var nextY = Margin;
        var root = diagram.Nodes[0];
        Place(root);
        foreach (var orphan in diagram.Nodes.Where(n => n != root && n.Y == 0 && n.X == 0)) Place(orphan);

        diagram.Width = diagram.Nodes.Max(n => n.X + n.Width) + Margin;
        diagram.Height = Math.Max(nextY, diagram.Nodes.Max(n => n.Y + n.Height)) + Margin;
        return diagram;

        void Place(DiagramNode node)
        {
            node.X = columnX[node.Depth];
            if (!children.TryGetValue(node.Id, out var kids) || kids.Count == 0)
            {
                node.Y = nextY;
                nextY += node.Height + RowGap;
                return;
            }
            foreach (var k in kids) Place(byId[k]);
            var first = byId[kids[0]];
            var last = byId[kids[^1]];
            node.Y = (first.Y + last.Y) / 2;
        }
    }
}
