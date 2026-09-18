// The "Diagram" tab: a UA type or instance in the notation of OPC 10000-3,
// drawn with WPF shapes from the same layout the SVG export uses.

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Aml.Editor.Plugin.OpcUa.Diagnostics;
using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using Microsoft.Win32;
using OpcUaAml.Diagram;
using OpcUaAml.Types;

namespace Aml.Editor.Plugin.OpcUa;

public partial class OpcUaPlugin
{
    private UaDiagram? _diagram;

    /// <summary>An entry of the diagram source picker: a type or an instance.</summary>
    private sealed record DiagramSource(string Label, SystemUnitClassType Element)
    {
        public override string ToString() => Label;
    }

    private void RefreshDiagramSources()
    {
        var doc = _document;
        if (doc == null) { DiagramSourceBox.ItemsSource = null; return; }
        var text = DiagramSearchBox.Text.Trim();
        var types = UaTypes.AllTypes(doc)
            .Where(t => !t.Path.StartsWith("SUC_OpcAmlMetaModel", StringComparison.Ordinal))
            .Select(t => new DiagramSource($"type  {t.Type.Name}", t.Type));
        var instances = doc.CAEXFile.InstanceHierarchy
            .SelectMany(ih => ih.Descendants<InternalElementType>())
            .Where(ie => UaTypes.IsUaLibraryPath(ie.RefBaseSystemUnitPath))
            .Select(ie => new DiagramSource($"instance  {ie.Name}", ie));
        DiagramSourceBox.ItemsSource = instances.Concat(types)
            .Where(s => text.Length == 0 || s.Label.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Take(500).ToList();
    }

    private void DiagramSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshDiagramSources();

    private void DiagramShowButton_Click(object sender, RoutedEventArgs e)
    {
        if (DiagramSourceBox.SelectedItem is not DiagramSource source) return;
        int.TryParse(DiagramDepthBox.Text, out var depth);
        try
        {
            _diagram = DiagramLayout.Apply(DiagramBuilder.Build(source.Element, Math.Clamp(depth, 1, 10)));
            Draw(_diagram);
            DiagramInfo.Text = $"{_diagram.Nodes.Count} nodes" + (_diagram.Truncated ? ", truncated" : "");
        }
        catch (Exception ex)
        {
            PluginLog.Error("Drawing the diagram failed", ex);
            DiagramInfo.Text = "Drawing failed: " + ex.Message;
        }
    }

    private void DiagramExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_diagram == null) return;
        var dialog = new SaveFileDialog { Filter = "SVG (*.svg)|*.svg", FileName = _diagram.Title + ".svg" };
        if (dialog.ShowDialog() != true) return;
        File.WriteAllText(dialog.FileName, SvgWriter.Write(_diagram));
        PluginLog.Info($"Diagram written to {dialog.FileName}");
    }

    private void DiagramZoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DiagramCanvas != null) DiagramCanvas.LayoutTransform = new ScaleTransform(e.NewValue, e.NewValue);
    }

    private void Draw(UaDiagram d)
    {
        var canvas = DiagramCanvas;
        canvas.Children.Clear();
        canvas.Width = d.Width;
        canvas.Height = d.Height;
        var byId = d.Nodes.ToDictionary(n => n.Id);
        var stroke = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));
        var grey = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));

        foreach (var edge in d.Edges)
        {
            if (!byId.TryGetValue(edge.From, out var a) || !byId.TryGetValue(edge.To, out var b)) continue;
            var line = new Polyline { Stroke = stroke, StrokeThickness = 1 };
            if (edge.Hierarchical)
            {
                double x1 = a.X + a.Width, y1 = a.Y + a.Height / 2, x2 = b.X, y2 = b.Y + b.Height / 2, mx = (x1 + x2) / 2;
                line.Points = new PointCollection { new(x1, y1), new(mx, y1), new(mx, y2), new(x2, y2) };
                Arrow(canvas, x2, y2, 1, 0, stroke);
                Label(canvas, edge.ReferenceType, mx + 3, y2 - 14, 9, grey);
            }
            else
            {
                double x1 = a.X + a.Width / 2, y1 = a.Y + a.Height, x2 = b.X + b.Width / 2, y2 = b.Y;
                line.Points = new PointCollection { new(x1, y1), new(x2, y2) };
                line.StrokeDashArray = new DoubleCollection { 4, 3 };
                var len = Math.Max(1, Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1)));
                Arrow(canvas, x2, y2, (x2 - x1) / len, (y2 - y1) / len, stroke);
                Label(canvas, edge.ReferenceType, (x1 + x2) / 2 + 4, (y1 + y2) / 2 - 7, 9, grey);
            }
            canvas.Children.Add(line);
        }

        foreach (var n in d.Nodes)
        {
            var isType = n.Kind is UaNodeKind.ObjectType or UaNodeKind.VariableType;
            if (isType) Place(canvas, Shape(n, new SolidColorBrush(Color.FromRgb(0x9a, 0xa8, 0xb8)), null), n.X + 4, n.Y + 4);
            var fill = isType ? new SolidColorBrush(Color.FromRgb(0xdd, 0xe6, 0xf0)) : Brushes.White;
            var shape = Shape(n, fill, Brushes.Black);
            shape.ToolTip = n.Name + (n.TypeName != null ? " : " + n.TypeName : "");
            Place(canvas, shape, n.X, n.Y);

            var second = n.TypeName != null ? ":" + n.TypeName : n.SupertypeName != null ? "subtype of " + n.SupertypeName : null;
            var panel = new StackPanel { Width = n.Width, IsHitTestVisible = false };
            panel.Children.Add(new TextBlock
            {
                Text = n.Name + (n.ModellingRule != null ? $" ({n.ModellingRule})" : ""),
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
            });
            if (second != null)
                panel.Children.Add(new TextBlock { Text = second, FontSize = 10, FontStyle = FontStyles.Italic, Foreground = grey, TextAlignment = TextAlignment.Center });
            Place(canvas, panel, n.X, n.Y + (second != null ? 3 : 10));
        }
    }

    private static Shape Shape(DiagramNode n, Brush fill, Brush? stroke) => n.Kind switch
    {
        UaNodeKind.Method => new Ellipse { Width = n.Width, Height = n.Height, Fill = fill, Stroke = stroke },
        UaNodeKind.Variable or UaNodeKind.VariableType =>
            new Rectangle { Width = n.Width, Height = n.Height, RadiusX = 12, RadiusY = 12, Fill = fill, Stroke = stroke },
        _ => new Rectangle { Width = n.Width, Height = n.Height, Fill = fill, Stroke = stroke },
    };

    private static void Place(Canvas canvas, UIElement element, double x, double y)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        canvas.Children.Add(element);
    }

    private static void Label(Canvas canvas, string text, double x, double y, double size, Brush brush) =>
        Place(canvas, new TextBlock { Text = text, FontSize = size, Foreground = brush, IsHitTestVisible = false }, x, y);

    private static void Arrow(Canvas canvas, double x, double y, double dx, double dy, Brush brush)
    {
        const double length = 7, width = 3.5;
        var bx = x - dx * length;
        var by = y - dy * length;
        canvas.Children.Add(new Polygon
        {
            Fill = brush,
            Points = new PointCollection { new(x, y), new(bx - dy * width, by + dx * width), new(bx + dy * width, by - dx * width) },
        });
    }
}
