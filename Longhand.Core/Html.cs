using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Ganss.Xss;

namespace Longhand.Services;

/// <summary>
/// The blocks Claude can put under its words: a little static HTML in a <c>&lt;figure&gt;</c>, for a table, laid-out or rich text,
/// or a drawing as inline SVG. This finds them in a reply, makes them safe (no scripts, nothing fetched, no links), gives the text
/// version Claude reads in later turns, and builds the document both apps render them from.
/// </summary>
public static class Html
{
    public const int MaxBytes = 100_000;
    public const int MaxBlocks = 3;
    /// <summary>A block taller than this, rendered at the page's text width, is scaled down to fit.</summary>
    public const double MaxHeight = 1200;
    /// <summary>Stands for the ink colour in <see cref="Document"/>; the app puts in its own, which follows the theme.</summary>
    public const string InkToken = "%INK%";

    static readonly Regex Opening = new(@"<(figure|svg)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex FenceBefore = new(@"```[ \t]*(?:html|svg|xml)?[ \t]*\r?\n?\s*(?=<(?:figure|svg)\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex FenceAfter = new(@"(?<=</(?:figure|svg)>)\s*```", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <param name="Text">The reply with the blocks taken out, words in their original order.</param>
    /// <param name="Blocks">The complete <c>&lt;figure&gt;…&lt;/figure&gt;</c> (or bare <c>&lt;svg&gt;…&lt;/svg&gt;</c>) elements, in order.</param>
    /// <param name="Open">True while a block has started but not ended, as when the reply is still streaming.</param>
    public readonly record struct Split(string Text, IReadOnlyList<string> Blocks, bool Open);

    public static Split Extract(string body, bool finished)
    {
        body = FenceAfter.Replace(FenceBefore.Replace(body, ""), "");
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
            int end = Close(body, m.Index, m.Groups[1].Value);
            if (end < 0)
            {
                open = true; // the rest is an unfinished block
                break;
            }
            blocks.Add(body.Substring(m.Index, end - m.Index));
            at = end;
        }

        var words = text.ToString();
        // While streaming, the start of "<figure" or "<svg" at the very end may begin a block: don't show it yet.
        if (!finished && !open)
            for (int len = Math.Min(7, words.Length); len >= 1; len--)
            {
                var tail = words[^len..];
                if ("<figure".StartsWith(tail, StringComparison.OrdinalIgnoreCase) || "<svg".StartsWith(tail, StringComparison.OrdinalIgnoreCase))
                {
                    words = words[..^len];
                    break;
                }
            }

        words = Regex.Replace(words, @"[ \t]+\n", "\n");
        words = Regex.Replace(words, @"\n{3,}", "\n\n").Trim();
        return new Split(words, blocks, open);
    }

    /// <summary>Just past the tag that closes the element opened at <paramref name="start"/>, or -1 when it isn't closed yet.</summary>
    static int Close(string s, int start, string tag)
    {
        int depth = 0;
        foreach (Match m in Regex.Matches(s[start..], $@"<{tag}\b|</{tag}\s*>", RegexOptions.IgnoreCase))
        {
            if (m.Value[1] != '/') depth++;
            else if (--depth == 0) return start + m.Index + m.Length;
        }
        return -1;
    }

    static readonly string[] HtmlTags =
    {
        "figure", "figcaption", "div", "span", "p", "br", "hr", "h1", "h2", "h3", "h4", "h5", "h6", "section", "header", "footer",
        "strong", "b", "em", "i", "u", "s", "del", "ins", "mark", "small", "sub", "sup", "code", "pre", "kbd", "q", "blockquote",
        "abbr", "time", "a", "ul", "ol", "li", "dl", "dt", "dd", "table", "thead", "tbody", "tfoot", "tr", "th", "td", "caption",
        "colgroup", "col", "style",
    };
    static readonly string[] SvgTags =
    {
        "svg", "g", "path", "rect", "circle", "ellipse", "line", "polyline", "polygon", "text", "tspan", "title", "desc", "defs",
        "marker", "linearGradient", "radialGradient", "stop", "clipPath", "mask", "pattern", "symbol",
    };
    static readonly string[] Attributes =
    {
        "class", "id", "style", "title", "aria-label", "role", "lang", "dir", "colspan", "rowspan", "scope", "span", "width", "height",
        "align", "valign", "start", "reversed", "type", "datetime",
        "xmlns", "viewBox", "preserveAspectRatio", "d", "x", "y", "x1", "y1", "x2", "y2", "cx", "cy", "r", "rx", "ry", "fx", "fy",
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
        foreach (var t in HtmlTags.Concat(SvgTags)) s.AllowedTags.Add(t);
        s.AllowedAttributes.Clear();
        foreach (var a in Attributes) s.AllowedAttributes.Add(a);
        // Nothing may be fetched or followed: no schemes at all, and only same-document references such as url(#arrow).
        s.AllowedSchemes.Clear();
        s.FilterUrl += (_, e) => { if (!e.OriginalUrl.TrimStart().StartsWith("#")) e.SanitizedUrl = null; };
        return s;
    }

