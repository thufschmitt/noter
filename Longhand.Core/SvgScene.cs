using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Longhand.Services;

/// <summary>A 2D transform, the way SVG writes it: a point (x, y) goes to (A x + C y + E, B x + D y + F).</summary>
public readonly record struct Matrix2(double A, double B, double C, double D, double E, double F)
{
    public static readonly Matrix2 Identity = new(1, 0, 0, 1, 0, 0);

    public bool IsIdentity => this == Identity;

    /// <summary>The transform that applies <paramref name="inner"/> first and then this one (SVG's transform lists multiply left to right).</summary>
    public Matrix2 Multiply(Matrix2 inner) => new(
        A * inner.A + C * inner.B, B * inner.A + D * inner.B,
        A * inner.C + C * inner.D, B * inner.C + D * inner.D,
        A * inner.E + C * inner.F + E, B * inner.E + D * inner.F + F);

    public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);

    public static Matrix2 Translate(double x, double y) => new(1, 0, 0, 1, x, y);
    public static Matrix2 Scale(double x, double y) => new(x, 0, 0, y, 0, 0);

    public static Matrix2 Rotate(double degrees)
    {
        double r = degrees * Math.PI / 180, cos = Math.Cos(r), sin = Math.Sin(r);
        return new(cos, sin, -sin, cos, 0, 0);
    }

    static readonly Regex Function = new(@"([A-Za-z]+)\s*\(([^)]*)\)", RegexOptions.Compiled);

    /// <summary>A <c>transform</c> attribute (matrix, translate, scale, rotate, skewX, skewY, several in a row); anything it can't read counts as no transform.</summary>
    public static Matrix2 Parse(string? text)
    {
        var result = Identity;
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (Match m in Function.Matches(text))
        {
            var args = SvgPath.Numbers(m.Groups[2].Value);
            double Arg(int i, double fallback) => i < args.Count ? args[i] : fallback;
            Matrix2? step = m.Groups[1].Value switch
            {
                "matrix" when args.Count == 6 => new Matrix2(args[0], args[1], args[2], args[3], args[4], args[5]),
                "translate" when args.Count >= 1 => Translate(args[0], Arg(1, 0)),
                "scale" when args.Count >= 1 => Scale(args[0], Arg(1, args[0])),
                "rotate" when args.Count == 1 => Rotate(args[0]),
                "rotate" when args.Count == 3 => Translate(args[1], args[2]).Multiply(Rotate(args[0])).Multiply(Translate(-args[1], -args[2])),
                "skewX" when args.Count == 1 => new Matrix2(1, 0, Math.Tan(args[0] * Math.PI / 180), 1, 0, 0),
                "skewY" when args.Count == 1 => new Matrix2(1, Math.Tan(args[0] * Math.PI / 180), 0, 1, 0, 0),
                _ => null,
            };
            if (step is { } s && IsFinite(s)) result = result.Multiply(s);
        }
        return result;
    }

    static bool IsFinite(Matrix2 m) => double.IsFinite(m.A) && double.IsFinite(m.B) && double.IsFinite(m.C) && double.IsFinite(m.D) && double.IsFinite(m.E) && double.IsFinite(m.F);
}

public enum PathKind { Move, Line, Cubic, Quad, Arc, Close }

/// <summary>One step of a path, in absolute coordinates. A cubic has two control points, a quadratic one (X1, Y1); an arc has radii, a turn, and the two flags.</summary>
public readonly record struct PathSeg(PathKind Kind, double X = 0, double Y = 0, double X1 = 0, double Y1 = 0, double X2 = 0, double Y2 = 0,
    double Rx = 0, double Ry = 0, double Rotation = 0, bool Large = false, bool Sweep = false);

/// <summary>SVG path data read into steps with absolute coordinates, which is what a native renderer wants (no relative commands, no shorthand curves).</summary>
public static class SvgPath
{
    /// <summary>Every number in a list written with commas or spaces (<c>10,20 30</c>); reading stops at the first thing that isn't one.</summary>
    public static List<double> Numbers(string? text)
    {
        var list = new List<double>();
        if (text is null) return list;
        int i = 0;
        while (Number(text, ref i, out var v)) list.Add(v);
        return list;
    }

