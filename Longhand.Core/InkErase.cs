using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Longhand.Services;

/// <summary>
/// The model asks to erase the user's marks over one of its blocks once they no longer make sense.
/// <c>Over</c> is the id of the block; the region is a part of it, as fractions (0 to 1) from its top left.
/// </summary>
public sealed record EraseRequest(string Over, double X = 0, double Y = 0, double W = 1, double H = 1);

/// <summary>
/// Reading <c>&lt;erase over="board" x="0.5" y="0" w="0.5" h="1"/&gt;</c> in a reply, and deciding which ink it takes: the strokes that
/// lie mostly inside the region, nothing else. The ink is only taken off the page (see <see cref="PageSession"/>), so undo brings it back.
/// </summary>
public static class InkErase
{
    /// <summary>More than this in one reply are ignored.</summary>
    public const int MaxRequests = 6;

    static readonly Regex Tag = new(@"<erase\b([^<>]*?)/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Unfinished = new(@"<erase\b[^<>]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Attribute = new(@"(\w+)\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.Compiled);

    /// <summary>
    /// The words with the erase tags taken out, and the requests they made. While the reply is still streaming a tag that has
    /// begun but not ended is left out too, so it never shows.
    /// </summary>
    public static string Take(string words, bool finished, List<EraseRequest> found)
    {
        words = Tag.Replace(words, m =>
        {
            if (Read(m.Groups[1].Value) is { } request && found.Count < MaxRequests) found.Add(request);
            return "";
        });
        return finished ? words : Unfinished.Replace(words, "");
    }

    static EraseRequest? Read(string attributes)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Attribute.Matches(attributes)) values[m.Groups[1].Value] = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
        if (!values.TryGetValue("over", out var over) || over.Trim().Length == 0) return null;

        double Number(string key, double fallback) =>
            values.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n)
                ? Math.Clamp(n, 0, 1)
                : fallback;
        double x = Number("x", 0), y = Number("y", 0);
        double w = Number("w", 1 - x), h = Number("h", 1 - y);
        if (x + w > 1) w = 1 - x;
        if (y + h > 1) h = 1 - y;
        return w > 0 && h > 0 ? new EraseRequest(over.Trim(), x, y, w, h) : null;
    }

    /// <summary>The part of a block (its box on the page) a request is about.</summary>
    public static HitRect Region(HitRect block, EraseRequest e) =>
        new(block.Left + block.Width * e.X, block.Top + block.Height * e.Y, block.Left + block.Width * (e.X + e.W), block.Top + block.Height * (e.Y + e.H));

    /// <summary>
    /// Whether a stroke is on a region: most of its points are in it (a few pixels of slop, for the edge of a block). A stroke that
    /// only passes through, or starts elsewhere and ends inside, is not.
    /// </summary>
    public static bool MostlyIn(IReadOnlyList<(double X, double Y)> points, HitRect region, double slop = 6, double share = 0.7) =>
        points.Count > 0 && points.Count(p => region.Contains(p.X, p.Y, slop)) >= Math.Ceiling(points.Count * share);
}