    [ThreadStatic] static HtmlSanitizer? _sanitizer;

    /// <summary>
    /// The block made safe to show, as one <c>&lt;figure&gt;</c>: only static HTML, CSS and inline SVG are kept. A bare
    /// <c>&lt;svg&gt;</c> (how drawings were written before) is wrapped in one. Null when it is too big or nothing is left of it.
    /// </summary>
    public static string? Sanitize(string raw)
    {
        if (Encoding.UTF8.GetByteCount(raw) > MaxBytes) return null;
        raw = raw.Trim();
        if (raw.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)) raw = "<figure>" + raw + "</figure>";
        if (!raw.StartsWith("<figure", StringComparison.OrdinalIgnoreCase)) return null;
        var clean = (_sanitizer ??= NewSanitizer()).Sanitize(raw).Trim();
        var figure = Parse(clean).FirstOrDefault(e => e.LocalName == "figure");
        if (figure is null) return null;
        bool shows = figure.TextContent.Trim().Length > 0 || figure.QuerySelector("svg") is not null;
        return shows ? figure.OuterHtml : null;
    }

    static IEnumerable<IElement> Parse(string html)
    {
        var doc = new HtmlParser().ParseDocument("<!doctype html><html><body>" + html + "</body></html>");
        return doc.Body!.Children;
    }

    /// <summary>A short description, for the page's accessibility and the history: the block's label, caption, title or heading.</summary>
    public static string Title(string block)
    {
        var figure = Parse(block).FirstOrDefault();
        if (figure is null) return "a block";
        string Tidy(string? s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();
        foreach (var candidate in new[]
        {
            figure.GetAttribute("aria-label"),
            figure.GetAttribute("title"),
            figure.QuerySelector("figcaption")?.TextContent,
            figure.QuerySelector("svg > title")?.TextContent,
            figure.QuerySelector("caption")?.TextContent,
            figure.QuerySelector("h1, h2, h3, h4, h5, h6")?.TextContent,
        })
            if (Tidy(candidate) is { Length: > 0 } t) return t.Length > 80 ? t[..80] + "…" : t;
        return figure.QuerySelector("table") is not null ? "a table" : figure.QuerySelector("svg") is not null ? "a drawing" : "a block";
    }

    /// <summary>
    /// The block as plain text, for the history Claude reads later: table rows with cells split by " | ", list items with
    /// dashes, and each drawing as "[drawing: its title]".
    /// </summary>
    public static string Text(string block, int max = 2000)
    {
        var sb = new StringBuilder();
        foreach (var e in Parse(block)) Walk(e, sb);
        var lines = sb.ToString().Split('\n').Select(l => Regex.Replace(l, @"[ \t]+", " ").Trim());
        var text = Regex.Replace(string.Join("\n", lines), @"\n{3,}", "\n\n").Trim();
        return text.Length > max ? text[..max] + " …" : text;
    }

    static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "h1", "h2", "h3", "h4", "h5", "h6", "table", "ul", "ol", "figure", "figcaption", "section", "header", "footer",
        "blockquote", "pre", "dl", "dt", "dd", "caption", "hr",
    };

    /// <summary>Starts a new line, unless the text is already at the start of one.</summary>
    static void Line(StringBuilder sb)
    {
        if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
    }

    /// <summary>Leaves a blank line, as between paragraphs.</summary>
    static void Gap(StringBuilder sb)
    {
        Line(sb);
        if (sb.Length > 1 && sb[^2] != '\n') sb.Append('\n');
    }

    static void Walk(INode node, StringBuilder sb)
    {
        if (node is IText t)
        {
            sb.Append(t.Data.Replace('\n', ' '));
            return;
        }
        if (node is not IElement e) return;
        switch (e.LocalName)
        {
            case "style":
            case "title":
                return;
            case "svg":
                var title = Regex.Replace(e.QuerySelector("title")?.TextContent ?? "", @"\s+", " ").Trim();
                sb.Append("[drawing" + (title.Length > 0 ? ": " + title : "") + "]");
                return;
            case "br":
                sb.Append('\n');
                return;
            case "tr":
                Line(sb);
                bool first = true;
                foreach (var cell in e.Children.Where(c => c.LocalName is "td" or "th"))
                {
                    if (!first) sb.Append(" | ");
                    first = false;
                    var inner = new StringBuilder();
                    foreach (var c in cell.ChildNodes) Walk(c, inner);
                    sb.Append(Regex.Replace(inner.ToString(), @"\s+", " ").Trim());
                }
                Line(sb);
                return;
            case "li":
                Line(sb);
                sb.Append("– ");
                foreach (var c in e.ChildNodes) Walk(c, sb);
                Line(sb);
                return;
        }
        bool block = BlockTags.Contains(e.LocalName);
        if (block) Gap(sb);
        foreach (var c in e.ChildNodes) Walk(c, sb);
        if (block) Gap(sb);
    }

    /// <summary>The handwriting fonts, best first: the same list the page's own words use.</summary>
    public const string HandFonts = "\"Ink Free\", \"Segoe Print\", \"Bradley Hand\", \"Caveat\", \"Comic Neue\", \"Comic Sans MS\", \"Chalkboard SE\", cursive";

    /// <summary>
    /// The page a block is rendered from: notebook defaults (the handwriting font on the page's 40px lines, the ink colour,
    /// a clear background) that the block's own CSS may override, and a content policy that lets nothing run or load.
    /// <see cref="InkToken"/> stands for the ink colour.
    /// </summary>
    public static string Document(string block) => """
        <!doctype html>
        <html><head><meta charset="utf-8">
        <meta name="color-scheme" content="light dark">
        <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src data:">
        <style>
        html, body { margin: 0; padding: 0; background: transparent; }
        body { color: %INK%; font: 26px/40px FONTS; overflow: hidden; overflow-wrap: anywhere; -webkit-font-smoothing: antialiased; }
        figure { margin: 0; }
        p, ul, ol, dl, table, pre, blockquote, h1, h2, h3, h4, h5, h6 { margin: 0; }
        h1, h2, h3, h4, h5, h6 { font-weight: bold; font-size: 1em; line-height: 40px; }
        h1 { font-size: 1.3em; }
        h2 { font-size: 1.15em; }
        ul, ol { padding-left: 1.3em; }
        blockquote { padding-left: 0.7em; border-left: 2px solid currentColor; }
        table { border-collapse: collapse; }
        th, td { border: 1.5px solid currentColor; padding: 0 10px; text-align: left; vertical-align: top; }
        caption { text-align: left; }
        code, pre, kbd { font-family: inherit; }
        mark { background: rgba(255, 214, 0, 0.35); color: inherit; }
        hr { border: 0; border-top: 1.5px solid currentColor; margin: 19px 0 18px; }
        figcaption { font-size: 0.85em; opacity: 0.8; }
        svg { display: block; max-width: 100%; height: auto; max-height: 480px; overflow: visible; }
        </style></head>
        <body>BLOCK</body></html>
        """.Replace("FONTS", HandFonts).Replace("BLOCK", block);

    /// <summary>How big to show a block: as wide as allowed, but never taller than <paramref name="maxHeight"/>.</summary>
    public static (double Width, double Height) Fit(double aspect, double maxWidth, double maxHeight = MaxHeight)
    {
        if (!(aspect > 0)) aspect = 4.0 / 3.0;
        double w = maxWidth, h = w / aspect;
        if (h > maxHeight) (w, h) = (maxHeight * aspect, maxHeight);
        return (w, h);
    }
}
