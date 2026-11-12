using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Longhand.Services;

/// <summary>One thing the user did to a part of a layout: moved it into a container, turned it, or deleted it.</summary>
/// <param name="Op">"move", "rotate" or "delete".</param>
/// <param name="To">The id of the container it was dropped into, for a move.</param>
/// <param name="Index">Its place among the other parts of that container (0 is first), not counting the part itself, for a move.</param>
/// <param name="Degrees">How far it was turned (clockwise is positive), for a turn.</param>
public sealed record DrawingChange(string Id, string Op, string? To = null, int Index = 0, double Degrees = 0);

/// <summary>
/// The blocks the model can put under its words: a <c>&lt;layout&gt;</c> (a board, a list, a table: structure, which the user can
/// edit) or an inline <c>&lt;svg&gt;</c> (a picture, which is static). This finds them in a reply and passes the rest on to the
/// right one.
/// </summary>
public static class Blocks
{
    public const int MaxBytes = 100_000;
    public const int MaxBlocks = 3;
    /// <summary>A block taller than this, rendered at the page's text width, is scaled down to fit.</summary>
    public const double MaxHeight = 1200;
    /// <summary>Stands for the ink colour in <see cref="Document"/>; the app puts in its own, which follows the theme.</summary>
    public const string InkToken = "%INK%";
    /// <summary>The handwriting fonts, best first: the same list the page's own words use.</summary>
    public const string HandFonts = "\"Ink Free\", \"Segoe Print\", \"Bradley Hand\", \"Caveat\", \"Comic Neue\", \"Comic Sans MS\", \"Chalkboard SE\", cursive";

    static readonly Regex Opening = new(@"<(svg|layout)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex FenceBefore = new(@"```[ \t]*(?:html|svg|xml)?[ \t]*\r?\n?\s*(?=<(?:svg|layout)\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex FenceAfter = new(@"(?<=</(?:svg|layout)>)\s*```", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Drawings used to sit in a <figure>: the wrapper goes, the drawing in it stays.
    static readonly Regex Figure = new(@"</?figure\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <param name="Text">The reply with the blocks taken out, words in their original order.</param>
    /// <param name="Blocks">The complete <c>&lt;layout&gt;…&lt;/layout&gt;</c> and <c>&lt;svg&gt;…&lt;/svg&gt;</c> elements, in order.</param>
    /// <param name="Open">True while a block has started but not ended, as when the reply is still streaming.</param>
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
        // While streaming, the start of "<svg" or "<layout" at the very end may begin a block: don't show it yet.
        if (!finished && !open)
            for (int len = Math.Min(7, words.Length); len >= 1; len--)
            {
                var tail = words[^len..];
                if ("<svg".StartsWith(tail, StringComparison.OrdinalIgnoreCase) || "<layout".StartsWith(tail, StringComparison.OrdinalIgnoreCase) || "<figure".StartsWith(tail, StringComparison.OrdinalIgnoreCase))
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

    public static bool IsLayout(string markup) => markup.AsSpan().TrimStart().StartsWith("<layout", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The block made safe to show, in its canonical form (a layout is written out again, an SVG is cleaned). Null when it is too
    /// big, isn't a layout or an SVG, or nothing is left of it.
    /// </summary>
    public static string? Sanitize(string raw)
    {
        if (Encoding.UTF8.GetByteCount(raw) > MaxBytes) return null;
        raw = Figure.Replace(raw.Trim(), "").Trim();
        return IsLayout(raw) ? Layout.Sanitize(raw) : Svg.Sanitize(raw);
    }

    /// <summary>The id of the block's root element, which names it: a new block with the same id replaces it. Null when it has none.</summary>
    public static string? FigureId(string markup) => IsLayout(markup) ? Layout.FigureId(markup) : Svg.FigureId(markup);

    /// <summary>A short description, for the page's accessibility and the history.</summary>
    public static string Title(string markup) => IsLayout(markup) ? Layout.Title(markup) : Svg.Title(markup);

    /// <summary>The block as the model reads it in later turns: a layout as it is now (it is short), a picture as a stand-in.</summary>
    public static string Text(string markup) => IsLayout(markup) ? Layout.Text(markup) : Svg.Text(markup);

    /// <summary>True when the user can select, move, turn or delete something in the block.</summary>
    public static bool IsEditable(string markup) => IsLayout(markup) && Layout.IsEditable(markup);

    /// <summary>The page a block is rendered from. <see cref="InkToken"/> stands for the ink colour.</summary>
    public static string Document(string markup) => IsLayout(markup) ? Layout.Document(markup) : Svg.Document(markup);

    /// <summary>How big to show a block: as wide as allowed, but never taller than <paramref name="maxHeight"/>.</summary>
    public static (double Width, double Height) Fit(double aspect, double maxWidth, double maxHeight = MaxHeight)
    {
        if (!(aspect > 0)) aspect = 4.0 / 3.0;
        double w = maxWidth, h = w / aspect;
        if (h > maxHeight) (w, h) = (maxHeight * aspect, maxHeight);
        return (w, h);
    }
}
