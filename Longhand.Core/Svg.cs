using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Longhand.Services;

/// <summary>Finds the SVG drawings in a reply and checks they are safe and sensible to show.</summary>
public static class Svg
{
    public const int MaxBytes = 60_000;
    public const int MaxDrawings = 3;

    static readonly Regex Tag = new(@"<svg\b|</svg\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Forbidden = new(
        @"<\s*(script|foreignObject|image|iframe)\b|<!DOCTYPE|<!ENTITY|\son\w+\s*=|javascript:|(xlink:)?href\s*=\s*[""'](?!#)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex FenceBefore = new(@"```[ \t]*(?:svg|xml)?[ \t]*\r?\n?\s*(?=<svg\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex FenceAfter = new(@"(?<=</svg>)\s*```", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <param name="Text">The reply with the drawings taken out, words in their original order.</param>
    /// <param name="Drawings">The complete <c>&lt;svg&gt;…&lt;/svg&gt;</c> elements, in order.</param>
    /// <param name="Open">True while a drawing has started but not ended, as when the reply is still streaming.</param>
    public readonly record struct Split(string Text, IReadOnlyList<string> Drawings, bool Open);

    public static Split Extract(string body, bool finished)
    {
        body = FenceAfter.Replace(FenceBefore.Replace(body, ""), "");
        var text = new StringBuilder();
        var drawings = new List<string>();
        bool open = false;

        int copied = 0, depth = 0, start = -1;
        foreach (Match m in Tag.Matches(body))
        {
            bool opening = m.Value.Length >= 4 && m.Value[1] != '/';
            if (opening)
            {
                if (depth == 0)
                {
                    text.Append(body, copied, m.Index - copied);
                    start = m.Index;
                }
                depth++;
            }
            else if (depth > 0)
            {
                depth--;
                if (depth == 0)
                {
                    drawings.Add(body.Substring(start, m.Index + m.Length - start));
                    copied = m.Index + m.Length;
                }
            }
        }
        if (depth > 0) open = true; // the rest is an unfinished drawing
        else text.Append(body, copied, body.Length - copied);

        var words = text.ToString();
        // While streaming, "<", "<s" or "<sv" at the very end may be the start of a drawing: don't show them yet.
        if (!finished && !open)
            for (int len = Math.Min(3, words.Length); len >= 1; len--)
                if ("<svg".StartsWith(words[^len..], StringComparison.OrdinalIgnoreCase)) { words = words[..^len]; break; }

        words = Regex.Replace(words, @"[ \t]+\n", "\n");
        words = Regex.Replace(words, @"\n{3,}", "\n\n").Trim();
        return new Split(words, drawings, open);
    }

    /// <summary>The drawing ready to show, or null when it's too big, unsafe or not an SVG.</summary>
    public static string? Sanitize(string svg)
    {
        if (Encoding.UTF8.GetByteCount(svg) > MaxBytes || Forbidden.IsMatch(svg)) return null;
        // SvgImageSource wants the namespace; models often leave it out.
        if (!Regex.IsMatch(svg, @"^\s*<svg\b[^>]*\bxmlns\s*=", RegexOptions.IgnoreCase))
            svg = Regex.Replace(svg, @"^\s*<svg\b", @"<svg xmlns=""http://www.w3.org/2000/svg""", RegexOptions.IgnoreCase);
        try
        {
            using var reader = XmlReader.Create(new StringReader(svg), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            bool sawRoot = false, isSvg = false;
            while (reader.Read()) // all of it, so a drawing that stops halfway is caught
                if (!sawRoot && reader.NodeType == XmlNodeType.Element)
                {
                    sawRoot = true;
                    isSvg = reader.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase);
                }
            return isSvg ? svg.Trim() : null;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>Where a text label's position is: its start, middle or end (SVG's <c>text-anchor</c>).</summary>
    public enum Anchor { Start, Middle, End }

    /// <summary>
    /// One run of SVG text, in the drawing's own coordinates (its viewBox): <c>X</c>, <c>Y</c> is the anchor point on the baseline,
    /// rotated by <c>Angle</c> degrees about it. <c>Fill</c> is null for the ink colour ("currentColor", or none given).
    /// </summary>
    public sealed record Label(string Text, double X, double Y, double Size, Anchor Anchor, double Angle, string? Family, bool Bold, bool Italic, string? Fill, double Opacity);

    /// <summary>The drawing's viewBox and its text, for a renderer that draws the shapes but not the text.</summary>
    public sealed record TextLayer(double MinX, double MinY, double Width, double Height, IReadOnlyList<Label> Labels);

    /// <summary>
    /// The text in a drawing, as labels to lay over a picture of it. Windows' SVG renderer (Direct2D) draws shapes but leaves
    /// out <c>&lt;text&gt;</c> entirely, so the app puts the words on top itself. Handles positions, <c>&lt;tspan&gt;</c> lines,
    /// transforms on the text and its groups, and the font attributes or <c>style</c> properties that matter for a sketch.
    /// </summary>
    public static TextLayer Labels(string svg)
    {
        XElement root;
        try { root = XElement.Parse(svg); }
        catch (XmlException) { return new TextLayer(0, 0, 400, 300, Array.Empty<Label>()); }

        var (minX, minY, width, height) = ViewBox(root);
        var labels = new List<Label>();
        foreach (var text in root.Descendants().Where(e => e.Name.LocalName == "text"))
        {
            if (text.Ancestors().Any(a => a.Name.LocalName is "defs" or "clipPath" or "mask" or "symbol" or "title" or "desc")) continue;
            var m = Matrix.Identity;
            foreach (var e in text.AncestorsAndSelf().TakeWhile(e => e != root).Reverse())
                m = m.Then(Matrix.Parse((string?)e.Attribute("transform")));
            double scale = Math.Sqrt(Math.Abs(m.A * m.D - m.B * m.C));
            double angle = Math.Atan2(m.B, m.A) * 180 / Math.PI;

            double size = Length(Prop(text, "font-size"), 16, 16);
            var anchor = Prop(text, "text-anchor") switch { "middle" => Anchor.Middle, "end" => Anchor.End, _ => Anchor.Start };
            var family = Prop(text, "font-family");
            var weight = Prop(text, "font-weight");
            bool bold = weight is "bold" or "bolder" || (int.TryParse(weight, out var w) && w >= 600);
            bool italic = Prop(text, "font-style") is "italic" or "oblique";
            var fill = Prop(text, "fill");
            if (fill == "none" || Prop(text, "display") == "none" || Prop(text, "visibility") == "hidden") continue;
            if (fill is "currentColor" or "inherit") fill = null;
            double opacity = 1;
            foreach (var e in text.AncestorsAndSelf().TakeWhile(e => e != root))
                opacity *= Number(Own(e, "opacity"), 1);
            opacity *= Number(Prop(text, "fill-opacity"), 1);

            // Runs: the text's own words, and each tspan that sets its own place starts a new one (as multi-line labels do).
            double x = First(text.Attribute("x")?.Value), y = First(text.Attribute("y")?.Value);
            x += First(text.Attribute("dx")?.Value);
            y += Length(FirstToken(text.Attribute("dy")?.Value), size);
            var run = new StringBuilder();
            double runX = x, runY = y;
            void Flush()
            {
                var words = Regex.Replace(run.ToString(), @"\s+", " ").Trim();
                run.Clear();
                if (words.Length == 0) return;
                var (px, py) = m.Apply(runX, runY);
                labels.Add(new Label(words, px, py, size * scale, anchor, angle, family, bold, italic, fill, Math.Clamp(opacity, 0, 1)));
            }
            foreach (var node in text.Nodes())
            {
                if (node is XText t) run.Append(t.Value);
                else if (node is XElement span && span.Name.LocalName == "tspan")
                {
                    var sx = span.Attribute("x")?.Value; var sy = span.Attribute("y")?.Value;
                    var dx = span.Attribute("dx")?.Value; var dy = span.Attribute("dy")?.Value;
                    if (sx is not null || sy is not null || dx is not null || dy is not null)
                    {
                        Flush();
                        if (sx is not null) x = First(sx);
                        if (sy is not null) y = First(sy);
                        x += First(dx);
                        y += Length(FirstToken(dy), size);
                        runX = x; runY = y;
                    }
                    run.Append(span.Value);
                }
            }
            Flush();
        }
        return new TextLayer(minX, minY, width, height, labels);
    }

    static (double MinX, double MinY, double Width, double Height) ViewBox(XElement root)
    {
        var box = ((string?)root.Attribute("viewBox"))?.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (box is { Length: 4 } && box.Select(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _)).All(ok => ok))
        {
            var v = box.Select(b => double.Parse(b, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
            if (v[2] > 0 && v[3] > 0) return (v[0], v[1], v[2], v[3]);
        }
        double width = Length((string?)root.Attribute("width"), 0, 16), height = Length((string?)root.Attribute("height"), 0, 16);
        return width > 0 && height > 0 ? (0, 0, width, height) : (0, 0, 400, 300);
    }

    /// <summary>A presentation property, from the element's <c>style</c> or attribute, or else inherited from its ancestors.</summary>
    static string? Prop(XElement e, string name)
    {
        for (var at = e; at is not null; at = at.Parent)
            if (Own(at, name) is { } value && value != "inherit") return value;
        return null;
    }

    static string? Own(XElement e, string name)
    {
        if ((string?)e.Attribute("style") is { } style)
            foreach (var part in style.Split(';'))
            {
                var colon = part.IndexOf(':');
                if (colon > 0 && part[..colon].Trim() == name) return part[(colon + 1)..].Trim();
            }
        return ((string?)e.Attribute(name))?.Trim();
    }

    static double Number(string? text, double fallback) =>
        double.TryParse(text?.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            ? (text!.Trim().EndsWith("%") ? n / 100 : n)
            : fallback;

    /// <summary>The first number of a list such as "10 20 30" (SVG lets x and y place each letter).</summary>
    static double First(string? list) => Number(FirstToken(list), 0);

    static string? FirstToken(string? list) => list?.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

    /// <summary>A length in user units: plain or px, or em relative to the font size.</summary>
    static double Length(string? text, double size, double fallback = 0)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        text = text.Trim();
        if (text.EndsWith("em")) return Number(text[..^2], 0) * size;
        if (text.EndsWith("px") || text.EndsWith("pt")) return Number(text[..^2], fallback) * (text.EndsWith("pt") ? 4.0 / 3 : 1);
        return Number(text, fallback);
    }

    /// <summary>A 2D affine transform, as SVG writes it: x' = a·x + c·y + e, y' = b·x + d·y + f.</summary>
    readonly record struct Matrix(double A, double B, double C, double D, double E, double F)
    {
        public static readonly Matrix Identity = new(1, 0, 0, 1, 0, 0);

        public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);

        /// <summary>This transform, with <paramref name="inner"/> applied first (an element's own transform inside its parents').</summary>
        public Matrix Then(Matrix inner) => new(
            A * inner.A + C * inner.B, B * inner.A + D * inner.B,
            A * inner.C + C * inner.D, B * inner.C + D * inner.D,
            A * inner.E + C * inner.F + E, B * inner.E + D * inner.F + F);

        public static Matrix Parse(string? transform)
        {
            var m = Identity;
            if (string.IsNullOrWhiteSpace(transform)) return m;
            foreach (Match f in Regex.Matches(transform, @"(\w+)\s*\(([^)]*)\)"))
            {
                var n = Regex.Matches(f.Groups[2].Value, @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?")
                    .Select(v => double.Parse(v.Value, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
                double At(int i, double fallback) => i < n.Length ? n[i] : fallback;
                Matrix step = f.Groups[1].Value switch
                {
                    "matrix" when n.Length == 6 => new(n[0], n[1], n[2], n[3], n[4], n[5]),
                    "translate" => new(1, 0, 0, 1, At(0, 0), At(1, 0)),
                    "scale" => new(At(0, 1), 0, 0, At(1, At(0, 1)), 0, 0),
                    "rotate" => Rotate(At(0, 0), At(1, 0), At(2, 0)),
                    "skewX" => new(1, 0, Math.Tan(At(0, 0) * Math.PI / 180), 1, 0, 0),
                    "skewY" => new(1, Math.Tan(At(0, 0) * Math.PI / 180), 0, 1, 0, 0),
                    _ => Identity,
                };
                m = m.Then(step);
            }
            return m;
        }

        static Matrix Rotate(double degrees, double cx, double cy)
        {
            double r = degrees * Math.PI / 180, cos = Math.Cos(r), sin = Math.Sin(r);
            return new Matrix(1, 0, 0, 1, cx, cy).Then(new Matrix(cos, sin, -sin, cos, 0, 0)).Then(new Matrix(1, 0, 0, 1, -cx, -cy));
        }
    }

    /// <summary>Width divided by height, from the viewBox (or width and height), kept within a sane range.</summary>
    public static double Aspect(string svg)
    {
        var tag = Regex.Match(svg, @"<svg\b[^>]*>", RegexOptions.IgnoreCase).Value;
        double? Number(string text) => double.TryParse(Regex.Match(text, @"^[\d.]+").Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;

        var box = Regex.Match(tag, @"viewBox\s*=\s*[""']\s*[-\d.]+[ ,]+[-\d.]+[ ,]+([\d.]+)[ ,]+([\d.]+)\s*[""']", RegexOptions.IgnoreCase);
        double? w = null, h = null;
        if (box.Success) (w, h) = (Number(box.Groups[1].Value), Number(box.Groups[2].Value));
        if (w is null || h is null)
        {
            var wa = Regex.Match(tag, @"\swidth\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            var ha = Regex.Match(tag, @"\sheight\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            if (wa.Success && ha.Success) (w, h) = (Number(wa.Groups[1].Value), Number(ha.Groups[1].Value));
        }
        return w is > 0 && h is > 0 ? Math.Clamp(w.Value / h.Value, 0.2, 5) : 4.0 / 3.0;
    }

    /// <summary>A short description for the history and for screen readers: the drawing's title, if it has one.</summary>
    public static string Title(string svg)
    {
        var m = Regex.Match(svg, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var title = m.Success ? Regex.Replace(System.Net.WebUtility.HtmlDecode(m.Groups[1].Value), @"\s+", " ").Trim() : "";
        return title.Length > 0 ? title : "a drawing";
    }

    /// <summary>How big to show it: as wide as allowed, but never taller than <paramref name="maxHeight"/>.</summary>
    public static (double Width, double Height) Fit(double aspect, double maxWidth, double maxHeight)
    {
        double w = maxWidth, h = w / aspect;
        if (h > maxHeight) (w, h) = (maxHeight * aspect, maxHeight);
        return (w, h);
    }
}
