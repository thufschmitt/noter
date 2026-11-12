using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;
using XPath = Windows.UI.Xaml.Shapes.Path;

namespace Longhand.Services;

/// <summary>
/// A drawing of the model's built as native shapes and text from a <see cref="SvgScene"/>, so Windows draws it itself (Direct2D's own
/// SVG renderer skips <c>&lt;text&gt;</c>, which most drawings are full of). Every shape is a <c>Path</c>; text is a <c>TextBlock</c>.
/// The drawing's own coordinates are kept by a <c>Viewbox</c>, so the caller only sets the size to show it at.
/// </summary>
public static class SvgXaml
{
    /// <summary>The size to show a drawing at on a page <paramref name="pageWidth"/> wide: the width it asks for (never more than the page), as tall as its shape, but at most 480: it is then centred in that.</summary>
    public static (double Width, double Height) DisplaySize(SvgScene scene, double pageWidth, double maxHeight = 480)
    {
        double w = Math.Min(pageWidth, scene.DeclaredWidth ?? pageWidth);
        return (w, Math.Min(maxHeight, w / Math.Max(0.01, scene.Aspect)));
    }

    public static FrameworkElement Build(SvgScene scene, Color ink, FontFamily font)
    {
        var inner = new Canvas { Width = scene.Width, Height = scene.Height, IsHitTestVisible = false };
        var content = new Canvas { RenderTransform = new TranslateTransform { X = -scene.MinX, Y = -scene.MinY } };
        inner.Children.Add(content);
        Add(content, scene.Root.Children, ink, font);
        return new Viewbox { Stretch = Stretch.Uniform, Child = inner, IsHitTestVisible = false };
    }

