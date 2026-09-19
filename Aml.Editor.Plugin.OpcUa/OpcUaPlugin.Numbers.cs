// Number fields (depth, node limit, diagram depth, port): a value outside
// its range is marked in the field at once and named when a command needs
// it, instead of being replaced silently by something else.

using System.Globalization;
using System.Windows.Controls;

namespace Aml.Editor.Plugin.OpcUa;

public partial class OpcUaPlugin
{
    private readonly Dictionary<TextBox, (int Min, int Max, string What, object? Tip)> _numbers = new();

    private void InitNumberFields()
    {
        Number(MirrorDepthBox, 0, 50, "depth");
        Number(MirrorMaxNodesBox, 1, 1_000_000, "node limit");
        Number(DiagramDepthBox, 1, 10, "diagram depth");
        Number(ServePortBox, 1, 65535, "port");
    }

    private void Number(TextBox box, int min, int max, string what)
    {
        _numbers[box] = (min, max, what, box.ToolTip);
        box.TextChanged += (_, __) => Mark(box);
        Mark(box);
    }

    private void Mark(TextBox box)
    {
        var (_, _, _, tip) = _numbers[box];
        if (ValueOf(box) != null)
        {
            box.ClearValue(Control.BorderBrushProperty);
            box.ClearValue(Control.BorderThicknessProperty);
            box.ToolTip = tip;
            return;
        }
        box.BorderBrush = DialogKit.Danger;
        box.BorderThickness = new System.Windows.Thickness(1.5);
        box.ToolTip = Problem(box);
    }

    /// <summary>The field's number, or null when it is not a whole number in its range.</summary>
    private int? ValueOf(TextBox box)
    {
        var (min, max, _, _) = _numbers[box];
        return int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max ? n : null;
    }

    private string Problem(TextBox box)
    {
        var (min, max, what, _) = _numbers[box];
        return $"Enter a whole number from {min} to {max} as {what}.";
    }

    /// <summary>True when every field is fine; else the first problem goes to the status line.</summary>
    private bool NumbersValid(params TextBox[] boxes)
    {
        foreach (var box in boxes)
        {
            if (ValueOf(box) != null) continue;
            SetStatus(Problem(box));
            box.Focus();
            return false;
        }
        return true;
    }
}
