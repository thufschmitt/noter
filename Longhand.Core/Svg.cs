using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Ganss.Xss;

namespace Longhand.Services;

/// <summary>A rectangle in a drawing's own coordinates (its viewBox units).</summary>
public readonly record struct Box(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
    public (double X, double Y) Center => (X + W / 2, Y + H / 2);
    public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;
    public bool Overlaps(Box o) => X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;
}

/// <summary>One thing the user did to an element of a drawing: moved it, turned it, or deleted it.</summary>
/// <param name="Op">"move", "rotate" or "delete".</param>
/// <param name="Transform">The element's new <c>transform</c> attribute, for a move or a turn.</param>
/// <param name="Degrees">How far it was turned (clockwise is positive), for a turn.</param>
public sealed record DrawingChange(string Id, string Op, string? Transform = null, double Degrees = 0);

/// <summary>
/// The drawings the model can put under its words: inline SVG, and nothing else (no HTML). This finds them in a reply, makes them
/// safe (no scripts, nothing fetched, no links), gives the text version the model reads in later turns, applies the user's
/// edits to them, and builds the document both apps render them from.
///
/// An element with a <c>data-edit</c> attribute is one the user can select, move, turn or delete. The edits are written into the
/// drawing (a <c>transform</c> on the element, or the element removed), so the saved page shows them.
/// </summary>
public static class Svg
{
    public const int MaxBytes = 100_000;
    public const int MaxBlocks = 3;
    /// <summary>A drawing taller than this, rendered at the page's text width, is scaled down to fit.</summary>
    public const double MaxHeight = 1200;
    /// <summary>Stands for the ink colour in <see cref="Document"/>; the app puts in its own, which follows the theme.</summary>
    public const string InkToken = "%INK%";
    public const string EditAttribute = "data-edit";

