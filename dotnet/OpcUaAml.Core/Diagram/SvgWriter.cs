// SVG of a laid-out diagram, for papers and documentation. Shapes follow the
// node classes of OPC 10000-3: Object a rectangle, Variable a rounded
// rectangle, Method an ellipse, types the same shapes shaded. References use
// the notation of Annex C (EdgeGlyphs); those other than HasComponent and
// HasProperty carry their name in italic.

using System.Globalization;
using System.Security;
using System.Text;

namespace OpcUaAml.Diagram;

public static class SvgWriter
{
    public static string Write(UaDiagram diagram)
    {
        static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(diagram.Width)}\" height=\"{F(diagram.Height)}\" " +
                      $"viewBox=\"0 0 {F(diagram.Width)} {F(diagram.Height)}\" font-family=\"Segoe UI, Arial, sans-serif\" font-size=\"12\">");
        sb.AppendLine($"  <title>{Esc(diagram.Title)}</title>");

        var byId = diagram.Nodes.ToDictionary(n => n.Id);
        foreach (var e in diagram.Edges)
        {
            if (!byId.TryGetValue(e.From, out var a) || !byId.TryGetValue(e.To, out var b)) continue;
            var (x1, y1, x2, y2) = e.Hierarchical
                ? (a.X + a.Width, a.Y + a.Height / 2, b.X, b.Y + b.Height / 2)
                : (a.X + a.Width / 2, a.Y + a.Height, b.X + b.Width / 2, b.Y);
            var points = e.Hierarchical
                ? new[] { (x1, y1), ((x1 + x2) / 2, y1), ((x1 + x2) / 2, y2), (x2, y2) }
                : new[] { (x1, y1), (x2, y2) };
            sb.AppendLine($"  <polyline points=\"{string.Join(" ", points.Select(p => $"{F(p.Item1)},{F(p.Item2)}"))}\" fill=\"none\" stroke=\"#444\" stroke-width=\"1\"/>");
            var notation = EdgeGlyphs.NotationOf(e);
            foreach (var g in EdgeGlyphs.For(notation, points))
            {
                var pts = string.Join(" ", g.Points.Select(p => $"{F(p.X)},{F(p.Y)}"));
                sb.AppendLine(g.Closed
                    ? $"  <polygon points=\"{pts}\" fill=\"{(g.Filled ? "#444" : "#fff")}\" stroke=\"#444\" stroke-width=\"1\"/>"
                    : $"  <polyline points=\"{pts}\" fill=\"none\" stroke=\"#444\" stroke-width=\"1.2\"/>");
            }
            if (!EdgeGlyphs.Labelled(notation)) continue;
            var lx = e.Hierarchical ? (x1 + x2) / 2 + 3 : (x1 + x2) / 2 + 4;
            var ly = e.Hierarchical ? y2 - 4 : (y1 + y2) / 2;
            sb.AppendLine($"  <text x=\"{F(lx)}\" y=\"{F(ly)}\" font-size=\"9\" font-style=\"italic\" fill=\"#666\">{Esc(e.ReferenceType)}</text>");
        }

        foreach (var n in diagram.Nodes)
        {
            var type = n.Kind is UaNodeKind.ObjectType or UaNodeKind.VariableType;
            var fill = type ? "#dde6f0" : "#ffffff";
            if (type)
                sb.AppendLine(Shape(n, n.X + 4, n.Y + 4, "#9aa8b8", "none"));
            sb.AppendLine(Shape(n, n.X, n.Y, fill, "#222"));
            var label = n.Name + (n.ModellingRule != null ? $" ({n.ModellingRule})" : "");
            var cx = n.X + n.Width / 2;
            var second = n.TypeName != null ? ":" + n.TypeName : n.SupertypeName != null ? "subtype of " + n.SupertypeName : null;
            sb.AppendLine($"  <text x=\"{F(cx)}\" y=\"{F(n.Y + (second != null ? 16 : 23))}\" text-anchor=\"middle\" font-weight=\"600\">{Esc(label)}</text>");
            if (second != null)
                sb.AppendLine($"  <text x=\"{F(cx)}\" y=\"{F(n.Y + 30)}\" text-anchor=\"middle\" font-size=\"10\" font-style=\"italic\" fill=\"#555\">{Esc(second)}</text>");
        }
        if (diagram.Truncated)
            sb.AppendLine($"  <text x=\"{F(DiagramLayout.Margin)}\" y=\"{F(diagram.Height - 6)}\" font-size=\"10\" fill=\"#a00\">Truncated: not all nodes are shown.</text>");
        sb.AppendLine("</svg>");
        return sb.ToString();

        string Shape(DiagramNode n, double x, double y, string fill, string stroke) => n.Kind switch
        {
            UaNodeKind.Method =>
                $"  <ellipse cx=\"{F(x + n.Width / 2)}\" cy=\"{F(y + n.Height / 2)}\" rx=\"{F(n.Width / 2)}\" ry=\"{F(n.Height / 2)}\" fill=\"{fill}\" stroke=\"{stroke}\"/>",
            UaNodeKind.Variable or UaNodeKind.VariableType =>
                $"  <rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(n.Width)}\" height=\"{F(n.Height)}\" rx=\"12\" fill=\"{fill}\" stroke=\"{stroke}\"/>",
            _ =>
                $"  <rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(n.Width)}\" height=\"{F(n.Height)}\" fill=\"{fill}\" stroke=\"{stroke}\"/>",
        };
    }

    private static string Esc(string s) => SecurityElement.Escape(s) ?? "";
}
