using System.Text.RegularExpressions;

namespace Longhand.Services;

/// <summary>
/// Claude replies in the shape:
///   HEARD: one-line transcription of the user's ink
///   ---
///   the reply that is written back on the page
/// The HEARD line feeds the conversation history; only the part after the separator is shown.
/// </summary>
public static class ReplyParser
{
    static readonly Regex Full = new(@"^\s*HEARD:\s*([^\n]*)\n([\s\S]*?)(?:^|\n)\s*-{3,}\s*\n?([\s\S]*)$", RegexOptions.Multiline);
    static readonly Regex HeardOnly = new(@"^\s*HEARD:\s*([^\n]*)\n?([\s\S]*)$");

    public readonly record struct Parsed(bool Ready, string Heard, string Body);

    /// <param name="finished">True once the stream has ended: then a reply that skipped the format is shown as-is.</param>
    public static Parsed Parse(string full, bool finished)
    {
        var m = Full.Match(full);
        if (m.Success)
        {
            var heard = (m.Groups[1].Value + (m.Groups[2].Value.Length > 0 ? " " + m.Groups[2].Value : "")).Trim();
            return new Parsed(true, heard, Clean(m.Groups[3].Value.TrimStart()));
        }
        if (!finished) return new Parsed(false, "", "");
        var h = HeardOnly.Match(full);
        if (h.Success)
            return new Parsed(true, h.Groups[1].Value.Trim(), Clean(Regex.Replace(h.Groups[2].Value, @"^\s*-{3,}\s*", "").Trim()));
        return new Parsed(true, "", Clean(full.Trim()));
    }

    /// <summary>Strips markdown the handwriting font can't show.</summary>
    public static string Clean(string text)
    {
        text = Regex.Replace(text, @"\*\*|__|`{1,3}", "");
        text = Regex.Replace(text, @"^#+\s*", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^\s*[*•]\s+", "– ", RegexOptions.Multiline);
        return text;
    }
}
