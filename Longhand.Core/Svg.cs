using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

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
