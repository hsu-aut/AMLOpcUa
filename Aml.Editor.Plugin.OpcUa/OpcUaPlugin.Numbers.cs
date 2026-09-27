// Number fields (depth, node limit, diagram depth, port): a value outside
// its range is marked in the field at once and named when a command needs
// it, instead of being replaced silently by something else.

using System.Globalization;
using System.Windows.Controls;
using Aml.Editor.Plugin.OpcUa.Localization;

namespace Aml.Editor.Plugin.OpcUa;

public partial class OpcUaPlugin
{
    private readonly Dictionary<TextBox, (int Min, int Max, Func<int, int, string> Problem, object? Tip)> _numbers = new();

    private void InitNumberFields()
    {
        // One whole sentence per field, with the range put in, so each can be translated as a sentence.
        Number(MirrorDepthBox, 0, 50, (min, max) => L.T("Enter a whole number from {0} to {1} as depth.", min, max));
        Number(MirrorMaxNodesBox, 1, 1_000_000, (min, max) => L.T("Enter a whole number from {0} to {1} as node limit.", min, max));
        Number(DiagramDepthBox, 1, 10, (min, max) => L.T("Enter a whole number from {0} to {1} as diagram depth.", min, max));
        Number(ServePortBox, 1, 65535, (min, max) => L.T("Enter a whole number from {0} to {1} as port.", min, max));
    }

    private void Number(TextBox box, int min, int max, Func<int, int, string> problem)
    {
        _numbers[box] = (min, max, problem, box.ToolTip);
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
        var (min, max, problem, _) = _numbers[box];
        return problem(min, max);
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
