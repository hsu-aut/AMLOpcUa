// A frame around the control a tutorial step is about, drawn over it in the
// adorner layer, so the control itself is not changed and stays usable.

using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Aml.Editor.Plugin.OpcUa.Guide;

internal sealed class HighlightAdorner : Adorner
{
    private static readonly Pen Frame = CreatePen(Color.FromRgb(0xF0, 0x9A, 0x40), 2.5);
    private static readonly Pen Halo = CreatePen(Color.FromArgb(0x55, 0xF0, 0x9A, 0x40), 7);

    public HighlightAdorner(UIElement target) : base(target) => IsHitTestVisible = false;

    protected override void OnRender(DrawingContext dc)
    {
        var box = new Rect(new Point(-3, -3), new Size(AdornedElement.RenderSize.Width + 6, AdornedElement.RenderSize.Height + 6));
        dc.DrawRoundedRectangle(null, Halo, box, 5, 5);
        dc.DrawRoundedRectangle(null, Frame, box, 5, 5);
    }

    private static Pen CreatePen(Color color, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness);
        pen.Freeze();
        return pen;
    }
}