    static void Skip(string s, ref int i)
    {
        while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == ',')) i++;
    }

    static bool Number(string s, ref int i, out double value)
    {
        Skip(s, ref i);
        int start = i, digits = 0;
        value = 0;
        if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
        while (i < s.Length && char.IsAsciiDigit(s[i])) { i++; digits++; }
        if (i < s.Length && s[i] == '.')
        {
            i++;
            while (i < s.Length && char.IsAsciiDigit(s[i])) { i++; digits++; }
        }
        if (digits == 0) { i = start; return false; }
        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
        {
            int save = i++, exponent = 0;
            if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
            while (i < s.Length && char.IsAsciiDigit(s[i])) { i++; exponent++; }
            if (exponent == 0) i = save;
        }
        return double.TryParse(s.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    // Arc flags are single characters and may run into the next number ("a1 1 0 011 1").
    static bool Flag(string s, ref int i, out bool value)
    {
        Skip(s, ref i);
        value = false;
        if (i >= s.Length || (s[i] != '0' && s[i] != '1')) return false;
        value = s[i++] == '1';
        return true;
    }

    /// <summary>The steps of a path; what can't be read ends it (what came before is kept). Empty when it doesn't start with a moveto.</summary>
    public static List<PathSeg> Parse(string? d)
    {
        var segs = new List<PathSeg>();
        if (string.IsNullOrWhiteSpace(d) || d.Length > 200_000) return segs;
        double cx = 0, cy = 0, startX = 0, startY = 0, lastCubicX = 0, lastCubicY = 0, lastQuadX = 0, lastQuadY = 0;
        bool afterCubic = false, afterQuad = false, needMove = false;
        char command = '\0';
        int i = 0;

        void Emit(PathSeg seg)
        {
            if (needMove && seg.Kind != PathKind.Move)
            {
                // Drawing on after a close starts a new subpath where the closed one began.
                segs.Add(new PathSeg(PathKind.Move, startX, startY));
                needMove = false;
            }
            segs.Add(seg);
        }

        while (true)
        {
            Skip(d, ref i);
            if (i >= d.Length) break;
            if (char.IsAsciiLetter(d[i]))
            {
                command = d[i++];
                if (command is 'Z' or 'z')
                {
                    if (segs.Count == 0) break;
                    segs.Add(new PathSeg(PathKind.Close));
                    cx = startX;
                    cy = startY;
                    needMove = true;
                    afterCubic = afterQuad = false;
                    continue;
                }
                if (segs.Count == 0 && command is not ('M' or 'm')) break;
            }
            else if (command == '\0' || command is 'Z' or 'z') break;

            bool rel = char.IsAsciiLetterLower(command);
            double ox = rel ? cx : 0, oy = rel ? cy : 0;
            double x, y, x1, y1, x2, y2;
            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                    if (!Number(d, ref i, out x) || !Number(d, ref i, out y)) return segs;
                    cx = startX = x + ox;
                    cy = startY = y + oy;
                    needMove = false;
                    segs.Add(new PathSeg(PathKind.Move, cx, cy));
                    command = rel ? 'l' : 'L'; // more pairs after a moveto are lines
                    afterCubic = afterQuad = false;
                    break;
                case 'L':
                    if (!Number(d, ref i, out x) || !Number(d, ref i, out y)) return segs;
                    cx = x + ox; cy = y + oy;
                    Emit(new PathSeg(PathKind.Line, cx, cy));
                    afterCubic = afterQuad = false;
                    break;
                case 'H':
                    if (!Number(d, ref i, out x)) return segs;
                    cx = x + ox;
                    Emit(new PathSeg(PathKind.Line, cx, cy));
                    afterCubic = afterQuad = false;
                    break;
                case 'V':
                    if (!Number(d, ref i, out y)) return segs;
                    cy = y + oy;
                    Emit(new PathSeg(PathKind.Line, cx, cy));
                    afterCubic = afterQuad = false;
                    break;
                case 'C':
                    if (!Number(d, ref i, out x1) || !Number(d, ref i, out y1) || !Number(d, ref i, out x2) || !Number(d, ref i, out y2) || !Number(d, ref i, out x) || !Number(d, ref i, out y)) return segs;
                    Emit(new PathSeg(PathKind.Cubic, x + ox, y + oy, x1 + ox, y1 + oy, x2 + ox, y2 + oy));
                    lastCubicX = x2 + ox; lastCubicY = y2 + oy;
                    cx = x + ox; cy = y + oy;
                    afterCubic = true; afterQuad = false;
                    break;
                case 'S':
                    if (!Number(d, ref i, out x2) || !Number(d, ref i, out y2) || !Number(d, ref i, out x) || !Number(d, ref i, out y)) return segs;
                    x1 = afterCubic ? 2 * cx - lastCubicX : cx;
                    y1 = afterCubic ? 2 * cy - lastCubicY : cy;
                    Emit(new PathSeg(PathKind.Cubic, x + ox, y + oy, x1, y1, x2 + ox, y2 + oy));
                    lastCubicX = x2 + ox; lastCubicY = y2 + oy;
                    cx = x + ox; cy = y + oy;
                    afterCubic = true; afterQuad = false;
                    break;
                case 'Q':
                    if (!Number(d, ref i, out x1) || !Number(d, ref i, out y1) || !Number(d, ref i, out x) || !Number(d, ref i, out y)) return segs;
                    Emit(new PathSeg(PathKind.Quad, x + ox, y + oy, x1 + ox, y1 + oy));
                    lastQuadX = x1 + ox; lastQuadY = y1 + oy;
                    cx = x + ox; cy = y + oy;
                    afterQuad = true; afterCubic = false;
                    break;
                case 'T':
                    if (!Number(d, ref i, out x) || !Number(d, ref i, out y)) return segs;
                    x1 = afterQuad ? 2 * cx - lastQuadX : cx;
                    y1 = afterQuad ? 2 * cy - lastQuadY : cy;
                    Emit(new PathSeg(PathKind.Quad, x + ox, y + oy, x1, y1));
                    lastQuadX = x1; lastQuadY = y1;
                    cx = x + ox; cy = y + oy;
                    afterQuad = true; afterCubic = false;
                    break;
                case 'A':
                    if (!Number(d, ref i, out var rx) || !Number(d, ref i, out var ry) || !Number(d, ref i, out var turn) || !Flag(d, ref i, out var large) || !Flag(d, ref i, out var sweep)
                        || !Number(d, ref i, out x) || !Number(d, ref i, out y)) return segs;
                    cx = x + ox; cy = y + oy;
                    Emit(new PathSeg(PathKind.Arc, cx, cy, Rx: Math.Abs(rx), Ry: Math.Abs(ry), Rotation: turn, Large: large, Sweep: sweep));
                    afterCubic = afterQuad = false;
                    break;
                default:
                    return segs; // a command that isn't one
            }
        }
        return segs;
    }

    // ----- the basic shapes, as paths -----

    public static List<PathSeg> Rect(double x, double y, double w, double h, double rx, double ry)
    {
        var s = new List<PathSeg>();
        if (!(w > 0) || !(h > 0)) return s;
        if (rx <= 0 && ry > 0) rx = ry;
        if (ry <= 0 && rx > 0) ry = rx;
        rx = Math.Min(Math.Max(rx, 0), w / 2);
        ry = Math.Min(Math.Max(ry, 0), h / 2);
        if (rx <= 0 || ry <= 0)
        {
            s.Add(new PathSeg(PathKind.Move, x, y));
            s.Add(new PathSeg(PathKind.Line, x + w, y));
            s.Add(new PathSeg(PathKind.Line, x + w, y + h));
            s.Add(new PathSeg(PathKind.Line, x, y + h));
            s.Add(new PathSeg(PathKind.Close));
            return s;
        }
        s.Add(new PathSeg(PathKind.Move, x + rx, y));
        s.Add(new PathSeg(PathKind.Line, x + w - rx, y));
        s.Add(new PathSeg(PathKind.Arc, x + w, y + ry, Rx: rx, Ry: ry, Sweep: true));
        s.Add(new PathSeg(PathKind.Line, x + w, y + h - ry));
        s.Add(new PathSeg(PathKind.Arc, x + w - rx, y + h, Rx: rx, Ry: ry, Sweep: true));
        s.Add(new PathSeg(PathKind.Line, x + rx, y + h));
        s.Add(new PathSeg(PathKind.Arc, x, y + h - ry, Rx: rx, Ry: ry, Sweep: true));
        s.Add(new PathSeg(PathKind.Line, x, y + ry));
        s.Add(new PathSeg(PathKind.Arc, x + rx, y, Rx: rx, Ry: ry, Sweep: true));
        s.Add(new PathSeg(PathKind.Close));
        return s;
    }

    public static List<PathSeg> Ellipse(double cx, double cy, double rx, double ry)
    {
        var s = new List<PathSeg>();
        if (!(rx > 0) || !(ry > 0)) return s;
        s.Add(new PathSeg(PathKind.Move, cx + rx, cy));
        s.Add(new PathSeg(PathKind.Arc, cx - rx, cy, Rx: rx, Ry: ry, Sweep: true));
        s.Add(new PathSeg(PathKind.Arc, cx + rx, cy, Rx: rx, Ry: ry, Sweep: true));
        s.Add(new PathSeg(PathKind.Close));
        return s;
    }

    public static List<PathSeg> Poly(string? points, bool close)
    {
        var s = new List<PathSeg>();
        var n = Numbers(points);
        for (int k = 0; k + 1 < n.Count; k += 2) s.Add(new PathSeg(k == 0 ? PathKind.Move : PathKind.Line, n[k], n[k + 1]));
        if (close && s.Count > 1) s.Add(new PathSeg(PathKind.Close));
        return s;
    }
}

