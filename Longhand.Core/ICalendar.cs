using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Longhand.Services;

/// <summary>One occurrence of an event. <c>Start</c> and <c>End</c> are instants for timed events; all-day events use whole dates.</summary>
public sealed record CalendarEvent(string Calendar, string Title, DateTimeOffset Start, DateTimeOffset End, bool AllDay, string Location, string Description);

/// <summary>Reads the few iCalendar (RFC 5545) properties needed to list events.</summary>
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
        return new CalendarEvent(calendar, title.Length > 0 ? title : "(no title)", from.Value, to, allDay, Text("LOCATION"), Text("DESCRIPTION"));
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

    static string Unescape(string value) =>
        Regex.Replace(value, @"\\([nN,;\\])", m => m.Groups[1].Value is "n" or "N" ? "\n" : m.Groups[1].Value);
}
