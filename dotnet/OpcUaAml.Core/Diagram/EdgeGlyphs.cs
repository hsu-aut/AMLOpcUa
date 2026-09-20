// The reference notation of OPC 10000-3 Annex C (Table C.2), as small shapes
// at the ends of a line, the same NodeSet.js draws: HasComponent one stroke
// across the line, HasProperty two, HasTypeDefinition two filled heads,
// HasSubtype two hollow heads at the supertype's end, other hierarchical
// references an open head, non-hierarchical ones a filled head, symmetric ones
// a filled head at both ends. SvgWriter and the plugin's diagram draw from
// these shapes.

namespace OpcUaAml.Diagram;

public enum EdgeNotation
{
    HasComponent,
    HasProperty,
    HasSubtype,
    HasTypeDefinition,
    Hierarchical,
    NonHierarchical,
    Symmetric,
}

/// <summary>A shape at a line's end: a closed polygon (filled or hollow) or an open polyline.</summary>
public sealed record EdgeGlyph(IReadOnlyList<(double X, double Y)> Points, bool Closed, bool Filled);

public static class EdgeGlyphs
{
    public static EdgeNotation NotationOf(DiagramEdge edge) => edge.ReferenceType switch
    {
        "HasComponent" => EdgeNotation.HasComponent,
        "HasProperty" => EdgeNotation.HasProperty,
        "HasSubtype" => EdgeNotation.HasSubtype,
        "HasTypeDefinition" => EdgeNotation.HasTypeDefinition,
        _ when edge.Symmetric => EdgeNotation.Symmetric,
        _ => edge.Hierarchical ? EdgeNotation.Hierarchical : EdgeNotation.NonHierarchical,
    };

    /// <summary>HasComponent and HasProperty are told by their strokes; the others carry their name.</summary>
    public static bool Labelled(EdgeNotation notation) => notation is not (EdgeNotation.HasComponent or EdgeNotation.HasProperty);

    /// <summary>
    /// The shapes for a line through <paramref name="points"/> (at least two),
    /// from the source to the target.
    /// </summary>
    public static IReadOnlyList<EdgeGlyph> For(EdgeNotation notation, IReadOnlyList<(double X, double Y)> points)
    {
        var start = points[0];
        var afterStart = points[1];
        var end = points[^1];
        var beforeEnd = points[^2];
        return notation switch
        {
            EdgeNotation.HasComponent => new[] { Stroke(beforeEnd, end, 10) },
            EdgeNotation.HasProperty => new[] { Stroke(beforeEnd, end, 10), Stroke(beforeEnd, end, 15) },
            EdgeNotation.HasTypeDefinition => new[] { Head(beforeEnd, end, 0, true), Head(beforeEnd, end, 9, true) },
            // The double hollow heads point to the source, the supertype.
            EdgeNotation.HasSubtype => new[] { Head(afterStart, start, 0, false), Head(afterStart, start, 9, false) },
            EdgeNotation.Hierarchical => new[] { OpenHead(beforeEnd, end) },
            EdgeNotation.Symmetric => new[] { Head(beforeEnd, end, 0, true), Head(afterStart, start, 0, true) },
            _ => new[] { Head(beforeEnd, end, 0, true) },
        };
    }

    private static (double X, double Y) Unit((double X, double Y) from, (double X, double Y) to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        return length == 0 ? (1, 0) : (dx / length, dy / length);
    }

    /// <summary>A short stroke across the line, <paramref name="distance"/> before its end.</summary>
    private static EdgeGlyph Stroke((double X, double Y) from, (double X, double Y) to, double distance)
    {
        var (ux, uy) = Unit(from, to);
        var cx = to.X - ux * distance;
        var cy = to.Y - uy * distance;
        return new EdgeGlyph(new[] { (cx - uy * 6, cy + ux * 6), (cx + uy * 6, cy - ux * 6) }, Closed: false, Filled: false);
    }

    /// <summary>A closed head pointing at <paramref name="to"/>, set back by <paramref name="offset"/>.</summary>
    private static EdgeGlyph Head((double X, double Y) from, (double X, double Y) to, double offset, bool filled)
    {
        var (ux, uy) = Unit(from, to);
        var tipX = to.X - ux * offset;
        var tipY = to.Y - uy * offset;
        var baseX = tipX - ux * 9;
        var baseY = tipY - uy * 9;
        return new EdgeGlyph(new[] { (tipX, tipY), (baseX - uy * 4.5, baseY + ux * 4.5), (baseX + uy * 4.5, baseY - ux * 4.5) }, Closed: true, Filled: filled);
    }

    private static EdgeGlyph OpenHead((double X, double Y) from, (double X, double Y) to)
    {
        var (ux, uy) = Unit(from, to);
        var baseX = to.X - ux * 10;
        var baseY = to.Y - uy * 10;
        return new EdgeGlyph(new[] { (baseX - uy * 5, baseY + ux * 5), (to.X, to.Y), (baseX + uy * 5, baseY - ux * 5) }, Closed: false, Filled: false);
    }
}