public enum SvgPaintKind { None, Current, Color }

/// <summary>A fill or stroke: nothing, the current colour (<c>currentColor</c>, which is the ink unless <c>color</c> says otherwise), or a colour.</summary>
public readonly record struct SvgPaint(SvgPaintKind Kind, byte R = 0, byte G = 0, byte B = 0, double Alpha = 1)
{
    public static readonly SvgPaint None = new(SvgPaintKind.None);
    public static readonly SvgPaint Current = new(SvgPaintKind.Current);
    public static readonly SvgPaint Black = new(SvgPaintKind.Color, 0, 0, 0);

    static readonly Dictionary<string, int> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = 0x000000, ["white"] = 0xFFFFFF, ["red"] = 0xFF0000, ["green"] = 0x008000, ["blue"] = 0x0000FF, ["yellow"] = 0xFFFF00,
        ["orange"] = 0xFFA500, ["purple"] = 0x800080, ["pink"] = 0xFFC0CB, ["brown"] = 0xA52A2A, ["gray"] = 0x808080, ["grey"] = 0x808080,
        ["silver"] = 0xC0C0C0, ["gold"] = 0xFFD700, ["navy"] = 0x000080, ["teal"] = 0x008080, ["maroon"] = 0x800000, ["olive"] = 0x808000,
        ["lime"] = 0x00FF00, ["aqua"] = 0x00FFFF, ["cyan"] = 0x00FFFF, ["fuchsia"] = 0xFF00FF, ["magenta"] = 0xFF00FF,
        ["lightgray"] = 0xD3D3D3, ["lightgrey"] = 0xD3D3D3, ["darkgray"] = 0xA9A9A9, ["darkgrey"] = 0xA9A9A9, ["dimgray"] = 0x696969, ["dimgrey"] = 0x696969,
        ["lightblue"] = 0xADD8E6, ["darkblue"] = 0x00008B, ["lightgreen"] = 0x90EE90, ["darkgreen"] = 0x006400, ["darkred"] = 0x8B0000,
        ["orangered"] = 0xFF4500, ["crimson"] = 0xDC143C, ["coral"] = 0xFF7F50, ["salmon"] = 0xFA8072, ["tomato"] = 0xFF6347, ["khaki"] = 0xF0E68C,
        ["violet"] = 0xEE82EE, ["indigo"] = 0x4B0082, ["turquoise"] = 0x40E0D0, ["tan"] = 0xD2B48C, ["beige"] = 0xF5F5DC, ["ivory"] = 0xFFFFF0,
        ["lavender"] = 0xE6E6FA, ["skyblue"] = 0x87CEEB, ["steelblue"] = 0x4682B4, ["royalblue"] = 0x4169E1, ["dodgerblue"] = 0x1E90FF,
        ["slategray"] = 0x708090, ["slategrey"] = 0x708090, ["forestgreen"] = 0x228B22, ["seagreen"] = 0x2E8B57, ["chocolate"] = 0xD2691E,
        ["sienna"] = 0xA0522D, ["firebrick"] = 0xB22222, ["hotpink"] = 0xFF69B4, ["deeppink"] = 0xFF1493, ["orchid"] = 0xDA70D6, ["plum"] = 0xDDA0DD,
        ["wheat"] = 0xF5DEB3, ["whitesmoke"] = 0xF5F5F5, ["gainsboro"] = 0xDCDCDC, ["limegreen"] = 0x32CD32, ["darkorange"] = 0xFF8C00,
        ["midnightblue"] = 0x191970, ["cornflowerblue"] = 0x6495ED, ["mediumpurple"] = 0x9370DB, ["darkviolet"] = 0x9400D3, ["goldenrod"] = 0xDAA520,
    };

    static byte Channel(string text)
    {
        text = text.Trim();
        if (text.EndsWith('%') && double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct)) return (byte)Math.Round(Math.Clamp(pct, 0, 100) / 100 * 255);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (byte)Math.Round(Math.Clamp(v, 0, 255)) : (byte)0;
    }

    /// <summary>A paint value (<c>none</c>, <c>currentColor</c>, a name, <c>#rgb</c>, <c>#rrggbb</c>, <c>rgb()</c>, <c>rgba()</c>); null when it isn't one, so the inherited value stays.</summary>
    public static SvgPaint? Parse(string? value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) return null;
        if (v.Equals("none", StringComparison.OrdinalIgnoreCase) || v.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return None;
        if (v.Equals("currentColor", StringComparison.OrdinalIgnoreCase) || v.Equals("inherit", StringComparison.OrdinalIgnoreCase)) return Current;
        if (v[0] == '#')
        {
            var hex = v[1..];
            if (hex.Length == 3 && hex.All(char.IsAsciiHexDigit)) hex = string.Concat(hex.Select(c => new string(c, 2)));
            if (hex.Length == 6 && hex.All(char.IsAsciiHexDigit))
            {
                int n = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return new SvgPaint(SvgPaintKind.Color, (byte)(n >> 16), (byte)(n >> 8), (byte)n);
            }
            return null;
        }
        var fn = Regex.Match(v, @"^rgba?\(\s*([^,\s)]+)\s*[, ]\s*([^,\s)]+)\s*[, ]\s*([^,\s)/]+)\s*(?:[,/]\s*([^)\s]+)\s*)?\)$", RegexOptions.IgnoreCase);
        if (fn.Success)
        {
            double alpha = 1;
            if (fn.Groups[4].Success)
            {
                var a = fn.Groups[4].Value;
                if (a.EndsWith('%') ? double.TryParse(a[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out alpha) : double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out alpha))
                    alpha = Math.Clamp(a.EndsWith('%') ? alpha / 100 : alpha, 0, 1);
                else alpha = 1;
            }
            return new SvgPaint(SvgPaintKind.Color, Channel(fn.Groups[1].Value), Channel(fn.Groups[2].Value), Channel(fn.Groups[3].Value), alpha);
        }
        if (Named.TryGetValue(v, out var rgb)) return new SvgPaint(SvgPaintKind.Color, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return null;
    }
}

