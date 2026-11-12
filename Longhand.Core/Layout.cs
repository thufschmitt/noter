using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Longhand.Services;

/// <summary>One element (or run of text) of a layout.</summary>
public sealed class LNode
{
    /// <summary>layout, row, col, box, text, rule; or "#text" for words.</summary>
    public string Tag = "";
    public string Text = "";
    public Dictionary<string, string> Attrs = new();
    public List<LNode> Kids = new();
    public LNode? Parent;

    public bool IsText => Tag == "#text";
    public string? Id => Attrs.TryGetValue("id", out var v) ? v : null;
    public bool Has(string attribute) => Attrs.ContainsKey(attribute);
    public double Rot => Attrs.TryGetValue("rot", out var r) && double.TryParse(r, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    public IEnumerable<LNode> Elements => Kids.Where(k => !k.IsText);

    public IEnumerable<LNode> Descendants()
    {
        foreach (var k in Kids)
        {
            yield return k;
            foreach (var d in k.Descendants()) yield return d;
        }
    }

    /// <summary>All the words in it, as one line.</summary>
    public string Words() => Regex.Replace(string.Join(" ", (IsText ? new[] { this } : Descendants().Where(d => d.IsText)).Select(t => t.Text)), @"\s+", " ").Trim();
}

/// <summary>
/// The layout language: what the model writes for anything with structure (a board, a list, a table, a tree) instead of drawing
/// it. A <c>&lt;layout&gt;</c> holds <c>row</c> (its children side by side), <c>col</c> (one under another), <c>box</c> (a bordered
/// card), <c>text</c> and <c>rule</c>; the host lays it out, so there are no sizes or coordinates to write. <c>title</c> gives a
/// layout, column or box a heading. <c>edit</c> on a part lets the user select, move, turn and delete it; <c>drop</c> on a row,
/// column or box lets the user drop such parts into it. The same language tracks what the user did: a move is a change of
/// container and place, a turn is <c>rot</c>, a deletion removes the part.
///
/// Nothing here is HTML: it is parsed by <see cref="Parse"/>, which keeps only these elements and attributes, and rendered by
/// <see cref="Document"/>, which escapes every word. The saved form is the canonical text of <see cref="Serialize"/>.
/// </summary>
public static class Layout
{
    public const int MaxNodes = 300, MaxDepth = 10, MaxText = 300, MaxTitle = 80;

    static readonly HashSet<string> ElementTags = new() { "layout", "row", "col", "box", "text", "rule" };
    static readonly HashSet<string> Containers = new() { "row", "col", "box" };
    /// <summary>Elements that are not part of the language and whose content is not words either: dropped with everything in them.</summary>
    static readonly HashSet<string> DropWithContent = new() { "script", "style", "svg", "iframe", "object", "embed", "template", "noscript", "foreignobject" };
    static readonly string[] AttrOrder = { "id", "title", "timeline", "color", "bg", "start", "end", "edit", "drop", "fit", "rot" };

    /// <summary>
    /// The colour names a layout can use, each with its value on a light page and on a dark one (the page follows the theme, so
    /// "red" stays readable in both). A colour can also be given as <c>#rgb</c> or <c>#rrggbb</c>, which is shown as it is.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (string Light, string Dark)> Palette = new Dictionary<string, (string, string)>
    {
        ["red"] = ("#C62828", "#FF8A80"),
        ["orange"] = ("#D86A00", "#FFB066"),
        ["yellow"] = ("#B58900", "#FFD84D"),
        ["green"] = ("#2E7D32", "#7FD88A"),
        ["teal"] = ("#00796B", "#5CD6C6"),
        ["blue"] = ("#1565C0", "#7DB4FF"),
        ["purple"] = ("#7B1FA2", "#D29BF5"),
        ["pink"] = ("#C2185B", "#FF8FB8"),
        ["brown"] = ("#6D4C41", "#C9A28F"),
        ["gray"] = ("#616161", "#B0B0B0"),
    };
    static readonly Regex HexColor = new(@"^#(?:[0-9a-f]{3}|[0-9a-f]{6})$", RegexOptions.Compiled);

    /// <summary>A palette name or a hex colour in its canonical (lower case) form; null for anything else (nothing that could carry CSS or a URL gets through).</summary>
    static string? NormalizeColor(string value)
    {
        value = value.Trim().ToLowerInvariant();
        if (value == "grey") value = "gray";
        return Palette.ContainsKey(value) || HexColor.IsMatch(value) ? value : null;
    }
    static readonly Regex IdRule = new(@"^[A-Za-z][A-Za-z0-9_-]{0,39}$", RegexOptions.Compiled);

    // ----- time: a column marked timeline places its children by start and end time -----