    static readonly Regex Opening = new(@"<svg\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex FenceBefore = new(@"```[ \t]*(?:html|svg|xml)?[ \t]*\r?\n?\s*(?=<svg\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex FenceAfter = new(@"(?<=</svg>)\s*```", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Drawings used to sit in a <figure>: the wrapper goes, the drawing in it stays.
    static readonly Regex Figure = new(@"</?figure\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <param name="Text">The reply with the drawings taken out, words in their original order.</param>
    /// <param name="Blocks">The complete <c>&lt;svg&gt;…&lt;/svg&gt;</c> elements, in order.</param>
    /// <param name="Open">True while a drawing has started but not ended, as when the reply is still streaming.</param>
    public readonly record struct Split(string Text, IReadOnlyList<string> Blocks, bool Open);

    public static Split Extract(string body, bool finished)
    {
        body = Figure.Replace(FenceAfter.Replace(FenceBefore.Replace(body, ""), ""), "");
        var text = new StringBuilder();
        var blocks = new List<string>();
        bool open = false;
        int at = 0;
        while (true)
        {
            var m = Opening.Match(body, at);
            if (!m.Success)
            {
                text.Append(body, at, body.Length - at);
                break;
            }
            text.Append(body, at, m.Index - at);
            int end = Close(body, m.Index);
            if (end < 0)
            {
                open = true; // the rest is an unfinished drawing
                break;
            }
            blocks.Add(body.Substring(m.Index, end - m.Index));
            at = end;
        }

        var words = text.ToString();
        // While streaming, the start of "<svg" (or of the old "<figure") at the very end may begin a drawing: don't show it yet.
        if (!finished && !open)
            for (int len = Math.Min(7, words.Length); len >= 1; len--)
            {
                var tail = words[^len..];
                if ("<svg".StartsWith(tail, StringComparison.OrdinalIgnoreCase) || "<figure".StartsWith(tail, StringComparison.OrdinalIgnoreCase))
                {
                    words = words[..^len];
                    break;
                }
            }

        words = Regex.Replace(words, @"[ \t]+\n", "\n");
        words = Regex.Replace(words, @"\n{3,}", "\n\n").Trim();
        return new Split(words, blocks, open);
    }

    /// <summary>Just past the tag that closes the svg opened at <paramref name="start"/>, or -1 when it isn't closed yet.</summary>
    static int Close(string s, int start)
    {
        int depth = 0;
        foreach (Match m in Regex.Matches(s[start..], @"<svg\b|</svg\s*>", RegexOptions.IgnoreCase))
        {
            if (m.Value[1] != '/') depth++;
            else if (--depth == 0) return start + m.Index + m.Length;
        }
        return -1;
    }

    static readonly string[] Tags =
    {
        "svg", "g", "path", "rect", "circle", "ellipse", "line", "polyline", "polygon", "text", "tspan", "title", "desc", "defs",
        "marker", "linearGradient", "radialGradient", "stop", "clipPath", "mask", "pattern", "symbol",
    };
    static readonly string[] Attributes =
    {
        "class", "id", "style", "title", "aria-label", "role", "lang", "dir", EditAttribute,
        "xmlns", "width", "height", "viewBox", "preserveAspectRatio", "d", "x", "y", "x1", "y1", "x2", "y2", "cx", "cy", "r", "rx", "ry", "fx", "fy",
        "points", "transform", "fill", "fill-opacity", "fill-rule", "stroke", "stroke-width", "stroke-linecap", "stroke-linejoin",
        "stroke-dasharray", "stroke-dashoffset", "stroke-opacity", "stroke-miterlimit", "opacity", "font-family", "font-size",
        "font-weight", "font-style", "text-anchor", "dominant-baseline", "alignment-baseline", "letter-spacing", "text-decoration",
        "dx", "dy", "rotate", "textLength", "marker-start", "marker-mid", "marker-end", "markerWidth", "markerHeight", "markerUnits",
        "refX", "refY", "orient", "offset", "stop-color", "stop-opacity", "gradientUnits", "gradientTransform", "spreadMethod",
        "clip-path", "clipPathUnits", "mask", "maskUnits", "patternUnits", "patternTransform", "visibility", "display", "vector-effect",
    };

    static HtmlSanitizer NewSanitizer()
    {
        var s = new HtmlSanitizer();
        s.AllowedTags.Clear();
        foreach (var t in Tags) s.AllowedTags.Add(t);
        s.AllowedAttributes.Clear();
        foreach (var a in Attributes) s.AllowedAttributes.Add(a);
        // Nothing may be fetched or followed: no schemes at all, and only same-document references such as url(#arrow).
        s.AllowedSchemes.Clear();
        s.FilterUrl += (_, e) => { if (!e.OriginalUrl.TrimStart().StartsWith("#")) e.SanitizedUrl = null; };
        return s;
    }

    [ThreadStatic] static HtmlSanitizer? _sanitizer;

    static IElement? ParseSvg(string html) =>
        new HtmlParser().ParseDocument("<!doctype html><html><body>" + html + "</body></html>").Body!.Children.FirstOrDefault(e => e.LocalName == "svg");

    /// <summary>
    /// The drawing made safe to show: only SVG is kept. Elements the user may edit (<c>data-edit</c>) get an id when they have
    /// none, so an edit can name them. A drawing still in the old <c>&lt;figure&gt;</c> wrapper is unwrapped. Null when it is too
    /// big, isn't SVG, or nothing is left of it.
    /// </summary>
    public static string? Sanitize(string raw)
    {
        if (Encoding.UTF8.GetByteCount(raw) > MaxBytes) return null;
        raw = Figure.Replace(raw.Trim(), "").Trim();
        if (!raw.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)) return null;
        var clean = (_sanitizer ??= NewSanitizer()).Sanitize(raw).Trim();
        var svg = ParseSvg(clean);
        if (svg is null || svg.Children.Length == 0) return null;
        svg.SetAttribute("xmlns", "http://www.w3.org/2000/svg");
        svg.RemoveAttribute(EditAttribute); // the drawing as a whole isn't an element to move
        var used = new HashSet<string>(svg.QuerySelectorAll("[id]").Select(e => e.GetAttribute("id")!));
        int n = 0;
        foreach (var e in svg.QuerySelectorAll("[" + EditAttribute + "]"))
        {
            if (!string.IsNullOrEmpty(e.GetAttribute("id"))) continue;
            string id;
            do id = "item-" + ++n; while (!used.Add(id));
            e.SetAttribute("id", id);
        }
        return svg.OuterHtml;
    }

    /// <summary>The id of the drawing's root element, which names the drawing: a new one with the same id replaces it. Null when it has none.</summary>
    public static string? FigureId(string markup) => ParseSvg(markup)?.GetAttribute("id") is { Length: > 0 } id ? id : null;

    /// <summary>True when something in the drawing can be edited by the user.</summary>
    public static bool IsEditable(string markup) => ParseSvg(markup)?.QuerySelector("[" + EditAttribute + "]") is not null;

    /// <summary>The ids of everything the user may edit.</summary>
    public static IReadOnlyList<string> EditableIds(string markup) =>
        ParseSvg(markup)?.QuerySelectorAll("[" + EditAttribute + "]").Select(e => e.GetAttribute("id") ?? "").Where(i => i.Length > 0).ToList() ?? new List<string>();

    /// <summary>A short description, for the page's accessibility and the history: the drawing's label or title.</summary>
    public static string Title(string markup)
    {
        var svg = ParseSvg(markup);
        if (svg is null) return "a drawing";
        string Tidy(string? s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();
        foreach (var candidate in new[] { svg.GetAttribute("aria-label"), svg.GetAttribute("title"), svg.QuerySelector("title")?.TextContent })
            if (Tidy(candidate) is { Length: > 0 } t) return t.Length > 80 ? t[..80] + "…" : t;
        return "a drawing";
    }

    /// <summary>
    /// The drawing as the history shows it to the model later: its title, its id if it has one, and whether the user can edit it.
    /// (When the user has edited it, the model is sent its source with the changes instead.)
    /// </summary>
    public static string Text(string markup)
    {
        var id = FigureId(markup);
        var title = Title(markup);
        var parts = new List<string>();
        if (id is not null) parts.Add($"id \"{id}\"");
        if (title != "a drawing") parts.Add(title);
        if (IsEditable(markup)) parts.Add("editable");
        return "[drawing" + (parts.Count > 0 ? ": " + string.Join(", ", parts) : "") + "]";
    }

    // ----- the user's edits -----

    static readonly Regex SafeTransform = new(@"^\s*(?:(?:translate|rotate|scale|matrix|skewX|skewY)\s*\(\s*[-+0-9.eE,\s]*\))(?:\s*,?\s*(?:translate|rotate|scale|matrix|skewX|skewY)\s*\(\s*[-+0-9.eE,\s]*\))*\s*$", RegexOptions.Compiled);

    /// <summary>
    /// The drawing with the user's changes written in: a moved or turned element gets its new <c>transform</c>, a deleted one is
    /// removed. Null when a change names something that isn't editable (the drawing is then left as it was).
    /// </summary>
    public static string? ApplyEdits(string markup, IReadOnlyList<DrawingChange> changes)
    {
        var svg = ParseSvg(markup);
        if (svg is null) return null;
        foreach (var change in changes)
        {
            var el = svg.QuerySelectorAll("[" + EditAttribute + "]").FirstOrDefault(e => e != svg && e.GetAttribute("id") == change.Id);
            if (el is null) return null;
            switch (change.Op)
            {
                case "delete":
                    el.Remove();
                    break;
                case "move":
                case "rotate":
                    if (change.Transform is not { } t || !SafeTransform.IsMatch(t)) return null;
                    el.SetAttribute("transform", t.Trim());
                    break;
                default:
                    return null;
            }
        }
        return svg.OuterHtml;
    }

    /// <summary>The ids that are ancestors or descendants of the element with this id: parts of the same thing, not things it overlaps.</summary>
    public static ISet<string> Relatives(string markup, string id)
    {
        var related = new HashSet<string>();
        var svg = ParseSvg(markup);
        var el = svg?.QuerySelectorAll("[id]").FirstOrDefault(e => e.GetAttribute("id") == id);
        if (el is null) return related;
        for (var up = el.ParentElement; up is not null; up = up.ParentElement)
            if (up.GetAttribute("id") is { Length: > 0 } a) related.Add(a);
        foreach (var down in el.QuerySelectorAll("[id]")) related.Add(down.GetAttribute("id")!);
        return related;
    }

    // ----- showing it -----

    /// <summary>The handwriting fonts, best first: the same list the page's own words use.</summary>
    public const string HandFonts = "\"Ink Free\", \"Segoe Print\", \"Bradley Hand\", \"Caveat\", \"Comic Neue\", \"Comic Sans MS\", \"Chalkboard SE\", cursive";

    /// <summary>
    /// The page a drawing is rendered from: the handwriting font and the ink colour on a clear background, and a content policy
    /// that lets nothing run or load. <see cref="InkToken"/> stands for the ink colour.
    /// </summary>
    public static string Document(string markup) => """
        <!doctype html>
        <html><head><meta charset="utf-8">
        <meta name="color-scheme" content="light dark">
        <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src data:">
        <style>
        html, body { margin: 0; padding: 0; background: transparent; }
        body { color: %INK%; font: 26px/40px FONTS; overflow: hidden; -webkit-font-smoothing: antialiased; -webkit-user-select: none; user-select: none; }
        svg { display: block; max-width: 100%; height: auto; max-height: 480px; overflow: visible; font-family: FONTS; }
        </style></head>
        <body>BLOCK</body></html>
        """.Replace("FONTS", HandFonts).Replace("BLOCK", markup);

    /// <summary>How big to show a drawing: as wide as allowed, but never taller than <paramref name="maxHeight"/>.</summary>
    public static (double Width, double Height) Fit(double aspect, double maxWidth, double maxHeight = MaxHeight)
    {
        if (!(aspect > 0)) aspect = 4.0 / 3.0;
        double w = maxWidth, h = w / aspect;
        if (h > maxHeight) (w, h) = (maxHeight * aspect, maxHeight);
        return (w, h);
    }

    // ----- telling the model what the user did -----

    /// <summary>What the user did to one element since the model last heard.</summary>
    public sealed class EditEntry
    {
        /// <summary>Where the element was before the first edit; null when that wasn't measured.</summary>
        public Box? First;
        public bool Deleted;
        /// <summary>Total turn, clockwise degrees.</summary>
        public double Degrees;

        public EditEntry Copy() => new() { First = First, Deleted = Deleted, Degrees = Degrees };

        /// <summary>False when the element ended up where and how it began (moved away and back).</summary>
        public bool IsNet(Box? now) =>
            Deleted || Math.Abs(Degrees % 360) > 0.5 || First is not { } was || now is not { } is_
            || Math.Abs(is_.Center.X - was.Center.X) > 0.5 || Math.Abs(is_.Center.Y - was.Center.Y) > 0.5;
    }

    static string N(double v)
    {
        var r = Math.Round(v);
        return (r == 0 ? 0 : r).ToString(CultureInfo.InvariantCulture);
    }

    static string Signed(double v)
    {
        var r = Math.Round(v);
        return (r >= 0 ? "+" : "") + r.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The list of what the user changed in one drawing, one line per element, with the geometry that makes it mechanical to read:
    /// how far it moved, and which other named parts it now sits inside or over. Null when nothing is left that differs.
    /// </summary>
    public static string? DescribeEdits(string markup, IReadOnlyDictionary<string, EditEntry> edits, IReadOnlyDictionary<string, Box> boxes)
    {
        var lines = new List<string>();
        foreach (var (id, e) in edits.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            boxes.TryGetValue(id, out var nowBox);
            Box? now = e.Deleted || !boxes.ContainsKey(id) ? null : nowBox;
            if (!e.IsNet(now)) continue;
            if (e.Deleted)
            {
                lines.Add($"- {id}: deleted" + (e.First is { } gone ? $" (it was at x {N(gone.X)}, y {N(gone.Y)}, {N(gone.W)}×{N(gone.H)})" : ""));
                continue;
            }
            var what = new List<string>();
            if (now is { } n && e.First is { } was)
            {
                double dx = n.Center.X - was.Center.X, dy = n.Center.Y - was.Center.Y;
                if (Math.Abs(dx) > 0.5 || Math.Abs(dy) > 0.5) what.Add($"moved by ({Signed(dx)}, {Signed(dy)}), from x {N(was.X)}, y {N(was.Y)} to x {N(n.X)}, y {N(n.Y)}");
            }
            if (Math.Abs(e.Degrees % 360) > 0.5)
            {
                var d = ((e.Degrees % 360) + 540) % 360 - 180; // −180…180
                what.Add($"turned {N(Math.Abs(d))}° {(d >= 0 ? "clockwise" : "anticlockwise")}");
            }
            if (what.Count == 0) what.Add(now is { } only ? $"changed (now at x {N(only.X)}, y {N(only.Y)}, {N(only.W)}×{N(only.H)})" : "moved or turned");
            var line = $"- {id}: {string.Join(", ", what)}";
            if (now is { } box)
            {
                var related = Relatives(markup, id);
                var inside = new List<string>();
                var over = new List<string>();
                foreach (var (other, ob) in boxes)
                {
                    if (other == id || related.Contains(other) || edits.TryGetValue(other, out var oe) && oe.Deleted) continue;
                    if (ob.Contains(box.Center.X, box.Center.Y) && ob.W * ob.H > box.W * box.H) inside.Add("#" + other);
                    else if (ob.Overlaps(box)) over.Add("#" + other);
                }
                if (inside.Count > 0) line += $"; its centre is now inside {string.Join(", ", inside.Take(6))}";
                if (over.Count > 0) line += $"; it overlaps {string.Join(", ", over.Take(6))}";
            }
            lines.Add(line);
        }
        return lines.Count > 0 ? string.Join("\n", lines) : null;
    }
}