/// <summary>What a shape or text looks like once everything it inherits has been worked out.</summary>
public sealed class SvgStyle
{
    public SvgPaint Fill = SvgPaint.Black, Stroke = SvgPaint.None;
    public double FillOpacity = 1, StrokeOpacity = 1, StrokeWidth = 1, MiterLimit = 4, DashOffset;
    public bool EvenOdd, Bold, Italic, Hidden;
    public string LineCap = "butt", LineJoin = "miter", Anchor = "start";
    public double[]? Dash;
    /// <summary>What <c>currentColor</c> stands for; null is the ink.</summary>
    public SvgPaint? Color;
    /// <summary>The page's own text size (26) applies when nothing says otherwise, as in the frame the web version draws in.</summary>
    public double FontSize = 26;

    public SvgStyle Clone() => (SvgStyle)MemberwiseClone();
}

public abstract class SvgNode
{
    public Matrix2 Transform = Matrix2.Identity;
    public double Opacity = 1;
}

public sealed class SvgGroup : SvgNode
{
    public List<SvgNode> Children = new();
}

public sealed class SvgPathNode : SvgNode
{
    public List<PathSeg> Segments = new();
    public required SvgStyle Style;
}

/// <summary>One line of text: where its baseline starts, and the words.</summary>
public sealed class SvgTextNode : SvgNode
{
    public double X, Y;
    public string Text = "";
    public required SvgStyle Style;
}