    /// <summary>A timeline column draws this many pixels to the hour; items snap to this many minutes (the page does, when dragging); an item with no end lasts this long.</summary>
    public const int PxPerHour = 64, SnapMinutes = 15, DefaultMinutes = 60, DayMinutes = 1440;
    static readonly Regex TimeRule = new(@"^(\d{1,2}):(\d{2})$", RegexOptions.Compiled);
    static readonly Regex RangeRule = new(@"^(\d{1,2})(?::(\d{2}))?\s*[-–]\s*(\d{1,2})(?::(\d{2}))?$", RegexOptions.Compiled);

    /// <summary>Minutes since midnight from <c>H:MM</c> or <c>HH:MM</c> (24:00 is the end of the day); false for anything else.</summary>
    public static bool TryMinutes(string? text, out int minutes)
    {
        minutes = 0;
        var m = TimeRule.Match((text ?? "").Trim());
        if (!m.Success) return false;
        int hours = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), mins = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        if (mins > 59 || hours > 24 || (hours == 24 && mins != 0)) return false;
        minutes = hours * 60 + mins;
        return true;
    }

    public static string Hhmm(int minutes) => $"{minutes / 60:00}:{minutes % 60:00}";

    /// <summary><c>8-18</c>, <c>8:30-17</c> or <c>08:00-18:00</c> as <c>08:00-18:00</c>; null when it isn't a range of times.</summary>
    static string? NormalizeRange(string value)
    {
        var m = RangeRule.Match(value.Trim());
        if (!m.Success) return null;
        int Part(int hour, int min) => int.Parse(m.Groups[hour].Value, CultureInfo.InvariantCulture) * 60 + (m.Groups[min].Success ? int.Parse(m.Groups[min].Value, CultureInfo.InvariantCulture) : 0);
        int from = Part(1, 2), to = Part(3, 4);
        if (int.Parse(m.Groups[2].Success ? m.Groups[2].Value : "0", CultureInfo.InvariantCulture) > 59 || int.Parse(m.Groups[4].Success ? m.Groups[4].Value : "0", CultureInfo.InvariantCulture) > 59) return null;
        return from < to && to <= DayMinutes ? Hhmm(from) + "-" + Hhmm(to) : null;
    }

    static (int From, int To)? ParseRange(string? value)
    {
        if (string.IsNullOrEmpty(value) || NormalizeRange(value) is not { } r) return null;
        TryMinutes(r[..5], out int from);
        TryMinutes(r[6..], out int to);
        return (from, to);
    }

    static bool IsTimeline(LNode n) => n.Tag == "col" && n.Has("timeline");

    /// <summary>When an item in a timeline column starts and ends, in minutes since midnight (an item with no end lasts an hour); null when it has no start or isn't in a timeline column.</summary>
    public static (int Start, int End)? TimesOf(LNode item)
    {
        if (item.IsText || item.Parent is not { } parent || !IsTimeline(parent)) return null;
        if (!item.Attrs.TryGetValue("start", out var s) || !TryMinutes(s, out int start) || start >= DayMinutes) return null;
        int end = item.Attrs.TryGetValue("end", out var e) && TryMinutes(e, out int parsed) && parsed > start ? parsed : Math.Min(DayMinutes, start + DefaultMinutes);
        return (start, end);
    }

    /// <summary>
    /// The hours a timeline column shows. Timeline columns side by side (the days of a week) share one range, so their hours line up:
    /// the widest of the ranges given, widened to whole hours to hold every item, or 08:00–18:00 when nothing says.
    /// </summary>
    public static (int From, int To) RangeOf(LNode col)
    {
        var group = col.Parent is { } parent ? parent.Elements.Where(IsTimeline).ToList() : new List<LNode> { col };
        int? from = null, to = null;
        foreach (var c in group)
            if (ParseRange(c.Attrs.GetValueOrDefault("timeline")) is { } r)
            {
                from = Math.Min(from ?? r.From, r.From);
                to = Math.Max(to ?? r.To, r.To);
            }
        foreach (var c in group)
            foreach (var item in c.Elements)
                if (TimesOf(item) is { } t)
                {
                    if (from is null || t.Start < from) from = t.Start / 60 * 60;
                    if (to is null || t.End > to) to = (t.End + 59) / 60 * 60;
                }
        int a = from ?? 8 * 60, b = to ?? 18 * 60;
        if (b - a < 60) { if (a + 60 <= DayMinutes) b = a + 60; else a = b - 60; }
        return (a, b);
    }

