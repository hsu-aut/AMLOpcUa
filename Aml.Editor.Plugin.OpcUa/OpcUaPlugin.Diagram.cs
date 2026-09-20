// The "Diagram" tab: a UA type or instance in the notation of OPC 10000-3,
// drawn with WPF shapes from the same layout the SVG export uses.

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Aml.Editor.Plugin.Contracts;
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

    private void DiagramSearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter || DiagramSourceBox.Items.Count == 0) return;
        e.Handled = true;
        DiagramSourceBox.SelectedIndex = 0;
    }

    private void DiagramSourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => DiagramShowButton_Click(sender, e);

    private void DiagramShowButton_Click(object sender, RoutedEventArgs e)
    {
        if (DiagramSourceBox.SelectedItem is DiagramSource source) DrawDiagramOf(source.Element);
    }

    private void DrawDiagramOf(SystemUnitClassType element)
    {
        if (ValueOf(DiagramDepthBox) is not { } depth)
        {
            DiagramInfo.Text = Problem(DiagramDepthBox);
            return;
        }
        try
        {
            _diagram = DiagramLayout.Apply(DiagramBuilder.Build(element, depth));
            Draw(_diagram);
            DiagramInfo.Text = $"{element.Name}: {_diagram.Nodes.Count} nodes" + (_diagram.Truncated ? ", truncated" : "")
                               + ". Click a shape to select its element, double click to draw its type. Rules: M Mandatory, O Optional, MP and OP placeholders, E ExposesItsArray.";
        }
        catch (Exception ex)
        {
            PluginLog.Error("Drawing the diagram failed", ex);
            DiagramInfo.Text = "Drawing failed: " + ex.Message;
        }
    }

    private void DiagramFit_Click(object sender, RoutedEventArgs e)
    {
        if (_diagram == null || _diagram.Width <= 0 || _diagram.Height <= 0) return;
        var scale = Math.Min((DiagramScroll.ViewportWidth - 16) / _diagram.Width, (DiagramScroll.ViewportHeight - 16) / _diagram.Height);
        DiagramZoom.Value = Math.Clamp(scale, DiagramZoom.Minimum, 1);
    }

    private void DiagramScroll_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0) return;
        e.Handled = true;
        DiagramZoom.Value = Math.Clamp(DiagramZoom.Value * (e.Delta > 0 ? 1.1 : 1 / 1.1), DiagramZoom.Minimum, DiagramZoom.Maximum);
    }

    /// <summary>The element a diagram node stands for: its ID is the node's, up to "#".</summary>
    private SystemUnitClassType? ElementOf(DiagramNode node)
    {
        var id = node.Id.Split('#')[0];
        return _document?.FindByID(id, true, null) as SystemUnitClassType;
    }

    private void DiagramNode_Click(DiagramNode node, int clicks)
    {
        if (ElementOf(node) is not { } element) return;
        if (clicks < 2)
        {
            Selected?.Invoke(this, new SelectionEventArgs(element));
            DiagramInfo.Text = $"Selected {element.Name} in the editor.";
            return;
        }
        // Double click: the node's type (a declaration's or instance's class, a type's supertype).
        var path = element is SystemUnitFamilyType family ? family.RefBaseClassPath : (element as InternalElementType)?.RefBaseSystemUnitPath;
        if (!string.IsNullOrEmpty(path) && _document?.FindByPath(path) is SystemUnitFamilyType type) DrawDiagramOf(type);
        else DiagramInfo.Text = $"{element.Name} has no UA type in this document.";
    }

    private void DiagramExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_diagram == null) return;
        var dialog = new SaveFileDialog { Filter = "SVG (*.svg)|*.svg", FileName = _diagram.Title + ".svg" };
        if (dialog.ShowDialog() != true) return;
        var diagram = _diagram;
        Guard("Saving the SVG", () =>
        {
            File.WriteAllText(dialog.FileName, SvgWriter.Write(diagram));
            PluginLog.Info($"Diagram written to {dialog.FileName}");
            DiagramInfo.Text = $"Saved as {System.IO.Path.GetFileName(dialog.FileName)}.";
        });
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
            // The notation of OPC 10000-3 Annex C, as NodeSet.js and the SVG export draw it.
            (double X, double Y)[] points;
            double lx, ly;
            if (edge.Hierarchical)
            {
                double x1 = a.X + a.Width, y1 = a.Y + a.Height / 2, x2 = b.X, y2 = b.Y + b.Height / 2, mx = (x1 + x2) / 2;
                points = new[] { (x1, y1), (mx, y1), (mx, y2), (x2, y2) };
                (lx, ly) = (mx + 3, y2 - 14);
            }
            else
            {
                double x1 = a.X + a.Width / 2, y1 = a.Y + a.Height, x2 = b.X + b.Width / 2, y2 = b.Y;
                points = new[] { (x1, y1), (x2, y2) };
                (lx, ly) = ((x1 + x2) / 2 + 4, (y1 + y2) / 2 - 7);
            }
            canvas.Children.Add(new Polyline
            {
                Stroke = stroke,
                StrokeThickness = 1,
                Points = new PointCollection(points.Select(p => new Point(p.X, p.Y))),
            });
            var notation = EdgeGlyphs.NotationOf(edge);
            foreach (var glyph in EdgeGlyphs.For(notation, points))
            {
                var glyphPoints = new PointCollection(glyph.Points.Select(p => new Point(p.X, p.Y)));
                canvas.Children.Add(glyph.Closed
                    ? new Polygon { Points = glyphPoints, Fill = glyph.Filled ? stroke : Brushes.White, Stroke = stroke, StrokeThickness = 1 }
                    : new Polyline { Points = glyphPoints, Stroke = stroke, StrokeThickness = 1.2 });
            }
            if (EdgeGlyphs.Labelled(notation)) Label(canvas, edge.ReferenceType, lx, ly, 9, grey, italic: true);
        }

        foreach (var n in d.Nodes)
        {
            var isType = n.Kind is UaNodeKind.ObjectType or UaNodeKind.VariableType;
            if (isType) Place(canvas, Shape(n, new SolidColorBrush(Color.FromRgb(0x9a, 0xa8, 0xb8)), null), n.X + 4, n.Y + 4);
            var fill = isType ? new SolidColorBrush(Color.FromRgb(0xdd, 0xe6, 0xf0)) : Brushes.White;
            var shape = Shape(n, fill, Brushes.Black);
            shape.ToolTip = n.Name + (n.TypeName != null ? " : " + n.TypeName : "") + "\nClick: select in the editor. Double click: draw its type.";
            shape.Cursor = System.Windows.Input.Cursors.Hand;
            var hover = new SolidColorBrush(Color.FromRgb(0x20, 0x70, 0xC0));
            shape.MouseEnter += (_, __) => { shape.Stroke = hover; shape.StrokeThickness = 2; };
            shape.MouseLeave += (_, __) => { shape.Stroke = Brushes.Black; shape.StrokeThickness = 1; };
            var node = n;
            shape.MouseLeftButtonDown += (_, e) => { e.Handled = true; DiagramNode_Click(node, e.ClickCount); };
            Place(canvas, shape, n.X, n.Y);

            var second = n.TypeName != null ? ":" + n.TypeName : n.SupertypeName != null ? "subtype of " + n.SupertypeName : null;
            var panel = new StackPanel { Width = n.Width, IsHitTestVisible = false };
            panel.Children.Add(new TextBlock
            {
                Text = n.Name + (n.ModellingRule != null ? $" ({n.ModellingRule})" : ""),
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                Foreground = Brushes.Black,
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

    private static void Label(Canvas canvas, string text, double x, double y, double size, Brush brush, bool italic = false) =>
        Place(canvas, new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = brush,
            FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
            IsHitTestVisible = false,
        }, x, y);
}