/// <summary>
/// A drawing read from the SVG the model wrote (already cleaned by <see cref="Svg.Sanitize"/>) into what a native renderer draws:
/// groups, paths (every basic shape becomes one) and lines of text, each with its style worked out. Markers, masks, clip paths and
/// patterns aren't drawn; a gradient is drawn as its first colour.
/// </summary>
public sealed class SvgScene
{
    /// <summary>The viewBox (or the declared size, or 300×150): the drawing's own coordinates.</summary>
    public double MinX, MinY, Width = 300, Height = 150;
    /// <summary>The width the drawing asks to be shown at, when it says (<c>width="400"</c>).</summary>
    public double? DeclaredWidth;
    public SvgGroup Root = new();
    public double Aspect => Height > 0 ? Width / Height : 4.0 / 3.0;

    const int MaxDepth = 24;
    const int MaxNodes = 5000;

    public static SvgScene? Parse(string? markup)
    {
        if (string.IsNullOrWhiteSpace(markup)) return null;
        var doc = new HtmlParser().ParseDocument("<!doctype html><html><body>" + markup + "</body></html>");
        if (doc.Body!.Children.FirstOrDefault(e => e.LocalName == "svg") is not { } svg) return null;
        var scene = new SvgScene();

        var box = Numbers(Attribute(svg, "viewBox"));
        double? declaredW = Length(Attribute(svg, "width")), declaredH = Length(Attribute(svg, "height"));
        if (box.Count == 4 && box[2] > 0 && box[3] > 0)
        {
            (scene.MinX, scene.MinY, scene.Width, scene.Height) = (box[0], box[1], box[2], box[3]);
        }
        else
        {
            if (declaredW is > 0) scene.Width = declaredW.Value;
            if (declaredH is > 0) scene.Height = declaredH.Value;
        }
        scene.DeclaredWidth = declaredW is > 0 ? declaredW : null;

        var context = new Context(doc.Body);
        var rootStyle = context.Apply(new SvgStyle(), svg);
        context.Walk(svg, scene.Root, rootStyle, 0);
        return scene;
    }