    static void Add(Canvas into, IEnumerable<SvgNode> nodes, Color ink, FontFamily font)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case SvgGroup group:
                    var canvas = new Canvas { Opacity = group.Opacity };
                    Transform(canvas, group.Transform);
                    Add(canvas, group.Children, ink, font);
                    into.Children.Add(canvas);
                    break;
                case SvgPathNode path:
                    into.Children.Add(Shape(path, ink));
                    break;
                case SvgTextNode text:
                    if (Text(text, ink, font) is { } block) into.Children.Add(block);
                    break;
            }
        }
    }

    static void Transform(UIElement element, Matrix2 m)
    {
        if (!m.IsIdentity) element.RenderTransform = new MatrixTransform { Matrix = new Matrix(m.A, m.B, m.C, m.D, m.E, m.F) };
    }

    /// <summary>The brush for a paint: none is null; the current colour is <paramref name="current"/> when the drawing set one, else the ink.</summary>
    static Brush? MakeBrush(SvgPaint paint, double opacity, SvgPaint? current, Color ink)
    {
        if (paint.Kind == SvgPaintKind.None) return null;
        byte r = ink.R, g = ink.G, b = ink.B;
        double alpha = opacity * ink.A / 255.0;
        var source = paint.Kind == SvgPaintKind.Current ? current : paint;
        if (source is { Kind: SvgPaintKind.Color } color)
        {
            (r, g, b) = (color.R, color.G, color.B);
            alpha = opacity * color.Alpha;
        }
        return new SolidColorBrush(Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), r, g, b));
    }

    static XPath Shape(SvgPathNode node, Color ink)
    {
        var s = node.Style;
        var path = new XPath { Data = Geometry(node.Segments, s.EvenOdd), Opacity = node.Opacity, IsHitTestVisible = false };
        path.Fill = MakeBrush(s.Fill, s.FillOpacity, s.Color, ink);
        if (s.StrokeWidth > 0 && MakeBrush(s.Stroke, s.StrokeOpacity, s.Color, ink) is { } stroke)
        {
            path.Stroke = stroke;
            path.StrokeThickness = s.StrokeWidth;
            var cap = s.LineCap == "round" ? PenLineCap.Round : s.LineCap == "square" ? PenLineCap.Square : PenLineCap.Flat;
            path.StrokeStartLineCap = path.StrokeEndLineCap = path.StrokeDashCap = cap;
            path.StrokeLineJoin = s.LineJoin == "round" ? PenLineJoin.Round : s.LineJoin == "bevel" ? PenLineJoin.Bevel : PenLineJoin.Miter;
            path.StrokeMiterLimit = s.MiterLimit;
            if (s.Dash is { Length: > 0 } dash)
            {
                // XAML measures dashes in stroke widths, SVG in user units; an odd list is repeated, as SVG does.
                var list = dash.Length % 2 == 1 ? dash.Concat(dash).ToArray() : dash;
                var collection = new DoubleCollection();
                foreach (var d in list) collection.Add(d / s.StrokeWidth);
                path.StrokeDashArray = collection;
                path.StrokeDashOffset = s.DashOffset / s.StrokeWidth;
            }
        }
        Transform(path, node.Transform);
        return path;
    }

    static Geometry Geometry(IReadOnlyList<PathSeg> segments, bool evenOdd)
    {
        var geometry = new PathGeometry { FillRule = evenOdd ? FillRule.EvenOdd : FillRule.Nonzero };
        PathFigure? figure = null;
        double x = 0, y = 0;
        foreach (var seg in segments)
        {
            switch (seg.Kind)
            {
                case PathKind.Move:
                    figure = new PathFigure { StartPoint = new Point(seg.X, seg.Y), IsClosed = false, IsFilled = true };
                    geometry.Figures.Add(figure);
                    break;
                case PathKind.Line:
                    figure?.Segments.Add(new LineSegment { Point = new Point(seg.X, seg.Y) });
                    break;
                case PathKind.Cubic:
                    figure?.Segments.Add(new BezierSegment { Point1 = new Point(seg.X1, seg.Y1), Point2 = new Point(seg.X2, seg.Y2), Point3 = new Point(seg.X, seg.Y) });
                    break;
                case PathKind.Quad:
                    figure?.Segments.Add(new QuadraticBezierSegment { Point1 = new Point(seg.X1, seg.Y1), Point2 = new Point(seg.X, seg.Y) });
                    break;
                case PathKind.Arc:
                    // An arc with no radius is a straight line.
                    if (seg.Rx <= 0 || seg.Ry <= 0) figure?.Segments.Add(new LineSegment { Point = new Point(seg.X, seg.Y) });
                    else
                        figure?.Segments.Add(new ArcSegment
                        {
                            Point = new Point(seg.X, seg.Y),
                            Size = new Size(seg.Rx, seg.Ry),
                            RotationAngle = seg.Rotation,
                            IsLargeArc = seg.Large,
                            SweepDirection = seg.Sweep ? SweepDirection.Clockwise : SweepDirection.Counterclockwise,
                        });
                    break;
                case PathKind.Close:
                    if (figure is not null) figure.IsClosed = true;
                    break;
            }
            (x, y) = (seg.X, seg.Y);
        }
        return geometry;
    }

    static FrameworkElement? Text(SvgTextNode node, Color ink, FontFamily font)
    {
        var s = node.Style;
        if (MakeBrush(s.Fill, s.FillOpacity, s.Color, ink) is not { } fill) return null; // fill="none" writes nothing
        var block = new TextBlock
        {
            Text = node.Text,
            FontFamily = font,
            FontSize = s.FontSize,
            FontWeight = s.Bold ? Windows.UI.Text.FontWeights.Bold : Windows.UI.Text.FontWeights.Normal,
            FontStyle = s.Italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
            Foreground = fill,
            Opacity = node.Opacity,
            TextWrapping = TextWrapping.NoWrap,
            IsTextScaleFactorEnabled = false,
            IsHitTestVisible = false,
        };
        // The text's own position is its baseline: the block goes up by the distance to its baseline, and sideways by its anchor.
        block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = block.DesiredSize.Width;
        double dx = s.Anchor == "middle" ? -width / 2 : s.Anchor == "end" ? -width : 0;
        double baseline = block.BaselineOffset > 0 ? block.BaselineOffset : s.FontSize * 0.8;
        Transform(block, node.Transform.Multiply(Matrix2.Translate(node.X + dx, node.Y - baseline)));
        return block;
    }
}
