using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Longhand.Services;

/// <summary>
/// One occurrence of an event. <c>Start</c> and <c>End</c> are instants for timed events; all-day events use whole dates (midnight
/// UTC, end exclusive). <c>Id</c> names the event on the server (empty when it isn't known); <c>Repeats</c> is true for an
/// occurrence of a repeating event.
/// </summary>
public sealed record CalendarEvent(string Calendar, string Title, DateTimeOffset Start, DateTimeOffset End, bool AllDay, string Location, string Description, string Id = "", bool Repeats = false);

/// <summary>When an event is: instants for a timed one; for an all-day one, the dates of <c>Start</c> and <c>End</c> (end exclusive).</summary>
public sealed record EventWhen(DateTimeOffset Start, DateTimeOffset End, bool AllDay);

/// <summary>What to change in an event. Null leaves that part as it is; an empty location or description removes it.</summary>
public sealed record EventChange(string? Title = null, EventWhen? When = null, string? Location = null, string? Description = null);

/// <summary>Reads the few iCalendar (RFC 5545) properties needed to list events, and writes the events Longhand makes or changes.</summary>
public static class ICalendar
{
    /// <param name="floatingZone">The zone for times written without one (floating times).</param>
    public static List<CalendarEvent> ParseEvents(string ics, string calendar, TimeZoneInfo floatingZone)
    {
        var events = new List<CalendarEvent>();
        Dictionary<string, (string Value, Dictionary<string, string> Params)>? props = null;
        int depth = 0;

        foreach (var line in Unfold(ics))
        {
            var (name, parameters, value) = SplitLine(line);
            if (name.Length == 0) continue;

            if (name == "BEGIN")
            {
                if (value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase) && depth == 0) props = new();
                else if (props is not null) depth++; // an alarm inside the event
                continue;
            }
            if (name == "END")
            {
                if (value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase) && depth == 0)
                {
                    if (props is not null && Build(props, calendar, floatingZone) is { } built) events.Add(built);
                    props = null;
                }
                else if (props is not null && depth > 0) depth--;
                continue;
            }
            if (props is not null && depth == 0) props[name] = (value, parameters);
        }
        return events;
    }

    static CalendarEvent? Build(Dictionary<string, (string Value, Dictionary<string, string> Params)> p, string calendar, TimeZoneInfo floatingZone)
    {
        if (!p.TryGetValue("DTSTART", out var start)) return null;
        if (p.TryGetValue("STATUS", out var status) && status.Value.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase)) return null;

        var (from, allDay) = ParseTime(start.Value, start.Params, floatingZone);
        if (from is null) return null;

        DateTimeOffset to;
        if (p.TryGetValue("DTEND", out var end) && ParseTime(end.Value, end.Params, floatingZone).Time is { } parsedEnd) to = parsedEnd;
        else if (p.TryGetValue("DURATION", out var duration) && ParseDuration(duration.Value) is { } span) to = from.Value + span;
        else to = allDay ? from.Value.AddDays(1) : from.Value;

        string Text(string key) => p.TryGetValue(key, out var v) ? Unescape(v.Value) : "";
        var title = Text("SUMMARY");
        bool repeats = p.ContainsKey("RRULE") || p.ContainsKey("RDATE") || p.ContainsKey("RECURRENCE-ID");
        return new CalendarEvent(calendar, title.Length > 0 ? title : "(no title)", from.Value, to, allDay, Text("LOCATION"), Text("DESCRIPTION"), Repeats: repeats);
    }

    /// <summary>Handles UTC ("...Z"), zoned (TZID=...), floating and date-only (VALUE=DATE) values.</summary>
    static (DateTimeOffset? Time, bool AllDay) ParseTime(string value, Dictionary<string, string> parameters, TimeZoneInfo floatingZone)
    {
        value = value.Trim();
        bool dateOnly = (parameters.TryGetValue("VALUE", out var kind) && kind.Equals("DATE", StringComparison.OrdinalIgnoreCase)) || value.Length == 8;
        if (dateOnly)
        {
            if (!DateTime.TryParseExact(value[..Math.Min(8, value.Length)], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) return (null, true);
            // All-day dates are calendar dates, not instants: keep them as midnight UTC so the date never shifts.
            return (new DateTimeOffset(day, TimeSpan.Zero), true);
        }

        bool utc = value.EndsWith("Z", StringComparison.OrdinalIgnoreCase);
        var text = value.TrimEnd('Z', 'z');
        if (!DateTime.TryParseExact(text, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) return (null, false);
        if (utc) return (new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Utc)), false);

        var zone = floatingZone;
        if (parameters.TryGetValue("TZID", out var tzid))
        {
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(tzid.Trim('"')); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { /* unknown zone: read it as local time */ }
        }
        var offset = zone.GetUtcOffset(local);
        return (new DateTimeOffset(local, offset), false);
    }

    static TimeSpan? ParseDuration(string value)
    {
        var m = Regex.Match(value, @"^([+-])?P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$");
        if (!m.Success) return null;
        int Part(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) : 0;
        var span = TimeSpan.FromDays(Part(2) * 7 + Part(3)) + TimeSpan.FromHours(Part(4)) + TimeSpan.FromMinutes(Part(5)) + TimeSpan.FromSeconds(Part(6));
        return m.Groups[1].Value == "-" ? -span : span;
    }

    /// <summary>Joins folded lines: a line that starts with a space or tab continues the one before.</summary>
    static IEnumerable<string> Unfold(string ics)
    {
        var current = new StringBuilder();
        // Lines end in CRLF; a stray CR left by another line-ending conversion is dropped too.
        foreach (var line in ics.Split('\n'))
        {
            var raw = line.TrimEnd('\r');
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t')) { current.Append(raw, 1, raw.Length - 1); continue; }
            if (current.Length > 0) yield return current.ToString();
            current.Clear().Append(raw);
        }
        if (current.Length > 0) yield return current.ToString();
    }

    /// <summary>"DTSTART;TZID=Europe/Paris:20261110T140000" becomes its name, parameters and value.</summary>
    static (string Name, Dictionary<string, string> Params, string Value) SplitLine(string line)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // The first colon outside quotes ends the name and parameters.
        int colon = -1;
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == ':' && !quoted) { colon = i; break; }
        }
        if (colon < 0) return ("", parameters, "");

        var head = line[..colon].Split(';');
        for (int i = 1; i < head.Length; i++)
        {
            var eq = head[i].IndexOf('=');
            if (eq > 0) parameters[head[i][..eq]] = head[i][(eq + 1)..];
        }
        return (head[0].ToUpperInvariant(), parameters, line[(colon + 1)..]);
    }

    // ---------- writing ----------

    /// <summary>A calendar holding one new event.</summary>
    public static string NewEvent(string uid, string title, EventWhen when, string? location, string? description, DateTimeOffset now)
    {
        var lines = new List<string> { "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//Longhand//Longhand//EN", "BEGIN:VEVENT", "UID:" + uid, "DTSTAMP:" + Utc(now), "CREATED:" + Utc(now) };
        lines.AddRange(Times(when));
        lines.Add("SUMMARY:" + Escape(title));
        if (!string.IsNullOrWhiteSpace(location)) lines.Add("LOCATION:" + Escape(location));
        if (!string.IsNullOrWhiteSpace(description)) lines.Add("DESCRIPTION:" + Escape(description));
        lines.AddRange(new[] { "END:VEVENT", "END:VCALENDAR" });
        return Join(lines);
    }

    /// <summary>
    /// The calendar with its one event changed, everything else in it kept as it was (alarms, attendees, categories…). Null,
    /// with the reason, for a repeating event or anything else that isn't one plain event.
    /// </summary>
    public static string? Change(string ics, EventChange change, DateTimeOffset now, out string? problem)
    {
        problem = null;
        var lines = Unfold(ics).ToList();
        var events = lines.Count(l => SplitLine(l) is ("BEGIN", _, var v) && v.Equals("VEVENT", StringComparison.OrdinalIgnoreCase));
        if (events != 1)
        {
            problem = events == 0 ? "That isn't an event." : "That is a repeating event; Longhand can't change repeating events.";
            return null;
        }

        var replaced = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DTSTAMP", "LAST-MODIFIED", "SEQUENCE" };
        if (change.Title is not null) replaced.Add("SUMMARY");
        if (change.Location is not null) replaced.Add("LOCATION");
        if (change.Description is not null) replaced.Add("DESCRIPTION");
        if (change.When is not null) replaced.UnionWith(new[] { "DTSTART", "DTEND", "DURATION" });

        var result = new List<string>();
        int sequence = 0, depth = 0;
        bool inEvent = false;
        foreach (var line in lines)
        {
            var (name, _, value) = SplitLine(line);
            if (name == "BEGIN" && value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase)) { inEvent = true; result.Add(line); continue; }
            if (inEvent && name == "BEGIN") depth++;
            if (inEvent && depth == 0)
            {
                if (name is "RRULE" or "RDATE" or "RECURRENCE-ID")
                {
                    problem = "That is a repeating event; Longhand can't change repeating events.";
                    return null;
                }
                if (name == "SEQUENCE" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) sequence = n;
                if (name == "END" && value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add("DTSTAMP:" + Utc(now));
                    result.Add("LAST-MODIFIED:" + Utc(now));
                    result.Add("SEQUENCE:" + (sequence + 1).ToString(CultureInfo.InvariantCulture));
                    if (change.When is { } when) result.AddRange(Times(when));
                    if (change.Title is not null) result.Add("SUMMARY:" + Escape(change.Title));
                    if (!string.IsNullOrWhiteSpace(change.Location)) result.Add("LOCATION:" + Escape(change.Location));
                    if (!string.IsNullOrWhiteSpace(change.Description)) result.Add("DESCRIPTION:" + Escape(change.Description));
                    result.Add(line);
                    inEvent = false;
                    continue;
                }
                if (replaced.Contains(name)) continue;
            }
            if (inEvent && name == "END" && depth > 0) depth--;
            result.Add(line);
        }
        return Join(result);
    }

    static IEnumerable<string> Times(EventWhen when) => when.AllDay
        ? new[] { "DTSTART;VALUE=DATE:" + Date(when.Start), "DTEND;VALUE=DATE:" + Date(when.End) }
        : new[] { "DTSTART:" + Utc(when.Start), "DTEND:" + Utc(when.End) };

    static string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
    static string Date(DateTimeOffset t) => t.DateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    static string Escape(string text) =>
        text.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\\n");

    /// <summary>CRLF line ends, and lines longer than 75 bytes folded, as RFC 5545 asks.</summary>
    static string Join(IEnumerable<string> lines)
    {
        var text = new StringBuilder();
        foreach (var line in lines)
        {
            int bytes = 0;
            var e = StringInfo.GetTextElementEnumerator(line);
            while (e.MoveNext())
            {
                var piece = e.GetTextElement();
                int size = Encoding.UTF8.GetByteCount(piece);
                if (bytes + size > 75)
                {
                    text.Append("\r\n ");
                    bytes = 1;
                }
                text.Append(piece);
                bytes += size;
            }
            text.Append("\r\n");
        }
        return text.ToString();
    }

    static string Unescape(string value) =>
        Regex.Replace(value, @"\\([nN,;\\])", m => m.Groups[1].Value is "n" or "N" ? "\n" : m.Groups[1].Value);
}