    /// <summary>Which side-by-side lane each item takes, and how many lanes share its time: items that overlap split the width.</summary>
    static Dictionary<LNode, (int Lane, int Lanes)> LanesOf(IEnumerable<(LNode Item, int Start, int End)> items)
    {
        var result = new Dictionary<LNode, (int, int)>();
        var cluster = new List<(LNode Item, int Lane)>();
        var laneEnds = new List<int>();
        int clusterEnd = 0;
        void Close()
        {
            foreach (var (item, lane) in cluster) result[item] = (lane, Math.Max(1, laneEnds.Count));
            cluster.Clear();
            laneEnds.Clear();
        }
        foreach (var (item, start, end) in items.OrderBy(i => i.Start).ThenByDescending(i => i.End))
        {
            if (cluster.Count > 0 && start >= clusterEnd) Close();
            int lane = laneEnds.FindIndex(e => e <= start);
            if (lane < 0) { laneEnds.Add(end); lane = laneEnds.Count - 1; } else laneEnds[lane] = end;
            clusterEnd = cluster.Count == 0 ? end : Math.Max(clusterEnd, end);
            cluster.Add((item, lane));
        }
        Close();
        return result;
    }

    // ----- reading it -----

    /// <summary>
    /// The tree of a <c>&lt;layout&gt;</c>; anything that isn't part of the language is dropped (an unknown element is unwrapped:
    /// its words and children stay, except scripts, styles and pictures, which go with their content). Null when there is no layout, or it has more than <see cref="MaxNodes"/> parts or is nested
    /// deeper than <see cref="MaxDepth"/>.
    /// </summary>
    public static LNode? Parse(string raw)
    {
        LNode? root = null;
        var stack = new List<(string Name, LNode Node)>();
        int count = 0, i = 0;
        bool done = false;
        while (i < raw.Length && !done)
        {
            if (raw[i] == '<')
            {
                if (string.CompareOrdinal(raw, i, "<!--", 0, 4) == 0)
                {
                    var e = raw.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    i = e < 0 ? raw.Length : e + 3;
                    continue;
                }
                if (i + 1 < raw.Length && raw[i + 1] == '/')
                {
                    var e = raw.IndexOf('>', i);
                    if (e < 0) break;
                    var closing = raw[(i + 2)..e].Trim().ToLowerInvariant();
                    int at = stack.FindLastIndex(s => s.Name == closing);
                    if (at >= 0)
                    {
                        stack.RemoveRange(at, stack.Count - at);
                        if (stack.Count == 0 && root is not null) done = true;
                    }
                    i = e + 1;
                    continue;
                }
                if (i + 1 < raw.Length && char.IsAsciiLetter(raw[i + 1]))
                {
                    int j = i + 1;
                    while (j < raw.Length && (char.IsAsciiLetterOrDigit(raw[j]) || raw[j] == '-' || raw[j] == ':')) j++;
                    var name = raw[(i + 1)..j].ToLowerInvariant();
                    var attrs = ReadAttributes(raw, ref j, out bool self, out bool closed);
                    i = j;
                    if (!closed) break; // an unfinished tag
                    if (root is null && name != "layout") continue; // nothing counts before the layout starts
                    if (root is not null && stack.Count == 0) { done = true; continue; }

                    var parent = stack.Count > 0 ? stack[^1].Node : null;
                    if (ElementTags.Contains(name) && !(name == "layout" && root is not null))
                    {
                        if (++count > MaxNodes || stack.Count + 1 > MaxDepth) return null;
                        var node = new LNode { Tag = name, Parent = parent };
                        foreach (var (key, value) in attrs) SetAttribute(node, key, value);
                        if (parent is null) root = node; else parent.Kids.Add(node);
                        if (!self && name != "rule") stack.Add((name, node));
                        else if (parent is null) done = true;
                    }
                    else if (!self && DropWithContent.Contains(name))
                    {
                        var end = raw.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
                        var gt = end < 0 ? -1 : raw.IndexOf('>', end);
                        i = gt < 0 ? raw.Length : gt + 1;
                    }
                    else if (!self && parent is not null)
                    {
                        stack.Add((name, parent)); // an unknown element: what is in it goes into its parent
                    }
                    continue;
                }
            }
            int next = raw.IndexOf('<', raw[i] == '<' ? i + 1 : i);
            if (next < 0) next = raw.Length;
            if (stack.Count > 0) AddText(stack[^1].Node, raw[i..next]);
            i = next;
        }
        if (root is null) return null;
        Tidy(root);
        return root;
    }

