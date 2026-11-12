using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Longhand.Services;

/// <summary>Turns events into the short agenda that is handed to Claude.</summary>
public static class CalendarText
{
    const int MaxEvents = 80;
    const int MaxDescription = 160;

    /// <summary>
    /// A reminder placed after an agenda, where the model reads it just before it writes its reply: events are shown on a timeline,
    /// not listed in words. Nothing is added when there were no events.
    /// </summary>
    public static string WithTimelineHint(string agenda) => agenda.EndsWith("None found.", StringComparison.Ordinal)
        ? agenda
        : agenda + "\n\n(If you show these events to the user, put them on a timeline: one timeline col per day, each event a box with its time of day as start and end in HH:MM. Don't list them in words.)";

    public static string Agenda(IReadOnlyList<CalendarEvent> events, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone, string? search)
    {
        var culture = CultureInfo.InvariantCulture;
        if (!string.IsNullOrWhiteSpace(search))
            events = events.Where(e => Contains(e.Title, search) || Contains(e.Location, search) || Contains(e.Description, search)).ToList();

        Func<DateTimeOffset, DateTime> toLocal = t => TimeZoneInfo.ConvertTime(t, zone).DateTime;
        var shown = events.Select(e => (Event: e, Day: Day(e, toLocal), Start: e.AllDay ? e.Start.UtcDateTime : toLocal(e.Start)))
            .OrderBy(x => x.Day).ThenBy(x => !x.Event.AllDay).ThenBy(x => x.Start).ThenBy(x => x.Event.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var text = new StringBuilder();
        text.Append(culture, $"Events from {toLocal(from):ddd d MMM yyyy HH:mm} to {toLocal(to):ddd d MMM yyyy HH:mm} ({zone.Id}).");
        if (shown.Count == 0) return text.Append(" None found.").ToString();
        text.AppendLine();

        DateTime? current = null;
        foreach (var (e, day, _) in shown.Take(MaxEvents))
        {
            if (current != day)
            {
                text.AppendLine().Append(day.ToString("dddd d MMMM yyyy", culture)).AppendLine();
                current = day;
            }
            text.Append("- ").Append(When(e, toLocal, culture)).Append(' ').Append(e.Title);
            text.Append(" [").Append(e.Calendar).Append(']');
            if (e.Location.Length > 0) text.Append(" @ ").Append(OneLine(e.Location, MaxDescription));
            if (e.Description.Length > 0) text.Append(" — ").Append(OneLine(e.Description, MaxDescription));
            // What changing or deleting it needs; a repeating event can't be changed from here.
            if (e.Repeats) text.Append(" (repeats)");
            if (e.Id.Length > 0) text.Append(" {id: ").Append(e.Id).Append('}');
            text.AppendLine();
        }
        if (shown.Count > MaxEvents) text.AppendLine().Append(culture, $"({shown.Count - MaxEvents} more events not shown; ask for a shorter range.)");
        return text.ToString().TrimEnd();
    }

    static bool Contains(string text, string search) => text.Contains(search, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>The day an event is shown under: all-day events are dates, timed ones are in the user's zone.</summary>
    static DateTime Day(CalendarEvent e, Func<DateTimeOffset, DateTime> local) => e.AllDay ? e.Start.UtcDateTime.Date : local(e.Start).Date;

    static string When(CalendarEvent e, Func<DateTimeOffset, DateTime> local, CultureInfo culture)
    {
        if (e.AllDay)
        {
            var last = e.End.UtcDateTime.Date.AddDays(-1);
            return last > e.Start.UtcDateTime.Date ? $"all day until {last.ToString("ddd d MMM", culture)}" : "all day";
        }
        var start = local(e.Start);
        var end = local(e.End);
        if (end <= start) return start.ToString("HH:mm", culture);
        var from = start.ToString("HH:mm", culture);
        return end.Date == start.Date ? $"{from}–{end.ToString("HH:mm", culture)}" : $"{from}–{end.ToString("ddd d MMM HH:mm", culture)}";
    }

    static string OneLine(string text, int max)
    {
        var line = string.Join(' ', text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return line.Length <= max ? line : line[..max].TrimEnd() + "…";
    }
}
