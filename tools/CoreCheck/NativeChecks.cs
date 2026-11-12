using System;
using System.Collections.Generic;
using System.Linq;
using Longhand.Services;

/// <summary>Checks for what the Windows app builds its native drawings from: SVG read into a scene, and the arithmetic of dropping on a layout.</summary>
static class NativeChecks
{
    static bool Near(double a, double b, double eps = 1e-6) => Math.Abs(a - b) < eps;
    static bool Near((double X, double Y) p, double x, double y, double eps = 1e-6) => Near(p.X, x, eps) && Near(p.Y, y, eps);

    public static void Run(Action<string, bool> check)
    {
        // ----- transforms -----
        var t = Matrix2.Parse("translate(10 20) scale(2)");
        check("a transform list multiplies left to right: the last is applied to the shape first", Near(t.Apply(1, 1), 12, 22));
        check("translate with one number, scale with one, matrix", Near(Matrix2.Parse("translate(5)").Apply(0, 0), 5, 0) && Near(Matrix2.Parse("scale(3)").Apply(1, 2), 3, 6) && Near(Matrix2.Parse("matrix(1 0 0 1 7 8)").Apply(1, 1), 8, 9));
        check("rotate turns clockwise on the page (y down), about a point when it is given", Near(Matrix2.Parse("rotate(90)").Apply(1, 0), 0, 1) && Near(Matrix2.Parse("rotate(90 10 10)").Apply(20, 10), 10, 20) && Near(Matrix2.Parse("rotate(180 5 5)").Apply(5, 5), 5, 5));
        check("skew, commas, and something it can't read counts for nothing", Near(Matrix2.Parse("skewX(45)").Apply(0, 1), 1, 1) && Near(Matrix2.Parse("translate(1,2),scale(2,3)").Apply(1, 1), 3, 5) && Matrix2.Parse("wobble(3)").IsIdentity && Matrix2.Parse(null).IsIdentity && Matrix2.Parse("matrix(1 2)").IsIdentity);

        // ----- path data -----
        var p = SvgPath.Parse("M10 10 L20 10 l0 10 H5 v-5 z");
        check("absolute and relative lines, horizontal and vertical, close", p.Select(s => s.Kind).SequenceEqual(new[] { PathKind.Move, PathKind.Line, PathKind.Line, PathKind.Line, PathKind.Line, PathKind.Close })
            && p[1].X == 20 && p[2].X == 20 && p[2].Y == 20 && p[3].X == 5 && p[3].Y == 20 && p[4].X == 5 && p[4].Y == 15);
        var run = SvgPath.Parse("M10-5.5.5.5l1-1");
        check("numbers run into each other: a sign or a second point starts the next", run.Count == 3 && run[0].X == 10 && run[0].Y == -5.5 && run[1].Kind == PathKind.Line && run[1].X == 0.5 && run[1].Y == 0.5 && run[2].X == 1.5 && run[2].Y == -0.5);
        var implicitLine = SvgPath.Parse("M0 0 10 0 10 10");
        check("pairs after a moveto are lines", implicitLine.Count == 3 && implicitLine[1].Kind == PathKind.Line && implicitLine[2].Y == 10 && SvgPath.Parse("m1 1 2 2")[1].X == 3);
        var cubic = SvgPath.Parse("M0 0 C10 0 20 10 20 20 S30 40 40 40");
        check("a cubic, and a smooth one reflects the last control point", cubic[1].Kind == PathKind.Cubic && cubic[1].X1 == 10 && cubic[1].X2 == 20 && cubic[2].X1 == 20 && cubic[2].Y1 == 30 && cubic[2].X2 == 30 && cubic[2].X == 40);
        var quad = SvgPath.Parse("M0 0 Q10 20 20 0 T40 0");
        check("a quadratic, and a smooth one reflects it", quad[1].Kind == PathKind.Quad && quad[1].X1 == 10 && quad[2].X1 == 30 && quad[2].Y1 == -20 && quad[2].X == 40);
        var arc = SvgPath.Parse("M0 0 a5 5 0 011 1");
        check("an arc keeps its radii, turn and flags, even when they are written without separators", arc[1].Kind == PathKind.Arc && arc[1].Rx == 5 && arc[1].Ry == 5 && !arc[1].Large && arc[1].Sweep && arc[1].X == 1 && arc[1].Y == 1);
        check("arc flags and relative arcs", SvgPath.Parse("M10 10 A4 2 30 1 0 20 10")[1] is { Large: true, Sweep: false, Rotation: 30, Rx: 4, Ry: 2, X: 20 } && SvgPath.Parse("M10 10 a4 2 0 0 1 5 5")[1] is { X: 15, Y: 15 });
        var afterClose = SvgPath.Parse("M0 0 L10 0 L10 10 z L5 5");
        check("drawing after a close starts again from where the path began", afterClose.Select(s => s.Kind).SequenceEqual(new[] { PathKind.Move, PathKind.Line, PathKind.Line, PathKind.Close, PathKind.Move, PathKind.Line }) && afterClose[4].X == 0 && afterClose[4].Y == 0);
        check("what can't be read ends the path, keeping what came before; it must start with a moveto", SvgPath.Parse("M0 0 L5 5 L oops 9").Count == 2 && SvgPath.Parse("L5 5").Count == 0 && SvgPath.Parse("").Count == 0 && SvgPath.Parse(null).Count == 0 && SvgPath.Parse("M0 0 X5 5").Count == 1);
        check("exponents", SvgPath.Parse("M1e1 2E+1")[0] is { X: 10, Y: 20 });

        // ----- basic shapes as paths -----
        check("a rectangle is four lines and a close; a rounded one has arcs, with radii that fit", SvgPath.Rect(1, 2, 10, 4, 0, 0).Count == 5 && SvgPath.Rect(1, 2, 10, 4, 2, 0).Count(s => s.Kind == PathKind.Arc) == 4
            && SvgPath.Rect(0, 0, 10, 4, 50, 50).First(s => s.Kind == PathKind.Arc) is { Rx: 5, Ry: 2 } && SvgPath.Rect(0, 0, 0, 4, 0, 0).Count == 0 && SvgPath.Rect(0, 0, 4, -1, 0, 0).Count == 0);
        var circle = SvgPath.Ellipse(10, 10, 5, 5);
        check("a circle is two half arcs", circle.Count == 4 && circle[0].X == 15 && circle[1].X == 5 && circle[2].X == 15 && circle[1].Rx == 5 && SvgPath.Ellipse(0, 0, 0, 3).Count == 0);
        check("a polyline is open, a polygon closes, odd numbers are dropped", SvgPath.Poly("0,0 10,0 10,10", false).Count == 3 && SvgPath.Poly("0,0 10,0 10,10", true).Last().Kind == PathKind.Close && SvgPath.Poly("0 0 5", false).Count == 1);

        // ----- paints -----
        check("none, currentColor, names, hex", SvgPaint.Parse("none") == SvgPaint.None && SvgPaint.Parse("currentColor") == SvgPaint.Current && SvgPaint.Parse("Red") == new SvgPaint(SvgPaintKind.Color, 255, 0, 0)
            && SvgPaint.Parse("#0f0") == new SvgPaint(SvgPaintKind.Color, 0, 255, 0) && SvgPaint.Parse("#C62828") == new SvgPaint(SvgPaintKind.Color, 0xC6, 0x28, 0x28) && SvgPaint.Parse("transparent") == SvgPaint.None);
        check("rgb and rgba, with percentages", SvgPaint.Parse("rgb(10, 20, 30)") == new SvgPaint(SvgPaintKind.Color, 10, 20, 30) && SvgPaint.Parse("rgba(255,0,0,0.5)") == new SvgPaint(SvgPaintKind.Color, 255, 0, 0, 0.5) && SvgPaint.Parse("rgb(100%, 0%, 50%)") == new SvgPaint(SvgPaintKind.Color, 255, 0, 128));
        check("what isn't a colour is null, so the inherited one stays", SvgPaint.Parse("blurple") is null && SvgPaint.Parse("#12") is null && SvgPaint.Parse("") is null && SvgPaint.Parse(null) is null);

        // ----- a drawing read into a scene -----
        var houseMarkup = ("<svg viewBox=\"0 0 400 200\"><title>A house</title><g transform=\"translate(10 20)\" opacity=\"0.5\" stroke=\"currentColor\" stroke-width=\"3\" fill=\"none\" stroke-linecap=\"round\" stroke-linejoin=\"round\">"
            + "<path d=\"M50 100 L200 20 L350 100 V180 H50 Z\"/><circle cx=\"200\" cy=\"100\" r=\"10\" fill=\"#C62828\" stroke-dasharray=\"4 2\"/></g>"
            + "<rect x=\"1\" y=\"2\" width=\"30\" height=\"10\" rx=\"3\" style=\"fill:blue;stroke:none\"/><line x1=\"0\" y1=\"0\" x2=\"5\" y2=\"5\" stroke=\"red\"/>"
            + "<text x=\"150\" y=\"150\" font-size=\"20\" fill=\"currentColor\" text-anchor=\"middle\" font-weight=\"bold\">home</text></svg>");
        var scene = SvgScene.Parse(houseMarkup)!;
        var viaSanitizer = SvgScene.Parse(Svg.Sanitize(houseMarkup)!)!;
        check("after the sanitizer the same drawing is read, with its attributes (only a style declaration is lost)", viaSanitizer.Root.Children.Count == 4 && viaSanitizer.Width == 400 && ((SvgGroup)viaSanitizer.Root.Children[0]).Children.Count == 2 && viaSanitizer.Root.Children.OfType<SvgTextNode>().Single().Text == "home");
        check("the viewBox is the drawing's own size, and sets its shape", scene.MinX == 0 && scene.Width == 400 && scene.Height == 200 && Near(scene.Aspect, 2) && scene.DeclaredWidth is null);
        var group = (SvgGroup)scene.Root.Children[0];
        check("a group keeps its transform and opacity, and its children inherit its style", Near(group.Transform.Apply(0, 0), 10, 20) && group.Opacity == 0.5 && group.Children.Count == 2
            && group.Children[0] is SvgPathNode { Style: { Stroke.Kind: SvgPaintKind.Current, StrokeWidth: 3, LineCap: "round", LineJoin: "round" } } roof && roof.Style.Fill == SvgPaint.None && roof.Segments.Count == 6);
        var dot = (SvgPathNode)group.Children[1];
        check("a shape's own attributes win over what it inherits, and a dash is read", dot.Style.Fill == new SvgPaint(SvgPaintKind.Color, 0xC6, 0x28, 0x28) && dot.Style.Stroke.Kind == SvgPaintKind.Current && dot.Style.Dash!.SequenceEqual(new[] { 4.0, 2.0 }) && dot.Segments.Count == 4);
        var rounded = (SvgPathNode)scene.Root.Children[1];
        check("style declarations count as attributes, and a rectangle becomes a path", rounded.Style.Fill == new SvgPaint(SvgPaintKind.Color, 0, 0, 255) && rounded.Style.Stroke == SvgPaint.None && rounded.Segments.Count(s => s.Kind == PathKind.Arc) == 4);
        var line = (SvgPathNode)scene.Root.Children[2];
        check("a line has its stroke; the default fill is black", line.Style.Stroke == new SvgPaint(SvgPaintKind.Color, 255, 0, 0) && line.Style.Fill == SvgPaint.Black && line.Segments.Count == 2);
        var label = (SvgTextNode)scene.Root.Children[3];
        check("text has its anchor, size, weight and where its baseline starts", label.Text == "home" && label.X == 150 && label.Y == 150 && label.Style is { Anchor: "middle", FontSize: 20, Bold: true } && label.Style.Fill.Kind == SvgPaintKind.Current);
        check("the title isn't drawn", scene.Root.Children.Count == 4);

        var many = SvgScene.Parse("<svg viewBox=\"0 0 100 50\"><text x=\"10\" y=\"20\">first <tspan>line</tspan> <tspan x=\"10\" dy=\"24\">second</tspan><tspan x=\"10\" dy=\"24\" >  third   one </tspan></text><text x=\"1\" y=\"2\">   </text></svg>")!;
        var lines = many.Root.Children.OfType<SvgTextNode>().ToList();
        check("tspans that set a position start a new line, the others run on, spaces are one", lines.Count == 3 && lines[0].Text == "first line" && lines[1].Text == "second" && lines[1].X == 10 && lines[1].Y == 44 && lines[2].Text == "third one" && lines[2].Y == 68);
        check("an empty text is nothing", many.Root.Children.OfType<SvgTextNode>().All(n => n.Text.Length > 0));

        var gradient = SvgScene.Parse("<svg viewBox=\"0 0 10 10\"><defs><linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"#ff0000\"/><stop offset=\"1\" stop-color=\"#0000ff\"/></linearGradient></defs><rect width=\"10\" height=\"10\" fill=\"url(#g)\"/><rect width=\"5\" height=\"5\" fill=\"url(#nothing)\" stroke=\"black\" display=\"none\"/><circle r=\"3\" cx=\"5\" cy=\"5\" visibility=\"hidden\"/></svg>")!;
        check("what isn't drawn in place (definitions) isn't, a gradient is its first colour, hidden things are left out", gradient.Root.Children.Count == 1 && ((SvgPathNode)gradient.Root.Children[0]).Style.Fill == new SvgPaint(SvgPaintKind.Color, 255, 0, 0));
        var sized = SvgScene.Parse(Svg.Sanitize("<svg width=\"400px\" height=\"300\"><rect width=\"1\" height=\"1\"/></svg>")!)!;
        check("with no viewBox the declared size is the drawing's, and the width it asks to be shown at is kept", sized.Width == 400 && sized.Height == 300 && sized.DeclaredWidth == 400 && Near(sized.Aspect, 4.0 / 3));
        check("with neither it is 300 by 150, and nothing is nothing", SvgScene.Parse("<svg><rect width=\"1\" height=\"1\"/></svg>") is { Width: 300, Height: 150 } && SvgScene.Parse("<div>x</div>") is null && SvgScene.Parse("") is null && SvgScene.Parse(null) is null);
        var nested = SvgScene.Parse("<svg viewBox=\"0 0 10 10\" fill=\"red\"><g><g><g><path d=\"M0 0 L1 1\" stroke=\"blue\"/></g></g></g></svg>")!;
        check("fill is inherited from the root through groups", ((SvgPathNode)((SvgGroup)((SvgGroup)((SvgGroup)nested.Root.Children[0]).Children[0]).Children[0]).Children[0]).Style.Fill == new SvgPaint(SvgPaintKind.Color, 255, 0, 0));

        // ----- the colours of a layout -----
        check("a palette name has a light and a dark value, a hex colour is as it is, anything else none", Layout.ResolveColor("red", false) is (0xC6, 0x28, 0x28) && Layout.ResolveColor("red", true) is (0xFF, 0x8A, 0x80) && Layout.ResolveColor("#1e90ff", true) is (0x1E, 0x90, 0xFF)
            && Layout.ResolveColor("#abc", false) is (0xAA, 0xBB, 0xCC) && Layout.ResolveColor("mauve", false) is null && Layout.ResolveColor(null, false) is null && Layout.ResolveColor("", true) is null);

        // ----- dropping on a layout: the arithmetic -----
        check("times snap to a quarter of an hour", LayoutHit.Snap(7) == 0 && LayoutHit.Snap(8) == 15 && LayoutHit.Snap(547) == 540 && LayoutHit.Snap(548) == 555 && LayoutHit.Snap(-4) == 0 && LayoutHit.Snap(7.5) == 15 && LayoutHit.Snap(37.5) == 45);
        check("a height on the grid is a time", Near(LayoutHit.MinutesAt(164, 100, 480), 480 + 60) && Near(LayoutHit.MinutesAt(100, 100, 480), 480) && Near(LayoutHit.MinutesAt(132, 100, 480), 510));
        check("it starts at nine when its top edge is an hour down a grid that starts at eight", LayoutHit.StartFor(164, 100, 480, 60) == 540 && LayoutHit.StartFor(-500, 100, 480, 60) == 0 && LayoutHit.StartFor(5000, 100, 480, 30) == 1410 && LayoutHit.StartFor(5000, 100, 480, 0) == 1425);
        check("an end follows the pointer, a step at a time, never before the start or past the day", LayoutHit.EndFor(228, 100, 480, 540) == 600);
        check("an end is at least a step after the start", LayoutHit.EndFor(100, 100, 480, 540) == 555 && LayoutHit.EndFor(9999, 100, 480, 540) == 1440 && LayoutHit.EndFor(164, 100, 480, 540) == 555);
        var containers = new[] { new HitRect(0, 0, 400, 400), new HitRect(10, 10, 100, 100), new HitRect(200, 200, 300, 300) };
        check("the smallest container under the point wins, and none when there is none", LayoutHit.Smallest(containers, 50, 50) == 1 && LayoutHit.Smallest(containers, 250, 250) == 2 && LayoutHit.Smallest(containers, 150, 150) == 0 && LayoutHit.Smallest(containers, 500, 500) == -1 && LayoutHit.Smallest(containers, 105, 50, 6) == 1);
        var column = new[] { new HitRect(0, 0, 100, 40), new HitRect(0, 50, 100, 90), new HitRect(0, 100, 100, 140) };
        check("the place in a column is before the first part whose middle is below the point", LayoutHit.IndexAmong(column, false, 50, 5) == 0 && LayoutHit.IndexAmong(column, false, 50, 25) == 1 && LayoutHit.IndexAmong(column, false, 50, 75) == 2 && LayoutHit.IndexAmong(column, false, 50, 130) == 3 && LayoutHit.IndexAmong(Array.Empty<HitRect>(), false, 1, 1) == 0);
        var row = new[] { new HitRect(0, 0, 100, 40), new HitRect(110, 0, 210, 40) };
        check("and in a row, before the first whose middle is right of it", LayoutHit.IndexAmong(row, true, 20, 500) == 0 && LayoutHit.IndexAmong(row, true, 150, 0) == 1 && LayoutHit.IndexAmong(row, true, 205, 0) == 2);

        var loop = new[] { (0.0, 0.0), (100.0, 0.0), (100.0, 100.0), (0.0, 100.0) };
        check("a point is in a polygon or it is not", LayoutHit.InsidePolygon(50, 50, loop) && !LayoutHit.InsidePolygon(150, 50, loop) && !LayoutHit.InsidePolygon(50, -1, loop) && LayoutHit.InsidePolygon(5, 95, loop));
        check("a lasso takes what it mostly encloses", LayoutHit.MostlyInside(new HitRect(10, 10, 60, 60), loop) && LayoutHit.MostlyInside(new HitRect(60, 10, 120, 60), loop) && !LayoutHit.MostlyInside(new HitRect(90, 10, 190, 60), loop) && !LayoutHit.MostlyInside(new HitRect(10, 10, 60, 60), new[] { (0.0, 0.0), (1.0, 1.0) }));
        check("a tap stays within five pixels of where it began", LayoutHit.IsTap(new[] { (10.0, 10.0), (12.0, 11.0), (10.5, 9.0) }) && !LayoutHit.IsTap(new[] { (10.0, 10.0), (20.0, 10.0) }) && !LayoutHit.IsTap(Array.Empty<(double, double)>())
            && !LayoutHit.IsTap(new[] { (10.0, 10.0), (14.0, 14.0) }) && LayoutHit.IsTap(new[] { (10.0, 10.0), (13.0, 13.0) })); // a circle of five, as on the web

        // ----- where a timeline puts things -----
        check("a time is a height on the grid, 64 pixels to the hour", Near(LayoutHit.TimelineY(540, 480), 64) && Near(LayoutHit.TimelineY(480, 480), 0) && Near(LayoutHit.TimelineY(1080, 480), 640));
        var one = LayoutHit.TimelineSlot(540, 570, 0, 1, 300, 480);
        check("an item alone has the width, a pixel in from the left and three short, and the height of its length", Near(one.Left, 1) && Near(one.Right, 298) && Near(one.Top, 64) && Near(one.Height, 30));
        var two = LayoutHit.TimelineSlot(840, 930, 1, 2, 300, 480);
        check("two that overlap each have half, side by side", Near(two.Left, 151) && Near(two.Right, 298) && Near(two.Top, 384) && Near(two.Height, 94));
        check("a very short item is still tall enough to see", Near(LayoutHit.TimelineSlot(540, 545, 0, 1, 300, 480).Height, 14));
        var tree = Layout.Parse(Layout.Sanitize("<layout><col id=\"d\" timeline=\"8-18\"><box start=\"9:00\" end=\"10:00\">a</box><box start=\"9:30\" end=\"10:30\">b</box><box start=\"9:45\" end=\"10:15\">c</box><box start=\"11:00\" end=\"12:00\">d</box></col></layout>")!)!;
        var lanes = Layout.LanesOf(tree.Descendants().Where(d => Layout.TimesOf(d) is not null).Select(d => (d, Layout.TimesOf(d)!.Value.Start, Layout.TimesOf(d)!.Value.End)));
        var byWords = tree.Descendants().Where(d => !d.IsText && d.Tag == "box").ToDictionary(d => d.Words());
        check("items that overlap in a chain are in separate lanes sharing the width, and one on its own has all of it", lanes[byWords["a"]] == (0, 3) && lanes[byWords["b"]] == (1, 3) && lanes[byWords["c"]] == (2, 3) && lanes[byWords["d"]] == (0, 1));
    }

}
