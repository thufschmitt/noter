using System;
using System.Linq;
using Longhand.Services;

/// <summary>
/// Checks for reading the text out of a drawing, which the Windows app lays over the picture itself because its SVG renderer
/// (Direct2D) draws no text.
/// </summary>
static class SvgTextChecks
{
    static bool Near(double a, double b) => Math.Abs(a - b) < 0.01;

    public static void Run(Action<string, bool> check)
    {
        // The kind of drawing the system prompt asks for.
        var house = Svg.Labels("""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 400 200"><title>A house</title><path d="M50 100 L200 20" stroke="currentColor"/><text x="150" y="150" font-family="Ink Free, Segoe Print, sans-serif" font-size="20" fill="currentColor">home</text></svg>""");
        var home = house.Labels.SingleOrDefault();
        check("a label is read with its place, size and font", home is not null && home.Text == "home" && Near(home.X, 150) && Near(home.Y, 150) && Near(home.Size, 20) && home.Family == "Ink Free, Segoe Print, sans-serif");
        check("currentColor text takes the ink colour, upright and anchored at its start", home is { Fill: null, Angle: 0, Anchor: Svg.Anchor.Start, Bold: false, Opacity: 1 });
        check("the viewBox comes with the labels", house is { MinX: 0, MinY: 0, Width: 400, Height: 200 });
        check("the title is not a label", house.Labels.All(l => l.Text != "A house"));

        var styled = Svg.Labels("""<svg viewBox="10 20 100 50"><g font-size="12" fill="#c00" style="text-anchor:middle"><text x="50" y="40" style="font-weight:bold;font-size:14px">Total</text><text x="60" y="45" fill="none">hidden</text></g></svg>""");
        var total = styled.Labels.SingleOrDefault();
        check("style and inherited properties count, and the style beats the attribute", total is not null && Near(total.Size, 14) && total.Bold && total.Anchor == Svg.Anchor.Middle && total.Fill == "#c00");
        check("text with no fill is left out, and a viewBox origin is kept", styled.Labels.Count == 1 && styled is { MinX: 10, MinY: 20 });

        var moved = Svg.Labels("""<svg viewBox="0 0 300 300"><g transform="translate(100 50) scale(2)"><text x="10" y="20" font-size="10">A</text></g><text x="0" y="0" transform="rotate(90)" font-size="10">B</text></svg>""");
        var a = moved.Labels.Single(l => l.Text == "A");
        var b = moved.Labels.Single(l => l.Text == "B");
        check("group transforms move and scale a label", Near(a.X, 120) && Near(a.Y, 90) && Near(a.Size, 20) && Near(a.Angle, 0));
        check("a rotated label keeps its angle", Near(b.Angle, 90) && Near(b.X, 0) && Near(b.Y, 0) && Near(b.Size, 10));

        var lines = Svg.Labels("""<svg viewBox="0 0 200 200"><text x="20" y="30" font-size="10">Shopping:<tspan x="20" dy="1.5em">eggs</tspan><tspan x="20" dy="15">  milk  </tspan></text></svg>""");
        check("tspans that set their own place are lines of their own", lines.Labels.Select(l => l.Text).SequenceEqual(new[] { "Shopping:", "eggs", "milk" })
            && Near(lines.Labels[1].Y, 45) && Near(lines.Labels[2].Y, 60) && lines.Labels.All(l => Near(l.X, 20)));

        var inline = Svg.Labels("""<svg width="120" height="60"><text x="5" y="20">x<tspan font-weight="bold">²</tspan> + 1</text></svg>""");
        check("a tspan with no place of its own runs on in the same label", inline.Labels.Count == 1 && inline.Labels[0].Text == "x² + 1" && Near(inline.Labels[0].Size, 16));
        check("without a viewBox, width and height give the box", inline is { Width: 120, Height: 60 });

        check("a drawing with no text has no labels", Svg.Labels("""<svg viewBox="0 0 4 3"><circle r="1"/></svg>""").Labels.Count == 0);
        check("a broken drawing gives no labels rather than failing", Svg.Labels("<svg><text>").Labels.Count == 0);
    }
}