    static List<(string Name, string Value)> ReadAttributes(string raw, ref int j, out bool self, out bool closed)
    {
        var attrs = new List<(string, string)>();
        self = closed = false;
        while (j < raw.Length)
        {
            while (j < raw.Length && char.IsWhiteSpace(raw[j])) j++;
            if (j >= raw.Length) break;
            if (raw[j] == '>') { j++; closed = true; break; }
            if (raw[j] == '/')
            {
                if (j + 1 < raw.Length && raw[j + 1] == '>') { self = closed = true; j += 2; break; }
                j++;
                continue;
            }
            int start = j;
            while (j < raw.Length && !char.IsWhiteSpace(raw[j]) && raw[j] != '=' && raw[j] != '>' && raw[j] != '/') j++;
            var key = raw[start..j].ToLowerInvariant();
            string value = "";
            int k = j;
            while (k < raw.Length && char.IsWhiteSpace(raw[k])) k++;
            if (k < raw.Length && raw[k] == '=')
            {
                j = k + 1;
                while (j < raw.Length && char.IsWhiteSpace(raw[j])) j++;
                if (j < raw.Length && (raw[j] == '"' || raw[j] == '\''))
                {
                    int end = raw.IndexOf(raw[j], j + 1);
                    if (end < 0) end = raw.Length;
                    value = raw[(j + 1)..end];
                    j = Math.Min(raw.Length, end + 1);
                }
                else
                {
                    int s = j;
                    while (j < raw.Length && !char.IsWhiteSpace(raw[j]) && raw[j] != '>') j++;
                    value = raw[s..j];
                }
            }
            if (key.Length > 0) attrs.Add((key, WebUtility.HtmlDecode(value)));
        }
        return attrs;
    }