    static List<double> Numbers(string? text) => SvgPath.Numbers(text);

    /// <summary>An attribute by name, whatever the case it was written in (the HTML parser keeps SVG's camel case, but be safe).</summary>
    static string? Attribute(IElement e, string name)
    {
        if (e.GetAttribute(name) is { } exact) return exact;
        foreach (var a in e.Attributes) if (a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return a.Value;
        return null;
    }

    /// <summary>The number at the start of a length (<c>400</c>, <c>400px</c>); null for anything else, percentages included.</summary>
    static double? Length(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Regex.Match(text.Trim(), @"^([+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)\s*(px)?$");
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
    }

    static double Number(string? text, double fallback) => Length(text) ?? fallback;

    static double Opacity(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 1;
        var t = text.Trim();
        bool pct = t.EndsWith('%');
        return double.TryParse(pct ? t[..^1] : t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? Math.Clamp(pct ? v / 100 : v, 0, 1) : 1;
    }

    sealed class Context
    {
        readonly IElement _body;
        int _nodes;

        public Context(IElement body) => _body = body;

        static Dictionary<string, string> Css(IElement e)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (Attribute(e, "style") is not { } style) return map;
            foreach (var declaration in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = declaration.IndexOf(':');
                if (colon <= 0) continue;
                map[declaration[..colon].Trim()] = Regex.Replace(declaration[(colon + 1)..], @"\s*!important\s*$", "", RegexOptions.IgnoreCase).Trim();
            }
            return map;
        }

        static string? Prop(IElement e, Dictionary<string, string> css, string name) => css.TryGetValue(name, out var v) ? v : Attribute(e, name);

        /// <summary>The paint a value stands for; <c>url(#gradient)</c> is the gradient's first colour.</summary>
        SvgPaint? Paint(string? value)
        {
            var v = (value ?? "").Trim();
            if (v.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
            {
                var id = Regex.Match(v, @"url\(\s*['""]?#([^'"")\s]+)").Groups[1].Value;
                if (id.Length > 0 && _body.QuerySelectorAll("[id]").FirstOrDefault(e => e.Id == id) is { } target
                    && target.QuerySelector("stop") is { } stop)
                {
                    var css = Css(stop);
                    var color = SvgPaint.Parse(Prop(stop, css, "stop-color")) ?? SvgPaint.Black;
                    return color.Kind == SvgPaintKind.Color ? color with { Alpha = color.Alpha * Opacity(Prop(stop, css, "stop-opacity")) } : color;
                }
                return SvgPaint.None;
            }
            return SvgPaint.Parse(v);
        }

        /// <summary>The style of an element: what it inherits, with its own presentation attributes and <c>style</c> on top.</summary>
        public SvgStyle Apply(SvgStyle inherited, IElement e)
        {
            var s = inherited.Clone();
            var css = Css(e);
            if (Paint(Prop(e, css, "fill")) is { } fill) s.Fill = fill;
            if (Paint(Prop(e, css, "stroke")) is { } stroke) s.Stroke = stroke;
            if (Prop(e, css, "fill-opacity") is { } fo) s.FillOpacity = Opacity(fo);
            if (Prop(e, css, "stroke-opacity") is { } so) s.StrokeOpacity = Opacity(so);
            if (Length(Prop(e, css, "stroke-width")) is { } sw && sw >= 0) s.StrokeWidth = sw;
            if (Prop(e, css, "fill-rule") is { } rule) s.EvenOdd = rule.Trim().Equals("evenodd", StringComparison.OrdinalIgnoreCase);
            if (OneOf(Prop(e, css, "stroke-linecap"), "butt", "round", "square") is { } cap) s.LineCap = cap;
            if (OneOf(Prop(e, css, "stroke-linejoin"), "miter", "round", "bevel") is { } join) s.LineJoin = join;
            if (Length(Prop(e, css, "stroke-miterlimit")) is { } ml && ml >= 1) s.MiterLimit = ml;
            if (Prop(e, css, "stroke-dasharray") is { } dash)
            {
                var dashes = dash.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) ? new List<double>() : SvgPath.Numbers(dash);
                s.Dash = dashes.Count > 0 && dashes.All(v => v >= 0) && dashes.Sum() > 0 ? dashes.ToArray() : null;
            }
            if (Length(Prop(e, css, "stroke-dashoffset")) is { } off) s.DashOffset = off;
            if (SvgPaint.Parse(Prop(e, css, "color")) is { } color && color.Kind == SvgPaintKind.Color) s.Color = color;
            if (Length(Prop(e, css, "font-size")) is { } size && size > 0) s.FontSize = size;
            if (Prop(e, css, "font-weight")?.Trim().ToLowerInvariant() is { } weight) s.Bold = weight is "bold" or "bolder" || (int.TryParse(weight, out var w) && w >= 600);
            if (Prop(e, css, "font-style")?.Trim().ToLowerInvariant() is { } fs) s.Italic = fs is "italic" or "oblique";
            if (OneOf(Prop(e, css, "text-anchor"), "start", "middle", "end") is { } anchor) s.Anchor = anchor;
            if (Prop(e, css, "visibility")?.Trim().ToLowerInvariant() is { } vis) s.Hidden = vis is "hidden" or "collapse";
            return s;
        }

        /// <summary>The value, in lower case, when it is one of the allowed words; otherwise null.</summary>
        static string? OneOf(string? value, params string[] allowed)
        {
            var v = value?.Trim().ToLowerInvariant();
            return v is not null && allowed.Contains(v) ? v : null;
        }

        static bool NotDisplayed(IElement e) => Prop(e, Css(e), "display")?.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) == true;

        public void Walk(IElement parent, SvgGroup into, SvgStyle style, int depth)
        {
            if (depth > MaxDepth) return;
            foreach (var el in parent.Children)
            {
                if (_nodes++ > MaxNodes) return;
                if (NotDisplayed(el)) continue;
                var name = el.LocalName.ToLowerInvariant();
                var css = Css(el);
                var transform = Matrix2.Parse(Attribute(el, "transform"));
                double opacity = Opacity(Prop(el, css, "opacity"));
                switch (name)
                {
                    case "g":
                    case "svg":
                    {
                        var group = new SvgGroup { Transform = transform, Opacity = opacity };
                        Walk(el, group, Apply(style, el), depth + 1);
                        into.Children.Add(group);
                        break;
                    }
                    case "path":
                        AddPath(into, el, style, transform, opacity, SvgPath.Parse(Attribute(el, "d")));
                        break;
                    case "rect":
                        AddPath(into, el, style, transform, opacity,
                            SvgPath.Rect(Number(Attribute(el, "x"), 0), Number(Attribute(el, "y"), 0), Number(Attribute(el, "width"), 0), Number(Attribute(el, "height"), 0),
                                Number(Attribute(el, "rx"), 0), Number(Attribute(el, "ry"), 0)));
                        break;
                    case "circle":
                    {
                        double r = Number(Attribute(el, "r"), 0);
                        AddPath(into, el, style, transform, opacity, SvgPath.Ellipse(Number(Attribute(el, "cx"), 0), Number(Attribute(el, "cy"), 0), r, r));
                        break;
                    }
                    case "ellipse":
                        AddPath(into, el, style, transform, opacity,
                            SvgPath.Ellipse(Number(Attribute(el, "cx"), 0), Number(Attribute(el, "cy"), 0), Number(Attribute(el, "rx"), 0), Number(Attribute(el, "ry"), 0)));
                        break;
                    case "line":
                        AddPath(into, el, style, transform, opacity, new List<PathSeg>
                        {
                            new(PathKind.Move, Number(Attribute(el, "x1"), 0), Number(Attribute(el, "y1"), 0)),
                            new(PathKind.Line, Number(Attribute(el, "x2"), 0), Number(Attribute(el, "y2"), 0)),
                        });
                        break;
                    case "polyline":
                        AddPath(into, el, style, transform, opacity, SvgPath.Poly(Attribute(el, "points"), close: false));
                        break;
                    case "polygon":
                        AddPath(into, el, style, transform, opacity, SvgPath.Poly(Attribute(el, "points"), close: true));
                        break;
                    case "text":
                        AddText(into, el, Apply(style, el), transform, opacity);
                        break;
                    // defs, symbol, marker, clipPath, mask, pattern, gradients, title and desc aren't drawn where they are
                }
            }
        }

        void AddPath(SvgGroup into, IElement el, SvgStyle inherited, Matrix2 transform, double opacity, List<PathSeg> segments)
        {
            if (segments.Count == 0) return;
            var style = Apply(inherited, el);
            if (style.Hidden) return;
            into.Children.Add(new SvgPathNode { Segments = segments, Style = style, Transform = transform, Opacity = opacity });
        }

        static double First(string? list, double fallback) => SvgPath.Numbers(list) is { Count: > 0 } n ? n[0] : fallback;

        /// <summary>
        /// Text is one line per <c>tspan</c> that sets a position (<c>x</c>, <c>y</c>, <c>dx</c>, <c>dy</c>); the rest of the words
        /// run on in the line they are in. Whitespace runs are one space.
        /// </summary>
        void AddText(SvgGroup into, IElement el, SvgStyle style, Matrix2 transform, double opacity)
        {
            if (style.Hidden) return;
            double x = First(Attribute(el, "x"), 0) + First(Attribute(el, "dx"), 0), y = First(Attribute(el, "y"), 0) + First(Attribute(el, "dy"), 0);
            var line = new StringBuilder();
            double lineX = x, lineY = y;

            void Flush()
            {
                var words = Regex.Replace(line.ToString(), @"\s+", " ").Trim();
                line.Clear();
                if (words.Length > 0 && words.Length <= 2000) into.Children.Add(new SvgTextNode { X = lineX, Y = lineY, Text = words, Style = style, Transform = transform, Opacity = opacity });
            }

            foreach (var child in el.ChildNodes)
            {
                if (child is IText text) line.Append(text.Data);
                else if (child is IElement span && span.LocalName.Equals("tspan", StringComparison.OrdinalIgnoreCase))
                {
                    if (span.HasAttribute("x") || span.HasAttribute("y") || span.HasAttribute("dx") || span.HasAttribute("dy"))
                    {
                        Flush();
                        lineX = First(Attribute(span, "x"), lineX) + First(Attribute(span, "dx"), 0);
                        lineY = First(Attribute(span, "y"), lineY) + First(Attribute(span, "dy"), 0);
                    }
                    line.Append(span.TextContent);
                }
            }
            Flush();
        }
    }
}
