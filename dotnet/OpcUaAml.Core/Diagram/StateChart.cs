// A state machine as a picture: the states on a circle, the transitions as
// arrows between them, a transition back to its own state as a small loop.
// The same drawing the modeler shows while a machine is built, so the
// documentation and the modeler tell the same story.

using System.Globalization;
using System.Text;
using OpcUaAml.Types;

namespace OpcUaAml.Diagram;

public static class StateChart
{
    /// <summary>The machine as SVG, or an empty string when it has no state.</summary>
    public static string Svg(StateMachine machine, int size = 460)
    {
        if (machine.States.Count == 0) return "";
        var radius = size / 2.0 - 70;
        var centre = size / 2.0;
        const double width = 104, height = 32;

        var places = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
        for (var i = 0; i < machine.States.Count; i++)
        {
            var angle = 2 * Math.PI * i / machine.States.Count - Math.PI / 2;
            var key = machine.States[i].NodeId ?? machine.States[i].Name;
            places[key] = (centre + radius * Math.Cos(angle), centre + radius * Math.Sin(angle));
        }

        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{size}\" height=\"{size}\" viewBox=\"0 0 {size} {size}\">");
        svg.Append("<defs><marker id=\"sm-arrow\" viewBox=\"0 0 10 10\" refX=\"9\" refY=\"5\" markerWidth=\"7\" markerHeight=\"7\" orient=\"auto-start-reverse\">")
           .Append("<path d=\"M 0 0 L 10 5 L 0 10 z\" fill=\"#57606a\"/></marker></defs>");

        foreach (var transition in machine.Transitions)
        {
            if (transition.From is null || transition.To is null) continue;
            if (!places.TryGetValue(transition.From, out var from) || !places.TryGetValue(transition.To, out var to)) continue;
            var label = transition.Cause is { Length: > 0 } cause ? $"{transition.Name} / {cause}()" : transition.Name;

            if (transition.From == transition.To)
            {
                var loop = $"M {N(from.X - 18)} {N(from.Y - height / 2)} A 22 22 0 1 1 {N(from.X + 18)} {N(from.Y - height / 2)}";
                svg.Append(CultureInfo.InvariantCulture, $"<path d=\"{loop}\" fill=\"none\" stroke=\"#57606a\" marker-end=\"url(#sm-arrow)\"/>");
                Label(svg, from.X, from.Y - height / 2 - 30, label);
                continue;
            }

            var a = Edge(from, to, width, height);
            var b = Edge(to, from, width, height);
            svg.Append(CultureInfo.InvariantCulture,
                $"<line x1=\"{N(a.X)}\" y1=\"{N(a.Y)}\" x2=\"{N(b.X)}\" y2=\"{N(b.Y)}\" stroke=\"#57606a\" marker-end=\"url(#sm-arrow)\"/>");
            Label(svg, (a.X + b.X) / 2, (a.Y + b.Y) / 2 - 4, label);
        }

        foreach (var state in machine.States)
        {
            var p = places[state.NodeId ?? state.Name];
            svg.Append(CultureInfo.InvariantCulture,
                $"<rect x=\"{N(p.X - width / 2)}\" y=\"{N(p.Y - height / 2)}\" width=\"{N(width)}\" height=\"{N(height)}\" rx=\"15\" fill=\"#ffffff\" stroke=\"#1f2328\"/>");
            svg.Append(CultureInfo.InvariantCulture,
                $"<text x=\"{N(p.X)}\" y=\"{N(p.Y + 4)}\" text-anchor=\"middle\" font-family=\"Segoe UI, Arial\" font-size=\"12\">{Escape(state.Name)}</text>");
        }

        svg.Append("</svg>");
        return svg.ToString();
    }

    /// <summary>Where a line from one state to another leaves the first one's box.</summary>
    private static (double X, double Y) Edge((double X, double Y) from, (double X, double Y) to, double width, double height)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var scale = Math.Min(
            dx == 0 ? double.MaxValue : width / 2 / Math.Abs(dx),
            dy == 0 ? double.MaxValue : height / 2 / Math.Abs(dy));
        return (from.X + dx * scale, from.Y + dy * scale);
    }

    /// <summary>A name on its own sheet, so a line underneath does not cross it.</summary>
    private static void Label(StringBuilder svg, double x, double y, string text)
    {
        var width = text.Length * 5.7 + 6;
        svg.Append(CultureInfo.InvariantCulture,
            $"<rect x=\"{N(x - width / 2)}\" y=\"{N(y - 9)}\" width=\"{N(width)}\" height=\"12\" fill=\"#ffffff\" fill-opacity=\"0.85\"/>");
        svg.Append(CultureInfo.InvariantCulture,
            $"<text x=\"{N(x)}\" y=\"{N(y)}\" text-anchor=\"middle\" font-family=\"Segoe UI, Arial\" font-size=\"11\" fill=\"#57606a\">{Escape(text)}</text>");
    }

    private static string N(double value) => Math.Round(value, 1).ToString(CultureInfo.InvariantCulture);

    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