    static void SetAttribute(LNode node, string key, string value)
    {
        switch (key)
        {
            case "id":
                if (IdRule.IsMatch(value)) node.Attrs["id"] = value;
                break;
            case "title":
                value = Regex.Replace(value, @"\s+", " ").Trim();
                if (value.Length > 0) node.Attrs["title"] = value.Length > MaxTitle ? value[..MaxTitle] : value;
                break;
            case "color":
            case "bg":
                if (NormalizeColor(value) is { } colour) node.Attrs[key] = colour;
                break;
            case "timeline":
                node.Attrs["timeline"] = NormalizeRange(value) ?? ""; // a bare timeline fits its items
                break;
            case "start":
                if (TryMinutes(value, out int startAt) && startAt < DayMinutes) node.Attrs["start"] = Hhmm(startAt);
                break;
            case "end":
                if (TryMinutes(value, out int endAt) && endAt > 0) node.Attrs["end"] = Hhmm(endAt);
                break;
            case "edit":
            case "drop":
            case "fit":
                node.Attrs[key] = "";
                break;
            case "rot":
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var degrees) && double.IsFinite(degrees)) SetRot(node, degrees);
                break;
        }
    }

    static void SetRot(LNode node, double degrees)
    {
        var turn = Wrap(degrees);
        if (Math.Abs(turn) < 0.05) node.Attrs.Remove("rot");
        else node.Attrs["rot"] = Math.Round(turn, 1).ToString("0.#", CultureInfo.InvariantCulture);
    }

    /// <summary>Degrees brought into -180…180.</summary>
    static double Wrap(double degrees) => ((degrees % 360) + 540) % 360 - 180;

    static void AddText(LNode parent, string raw)
    {
        var text = Regex.Replace(WebUtility.HtmlDecode(raw), @"\s+", " ");
        if (text.Trim().Length == 0 || parent.Tag == "rule") return;
        if (parent.Kids.Count > 0 && parent.Kids[^1].IsText) parent.Kids[^1].Text += text;
        else parent.Kids.Add(new LNode { Tag = "#text", Text = text, Parent = parent });
    }

    /// <summary>Trims the words, drops what is empty, and applies the rules about where each attribute makes sense.</summary>
    static void Tidy(LNode node)
    {
        node.Kids.RemoveAll(k =>
        {
            if (!k.IsText) return false;
            k.Text = k.Text.Trim();
            if (k.Text.Length > MaxText) k.Text = k.Text[..MaxText];
            return k.Text.Length == 0;
        });
        if (node.Parent is null) foreach (var a in new[] { "edit", "drop", "fit", "rot" }) node.Attrs.Remove(a); // the layout as a whole isn't a part to move
        if (!Containers.Contains(node.Tag)) node.Attrs.Remove("drop");
        if (node.Tag is "rule") node.Attrs.Remove("title");
        if (node.Tag != "col") node.Attrs.Remove("timeline");
        if (node.Parent is { } parent && IsTimeline(parent))
        {
            node.Attrs.Remove("rot"); // a slot of a timeline isn't turned
            if (node.Attrs.TryGetValue("start", out var from) && node.Attrs.TryGetValue("end", out var until) && TryMinutes(from, out int a) && TryMinutes(until, out int b) && b <= a) node.Attrs.Remove("end");
            if (!node.Has("start")) node.Attrs.Remove("end");
        }
        else
        {
            node.Attrs.Remove("start"); // times only mean something in a timeline column
            node.Attrs.Remove("end");
        }
        foreach (var k in node.Kids) Tidy(k);
    }

    // ----- making it safe, and giving the parts a name -----

    /// <summary>
    /// The layout in its canonical text. Every part the user can act on gets an id if it has none (<c>item-1</c>, <c>area-1</c>),
    /// and so does every container that holds such a part, so a change can always say where a part was. Null when there is no
    /// layout, or nothing in it.
    /// </summary>
    public static string? Sanitize(string raw)
    {
        var root = Parse(raw);
        if (root is null || root.Kids.Count == 0) return null;
        EnsureIds(root);
        return Serialize(root);
    }

    static void EnsureIds(LNode root)
    {
        var used = new HashSet<string>();
        foreach (var n in new[] { root }.Concat(root.Descendants().Where(d => !d.IsText)))
            if (n.Id is { } id && !used.Add(id)) n.Attrs.Remove("id"); // the first one keeps the name
        int items = 0, areas = 0;
        string Fresh(string prefix, ref int counter)
        {
            string id;
            do id = prefix + ++counter; while (!used.Add(id));
            return id;
        }
        foreach (var n in root.Descendants().Where(d => !d.IsText))
        {
            if (n.Id is not null) continue;
            if (n.Has("edit")) n.Attrs["id"] = Fresh("item-", ref items);
            else if (n.Has("drop") || (Containers.Contains(n.Tag) && n.Elements.Any(k => k.Has("edit")))) n.Attrs["id"] = Fresh("area-", ref areas);
        }
    }

    /// <summary>The id of the layout itself, which names it: a new layout with the same id replaces it. Null when it has none.</summary>
    public static string? FigureId(string markup) => Parse(markup)?.Id;

    /// <summary>True when something in the layout can be edited by the user.</summary>
    public static bool IsEditable(string markup) => Parse(markup) is { } root && root.Descendants().Any(d => d.Has("edit"));

    public static string Title(string markup) => Parse(markup)?.Attrs.GetValueOrDefault("title") is { Length: > 0 } t ? t : "a layout";

    /// <summary>The layout as the model reads it in later turns: as it is now, unless it is very long.</summary>
    public static string Text(string markup, int max = 3000) => markup.Length <= max ? markup : $"[layout{(FigureId(markup) is { } id ? $" id \"{id}\"" : "")}: {Title(markup)}, too long to repeat here]";

    // ----- writing it out -----

    public static string Serialize(LNode root)
    {
        var sb = new StringBuilder();
        Write(root, 0, sb);
        return sb.ToString().TrimEnd();
    }

    static string EscapeText(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    static string EscapeAttribute(string s) => EscapeText(s).Replace("\"", "&quot;");

    static string Attributes(LNode n)
    {
        var sb = new StringBuilder();
        foreach (var key in AttrOrder)
            if (n.Attrs.TryGetValue(key, out var value))
                sb.Append(' ').Append(key).Append(key is "edit" or "drop" or "fit" || (key == "timeline" && value.Length == 0) ? "" : "=\"" + EscapeAttribute(value) + "\"");
        return sb.ToString();
    }

    static void Write(LNode n, int depth, StringBuilder sb)
    {
        var pad = new string(' ', depth * 2);
        if (n.IsText)
        {
            sb.Append(pad).Append(EscapeText(n.Text)).Append('\n');
            return;
        }
        var open = "<" + n.Tag + Attributes(n);
        if (n.Kids.Count == 0)
            sb.Append(pad).Append(open).Append("/>\n");
        else if (n.Kids.All(k => k.IsText))
            sb.Append(pad).Append(open).Append('>').Append(string.Join(" ", n.Kids.Select(k => EscapeText(k.Text)))).Append("</").Append(n.Tag).Append(">\n");
        else
        {
            sb.Append(pad).Append(open).Append(">\n");
            foreach (var k in n.Kids) Write(k, depth + 1, sb);
            sb.Append(pad).Append("</").Append(n.Tag).Append(">\n");
        }
    }

    // ----- changing it -----

    public static LNode? Find(LNode root, string id) =>
        root.Id == id ? root : root.Descendants().FirstOrDefault(d => !d.IsText && d.Id == id);

    /// <summary>
    /// The layout with the user's changes made: a part moved into a container that accepts drops (at a place among that
    /// container's parts), turned, or deleted. Null, and nothing changed, when any change names a part the user can't edit, a
    /// container that doesn't accept drops (or is inside the part), or something that isn't a change.
    /// </summary>
    public static string? ApplyEdits(string markup, IReadOnlyList<DrawingChange> changes)
    {
        var root = Parse(markup);
        if (root is null) return null;
        foreach (var c in changes)
        {
            var node = Find(root, c.Id);
            if (node is null || node == root || !node.Has("edit")) return null;
            switch (c.Op)
            {
                case "delete":
                    node.Parent!.Kids.Remove(node);
                    break;
                case "rotate":
                    if (!double.IsFinite(c.Degrees) || IsTimeline(node.Parent!)) return null; // turning a slot of a timeline makes no sense
                    SetRot(node, node.Rot + c.Degrees);
                    break;
                case "resize":
                    // The end of an item in a timeline column: at least a snap after its start, and not past the end of the day.
                    if (TimesOf(node) is not { } now || !TryMinutes(c.End, out int newEnd)) return null;
                    node.Attrs["end"] = Hhmm(Math.Clamp(newEnd, now.Start + SnapMinutes, DayMinutes));
                    break;
                case "move":
                    var target = c.To is { } to ? Find(root, to) : null;
                    // A timeline column always lets its own items change time; receiving items from elsewhere takes drop.
                    bool own = target is not null && target == node.Parent && IsTimeline(target);
                    if (target is null || target == root || !(target.Has("drop") || own) || target == node || node.Descendants().Contains(target)) return null;
                    if (IsTimeline(target))
                    {
                        // Dropped in a timeline column: it starts at that time (an hour long if it had no length; kept inside the day).
                        if (!TryMinutes(c.At, out int at) || at >= DayMinutes) return null;
                        int length = Math.Clamp(TimesOf(node) is { } was ? was.End - was.Start : DefaultMinutes, SnapMinutes, DayMinutes);
                        int start = Math.Clamp(at, 0, DayMinutes - length);
                        if (node.Parent != target)
                        {
                            node.Parent!.Kids.Remove(node);
                            node.Parent = target;
                            target.Kids.Add(node);
                        }
                        node.Attrs.Remove("rot");
                        node.Attrs["start"] = Hhmm(start);
                        node.Attrs["end"] = Hhmm(start + length);
                        break;
                    }
                    node.Parent!.Kids.Remove(node);
                    node.Parent = target;
                    node.Attrs.Remove("start"); // out of a timeline, it has no time
                    node.Attrs.Remove("end");
                    var elements = target.Elements.ToList();
                    int index = Math.Clamp(c.Index, 0, elements.Count);
                    if (index >= elements.Count) target.Kids.Add(node);
                    else target.Kids.Insert(target.Kids.IndexOf(elements[index]), node);
                    break;
                default:
                    return null;
            }
        }
        return Serialize(root);
    }

    // ----- telling what the user did -----

    /// <summary>Where a part was before the user's first change to it, and whether it has been deleted: what the report compares with.</summary>
    public sealed class EditEntry
    {
        /// <summary>The id of its container (or of the layout, or "top"), and its place there, counting from 1.</summary>
        public string? Parent;
        public int Position;
        public double Rot;
        public bool Deleted;
        /// <summary>When it was, in minutes since midnight, if it was in a timeline column (with a start).</summary>
        public int? Start, End;
        /// <summary>Its words, so the report can say which one it means.</summary>
        public string Label = "";

        public EditEntry Copy() => (EditEntry)MemberwiseClone();
    }

    static (string Parent, int Position, int Count) PlaceOf(LNode root, LNode node)
    {
        var parent = node.Parent!;
        var siblings = parent.Elements.ToList();
        return (parent == root ? root.Id ?? "top" : parent.Id ?? "?", siblings.IndexOf(node) + 1, siblings.Count);
    }

    /// <summary>What the part looks like now, to compare with later; null when there is no such part.</summary>
    public static EditEntry? Capture(string markup, string id)
    {
        var root = Parse(markup);
        var node = root is null ? null : Find(root, id);
        if (root is null || node is null || node == root) return null;
        var (parent, position, _) = PlaceOf(root, node);
        var label = node.Words();
        var times = TimesOf(node);
        return new EditEntry { Parent = parent, Position = position, Rot = node.Rot, Start = times?.Start, End = times?.End, Label = label.Length > 40 ? label[..40] : label };
    }

    static string Span(int start, int end) => Hhmm(start) + "–" + Hhmm(end);

    static List<string> Describe(string markup, IReadOnlyDictionary<string, EditEntry> edits)
    {
        var lines = new List<string>();
        var root = Parse(markup);
        if (root is null) return lines;
        foreach (var (id, was) in edits.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var name = was.Label.Length > 0 ? $"{id} \"{was.Label}\"" : id;
            bool wasTimed = was.Start is not null && was.End is not null;
            var wasSpan = wasTimed ? Span(was.Start!.Value, was.End!.Value) : "";
            var from = wasTimed ? $"#{was.Parent} {wasSpan}" : $"#{was.Parent} (position {was.Position})";
            if (was.Deleted)
            {
                lines.Add($"- {name}: deleted (it was in {from})");
                continue;
            }
            if (Find(root, id) is not { } node || node == root) continue;
            var (parent, position, count) = PlaceOf(root, node);
            var times = TimesOf(node);
            var what = new List<string>();
            if (parent != was.Parent)
                what.Add($"moved from {from} to " + (times is { } t ? $"#{parent} {Span(t.Start, t.End)}" : $"#{parent} (position {position} of {count})"));
            else if (wasTimed && times is { } now)
            {
                if (now.Start != was.Start) what.Add($"rescheduled from {wasSpan} to {Span(now.Start, now.End)}");
                else if (now.End != was.End) what.Add($"resized from {wasSpan} to {Span(now.Start, now.End)}");
            }
            else if (times is { } scheduled) what.Add($"scheduled at {Span(scheduled.Start, scheduled.End)} (it had no time)");
            else if (position != was.Position) what.Add($"moved within #{parent} from position {was.Position} to position {position} (of {count})");
            var turn = Wrap(node.Rot - was.Rot);
            if (Math.Abs(turn) > 0.5) what.Add($"turned {Math.Round(Math.Abs(turn))}° {(turn > 0 ? "clockwise" : "anticlockwise")}");
            if (what.Count > 0) lines.Add($"- {name}: {string.Join(", ", what)}");
        }
        return lines;
    }

    /// <summary>One line per part the user changed: where it was and where it is, how far it was turned, or that it was deleted. Null when nothing is left that differs (a part moved away and back).</summary>
    public static string? DescribeEdits(string markup, IReadOnlyDictionary<string, EditEntry> edits) =>
        Describe(markup, edits) is { Count: > 0 } lines ? string.Join("\n", lines) : null;

    public static bool HasNetEdits(string markup, IReadOnlyDictionary<string, EditEntry> edits) => edits.Count > 0 && Describe(markup, edits).Count > 0;

    // ----- showing it -----

    static string Encode(string s) => WebUtility.HtmlEncode(s);

    static string Num(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);

    /// <param name="placed">A style that puts the part where its time says (an item in a timeline column).</param>
    static void Render(LNode n, StringBuilder sb, string? placed = null)
    {
        if (n.IsText)
        {
            sb.Append(Encode(n.Text));
            return;
        }
        bool timeline = IsTimeline(n);
        bool axis = timeline && n.Parent is { } group && group.Elements.First(IsTimeline) == n;
        var times = TimesOf(n);
        sb.Append("<div class=\"n ").Append(n.Tag);
        if (n.Has("edit")) sb.Append(" edit");
        if (n.Has("drop")) sb.Append(" drop");
        if (n.Has("fit")) sb.Append(" fit");
        if (axis) sb.Append(" tl-axis"); // the first of the days shows the hours
        // A palette colour is a class (which follows the theme); a hex colour is a style. Both were checked when the layout was read.
        var styles = new List<string>();
        if (n.Attrs.TryGetValue("color", out var color)) { if (Palette.ContainsKey(color)) sb.Append(" fg-").Append(color); else styles.Add("color:" + color); }
        if (n.Attrs.TryGetValue("bg", out var bg)) { if (Palette.ContainsKey(bg)) sb.Append(" bg-").Append(bg); else styles.Add("background:color-mix(in srgb," + bg + " 22%,transparent)"); }
        if (n.Rot != 0) styles.Add("transform:rotate(" + n.Rot.ToString("0.#", CultureInfo.InvariantCulture) + "deg)");
        if (placed is not null) styles.Add(placed);
        sb.Append('"');
        if (n.Id is { } id) sb.Append(" id=\"").Append(Encode(id)).Append('"');
        if (n.Has("edit")) sb.Append(" data-edit");
        if (n.Has("drop")) sb.Append(" data-drop");
        if (timeline) sb.Append(" data-tl");
        if (times is { } t) sb.Append(" data-start=\"").Append(t.Start).Append("\" data-end=\"").Append(t.End).Append('"');
        if (n.Rot != 0) sb.Append(" data-rot=\"").Append(n.Rot.ToString("0.#", CultureInfo.InvariantCulture)).Append('"');
        if (styles.Count > 0) sb.Append(" style=\"").Append(string.Join(";", styles)).Append('"');
        sb.Append('>');
        if (n.Attrs.TryGetValue("title", out var title)) sb.Append("<div class=\"title\">").Append(Encode(title)).Append("</div>");
        if (timeline) RenderTimeline(n, sb, axis);
        else foreach (var k in n.Kids) Render(k, sb);
        sb.Append("</div>");
    }

    /// <summary>
    /// A timeline column: what has no time first, then the hours, with each timed item at its start, as tall as it is long, and side by
    /// side with the items it overlaps. The browser lays out the rest, so this needs no measuring. The grid carries what the page needs to
    /// turn a drop position back into a time (<c>data-from</c> in minutes, <c>data-ph</c> pixels to the hour).
    /// </summary>
    static void RenderTimeline(LNode col, StringBuilder sb, bool axis)
    {
        var (from, to) = RangeOf(col);
        double px = PxPerHour / 60.0;
        foreach (var k in col.Kids.Where(k => TimesOf(k) is null)) Render(k, sb);
        var timed = col.Elements.Select(k => (Item: k, Times: TimesOf(k))).Where(x => x.Times is not null).Select(x => (x.Item, x.Times!.Value.Start, x.Times.Value.End)).ToList();
        var lanes = LanesOf(timed);
        sb.Append("<div class=\"tl\" data-from=\"").Append(from).Append("\" data-to=\"").Append(to).Append("\" data-ph=\"").Append(PxPerHour)
            .Append("\" style=\"height:").Append(Num((to - from) * px)).Append("px\">");
        if (axis)
            for (int h = (from + 59) / 60 * 60; h <= to; h += 60)
                sb.Append("<div class=\"tl-label\" style=\"top:").Append(Num((h - from) * px)).Append("px\">").Append(Hhmm(h)).Append("</div>");
        foreach (var (item, start, end) in timed)
        {
            var (lane, count) = lanes[item];
            Render(item, sb, $"top:{Num((start - from) * px)}px;height:{Num(Math.Max(14, (end - start) * px - 2))}px;left:calc({Num(100.0 * lane / count)}% + 1px);width:calc({Num(100.0 / count)}% - 3px)");
        }
        sb.Append("</div>");
    }

    /// <summary>
    /// The page a layout is rendered from (by the browser, which does the laying out): the handwriting font and the ink colour on a
    /// clear background, and a content policy that lets nothing run or load. <see cref="Blocks.InkToken"/> stands for the ink colour.
    /// </summary>
    public static string Document(string markup)
    {
        var body = new StringBuilder();
        if (Parse(markup) is { } root) Render(root, body);
        return """
            <!doctype html>
            <html><head><meta charset="utf-8">
            <meta name="color-scheme" content="light dark">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src data:">
            <style>
            html, body { margin: 0; padding: 0; background: transparent; }
            body { color: %INK%; font: 24px/32px FONTS; overflow: hidden; -webkit-font-smoothing: antialiased; -webkit-user-select: none; user-select: none; }
            .layout { padding: 3px; }
            .layout > .title { font-weight: bold; font-size: 1.1em; margin: 0 0 8px; }
            .row { display: flex; flex-direction: row; gap: 12px; align-items: stretch; }
            .row > .n { flex: 1 1 0; min-width: 0; }
            .row > .n.fit { flex: 0 0 auto; }
            .col, .box { display: flex; flex-direction: column; gap: 8px; min-width: 0; overflow-wrap: anywhere; }
            .title { font-weight: bold; }
            .box { border: 2px solid currentColor; border-radius: 8px; padding: 4px 12px; gap: 6px; }
            .col.drop, .row.drop { border: 1.5px solid color-mix(in srgb, currentColor 45%, transparent); border-radius: 10px; padding: 8px; }
            .box.drop { border-style: dashed; }
            .drop { min-height: 56px; }
            .rule { border-top: 1.5px solid currentColor; opacity: .6; margin: 6px 0; }
            .text { overflow-wrap: anywhere; }
            .col.tl-axis { padding-left: 52px; }
            .tl { position: relative; flex: none; background-image: repeating-linear-gradient(to bottom, color-mix(in srgb, currentColor 30%, transparent) 0 1px, transparent 1px 64px), repeating-linear-gradient(to bottom, color-mix(in srgb, currentColor 12%, transparent) 0 1px, transparent 1px 32px); }
            .tl-label { position: absolute; left: -50px; width: 44px; margin-top: -7px; text-align: right; font-size: 13px; line-height: 14px; opacity: .6; }
            .tl > .box { position: absolute; box-sizing: border-box; padding: 0 6px; border-width: 1.5px; border-radius: 6px; gap: 0; overflow: hidden; font-size: 18px; line-height: 20px; }
            :where(.tl) > .box { background: Canvas; }
            PALETTE
            </style></head>
            <body>BLOCK</body></html>
            """.Replace("FONTS", Blocks.HandFonts).Replace("PALETTE", PaletteCss()).Replace("BLOCK", body.ToString());
    }

    /// <summary>The classes for the palette colours: the light values, and the dark ones when the page is dark. A background is a soft tint, so the words on it stay readable.</summary>
    static string PaletteCss()
    {
        string Rules(Func<(string Light, string Dark), string> pick) =>
            string.Concat(Palette.Select(p => $".fg-{p.Key}{{color:{pick(p.Value)}}}.bg-{p.Key}{{background:color-mix(in srgb,{pick(p.Value)} 22%,transparent)}}"));
        return Rules(v => v.Light) + "@media (prefers-color-scheme: dark){" + Rules(v => v.Dark) + "}";
    }
}
