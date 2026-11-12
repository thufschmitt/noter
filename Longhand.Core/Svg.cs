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
/// Pictures: the inline SVG the model draws for a diagram, sketch, chart or map. They are static (anything the user can edit is a
/// <see cref="Layout"/>). This makes them safe (no scripts, nothing fetched, no links), gives the stand-in the model reads in later
/// turns, and builds the document both apps render them from. <see cref="Blocks"/> finds them in a reply.
/// </summary>
public static class Svg
{
    static readonly string[] Tags =
    {
        "svg", "g", "path", "rect", "circle", "ellipse", "line", "polyline", "polygon", "text", "tspan", "title", "desc", "defs",
        "marker", "linearGradient", "radialGradient", "stop", "clipPath", "mask", "pattern", "symbol",
    };
    static readonly string[] Attributes =
    {
        "class", "id", "style", "title", "aria-label", "role", "lang", "dir",
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

    /// <summary>The drawing made safe to show: only SVG is kept. Null when it is too big, isn't SVG, or nothing is left of it.</summary>
    public static string? Sanitize(string raw)
    {
        if (Encoding.UTF8.GetByteCount(raw) > Blocks.MaxBytes) return null;
        raw = raw.Trim();
        if (!raw.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)) return null;
        var clean = (_sanitizer ??= NewSanitizer()).Sanitize(raw).Trim();
        var svg = ParseSvg(clean);
        if (svg is null || svg.Children.Length == 0) return null;
        svg.SetAttribute("xmlns", "http://www.w3.org/2000/svg");
        return svg.OuterHtml;
    }

    /// <summary>The id of the drawing's root element, which names the drawing: a new one with the same id replaces it. Null when it has none.</summary>
    public static string? FigureId(string markup) => ParseSvg(markup)?.GetAttribute("id") is { Length: > 0 } id ? id : null;

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

    /// <summary>The drawing as the history shows it to the model later: its title, and its id if it has one.</summary>
    public static string Text(string markup)
    {
        var id = FigureId(markup);
        var title = Title(markup);
        var parts = new List<string>();
        if (id is not null) parts.Add($"id \"{id}\"");
        if (title != "a drawing") parts.Add(title);
        return "[drawing" + (parts.Count > 0 ? ": " + string.Join(", ", parts) : "") + "]";
    }

    /// <summary>
    /// The page a drawing is rendered from: the handwriting font and the ink colour on a clear background, and a content policy
    /// that lets nothing run or load. <see cref="Blocks.InkToken"/> stands for the ink colour.
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
        """.Replace("FONTS", Blocks.HandFonts).Replace("BLOCK", markup);
}
