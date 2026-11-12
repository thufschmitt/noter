using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Longhand.Services;

/// <summary>
/// The calendar tools Claude gets while a Nextcloud account is connected: reading events, and adding, changing and deleting
/// them. Changes go through the version the event was read at, so an event edited elsewhere in the meantime is left alone, and
/// repeating events are never changed (one change could rewrite every occurrence).
/// </summary>
public static class CalendarTools
{
    /// <param name="calendars">The calendars (by <see cref="CalendarInfo.Segment"/>) the user chose for the tools to use; null for all of them.</param>
    public static IAgentTool[] For(Nextcloud nextcloud, NextcloudAccount account, TimeZoneInfo? zone = null, Func<DateTimeOffset>? now = null, IReadOnlyCollection<string>? calendars = null)
    {
        var context = new CalendarContext(nextcloud, account, zone ?? TimeZoneInfo.Local, now ?? (() => DateTimeOffset.Now), calendars);
        return new IAgentTool[] { new CalendarTool(context), new CalendarAddTool(context), new CalendarChangeTool(context), new CalendarDeleteTool(context) };
    }
}

/// <summary>What every calendar tool shares, whichever calendar it is: the user's time zone and the clock, and reading the times and words they are given.</summary>
public class CalendarClock
{
    public TimeZoneInfo Zone { get; }
    public Func<DateTimeOffset> Now { get; }

    public CalendarClock(TimeZoneInfo zone, Func<DateTimeOffset> now)
    {
        Zone = zone;
        Now = now;
    }

    internal static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    internal const string TimeHelp = "a date like 2026-11-10 or a date-time like 2026-11-10T14:00, in the user's local time";

    /// <summary>Times without an offset are the user's local time. A date alone sets <c>dateOnly</c>.</summary>
    internal bool TryTime(IReadOnlyDictionary<string, JsonElement> input, string key, out DateTimeOffset time, out bool dateOnly)
    {
        time = default;
        dateOnly = false;
        if (!input.TryGetValue(key, out var element) || element.ValueKind != JsonValueKind.String) return false;
        var text = element.GetString()!.Trim();

        if (DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            dateOnly = true;
            time = new DateTimeOffset(day, Zone.GetUtcOffset(day));
            return true;
        }
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces, out var parsed))
        {
            // "AssumeLocal" means this PC's zone; redo the offset in the zone the tool was given.
            bool hasOffset = text.EndsWith("Z", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(text, @"[+-]\d{2}:?\d{2}$");
            time = hasOffset ? parsed : new DateTimeOffset(DateTime.SpecifyKind(parsed.DateTime, DateTimeKind.Unspecified), Zone.GetUtcOffset(parsed.DateTime));
            return true;
        }
        return false;
    }

    internal static string? Text(IReadOnlyDictionary<string, JsonElement> input, string key) =>
        input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : null;

    internal static bool? Flag(IReadOnlyDictionary<string, JsonElement> input, string key) =>
        input.TryGetValue(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    /// <summary>
    /// When an event is, from the start, end and all_day the tool was given, falling back to <paramref name="old"/> for what it
    /// wasn't (or, for a new event, an hour, or the one day). An end given as a date includes that day.
    /// </summary>
    internal EventWhen When(IReadOnlyDictionary<string, JsonElement> input, CalendarEvent? old)
    {
        bool hasStart = input.ContainsKey("start"), hasEnd = input.ContainsKey("end");
        DateTimeOffset start = default, end = default;
        bool startDate = false, endDate = false;
        if (hasStart && !TryTime(input, "start", out start, out startDate)) throw new InvalidOperationException("start must be " + TimeHelp + ".");
        if (hasEnd && !TryTime(input, "end", out end, out endDate)) throw new InvalidOperationException("end must be " + TimeHelp + ".");
        bool allDay = Flag(input, "all_day") ?? (hasStart ? startDate : old?.AllDay ?? false);

        // An all-day event is whole dates, kept as midnight UTC (as events read from the calendar are).
        DateTimeOffset Day(DateTimeOffset t) => new(TimeZoneInfo.ConvertTime(t, Zone).Date, TimeSpan.Zero);
        DateTimeOffset LocalDay(DateTimeOffset t, bool isDate) => isDate ? new(t.DateTime.Date, TimeSpan.Zero) : Day(t);

        if (allDay)
        {
            var from = hasStart ? LocalDay(start, startDate) : old is { AllDay: true } ? old.Start : old is not null ? Day(old.Start) : throw new InvalidOperationException("start is needed.");
            DateTimeOffset to;
            if (hasEnd) to = LocalDay(end, endDate).AddDays(endDate ? 1 : 0);
            else if (old is { AllDay: true }) to = from + (old.End - old.Start);
            else to = from.AddDays(1);
            if (to <= from) to = from.AddDays(1);
            return new EventWhen(from, to, true);
        }
        else
        {
            if (!hasStart && old is null or { AllDay: true }) throw new InvalidOperationException("A timed event needs a start time (a date-time like 2026-11-10T14:00).");
            if (hasStart && startDate) throw new InvalidOperationException("A timed event needs a start time, not just a date; or set all_day.");
            var from = hasStart ? start : old!.Start;
            DateTimeOffset to;
            if (hasEnd) to = endDate ? end.AddDays(1) : end;
            else if (old is { AllDay: false }) to = from + (old.End - old.Start);
            else to = from.AddHours(1);
            if (to <= from) throw new InvalidOperationException("end must be after start.");
            return new EventWhen(from, to, false);
        }
    }

    /// <summary>How the event reads back to Claude: its day and time in the user's zone, title and id.</summary>
    internal string Describe(string title, EventWhen when, string calendar, string id)
    {
        var culture = CultureInfo.InvariantCulture;
        string at;
        if (when.AllDay)
        {
            var last = when.End.DateTime.Date.AddDays(-1);
            at = when.Start.ToString("ddd d MMM yyyy", culture) + (last > when.Start.DateTime.Date ? " to " + last.ToString("ddd d MMM yyyy", culture) : "") + ", all day";
        }
        else
        {
            var from = TimeZoneInfo.ConvertTime(when.Start, Zone);
            var to = TimeZoneInfo.ConvertTime(when.End, Zone);
            at = from.ToString("ddd d MMM yyyy HH:mm", culture) + "–" + to.ToString(to.Date == from.Date ? "HH:mm" : "ddd d MMM HH:mm", culture);
        }
        return $"\"{title}\", {at} [{calendar}] {{id: {id}}}";
    }

}

/// <summary>What the Nextcloud calendar tools share: the account, and reading an event back from the server.</summary>
public sealed class CalendarContext : CalendarClock
{
    public Nextcloud Nextcloud { get; }
    public NextcloudAccount Account { get; }
    /// <summary>The calendars (by <see cref="CalendarInfo.Segment"/>) the user chose to use; null when all of them are.</summary>
    public IReadOnlyCollection<string>? Only { get; }

    public CalendarContext(Nextcloud nextcloud, NextcloudAccount account, TimeZoneInfo zone, Func<DateTimeOffset> now, IReadOnlyCollection<string>? only = null) : base(zone, now)
    {
        Nextcloud = nextcloud;
        Account = account;
        Only = only;
    }

    internal bool Uses(string segment) => Only is null || Only.Contains(segment);

    /// <summary>Refuses to go on when the user switched every calendar off.</summary>
    internal void RequireCalendars()
    {
        if (Only is { Count: 0 }) throw CalendarChoice.NoneChosen("Nextcloud");
    }

    /// <summary>The event behind an id, as it is now on the server, with its version tag.</summary>
    internal async Task<(string Ics, string? ETag, CalendarEvent Event)> ReadEventAsync(string id, CancellationToken ct)
    {
        if (!Uses(id.Split('/')[0])) throw CalendarChoice.NotChosen();
        var (ics, etag) = await Nextcloud.GetEventAsync(Account, id, ct);
        var events = ICalendar.ParseEvents(ics, id.Split('/')[0], Zone);
        if (events.Count == 0) throw new InvalidOperationException("That isn't an event Longhand can read.");
        if (events.Count > 1 || events[0].Repeats) throw new InvalidOperationException("That is a repeating event; Longhand can't change or delete repeating events. Tell the user to change it in their calendar app.");
        return (ics, etag, events[0]);
    }

    internal static string Id(IReadOnlyDictionary<string, JsonElement> input) =>
        Text(input, "id") is { } id && Nextcloud.IsEventId(id) ? id : throw new InvalidOperationException("id must be an event's id, as reading the calendar shows it ({id: …}).");
}

/// <summary>Lets Claude read the user's Nextcloud calendar.</summary>
public sealed class CalendarTool : IAgentTool
{
    public const string ToolName = "calendar_events";
    const int MaxDays = 93;

    readonly CalendarContext _c;

    public CalendarTool(CalendarContext context) => _c = context;

    public CalendarTool(Nextcloud nextcloud, NextcloudAccount account, TimeZoneInfo? zone = null, Func<DateTimeOffset>? now = null, IReadOnlyCollection<string>? calendars = null)
        : this(new CalendarContext(nextcloud, account, zone ?? TimeZoneInfo.Local, now ?? (() => DateTimeOffset.Now), calendars)) { }

    public string Name => ToolName;
    public string Status => "checking your calendar…";

    public ToolSpec Definition => ToolSpec.Object(
        ToolName,
        "Reads the user's calendar from their Nextcloud server. Use it for questions about their schedule, availability, " +
        "upcoming or past events, or when something is on, and to find an event before changing or deleting it. Returns the " +
        "events in the range, grouped by day, each with its time, title, calendar, location, whether it repeats, and its " +
        "{id: …}. Times are in the user's own time zone. To show the events to the user, put them on a timeline in a layout " +
        "(see your instructions), not in a list of words. Ask for a short range (a day or a week) unless the question needs " +
        "more; the longest range is about three months.",
        new Dictionary<string, JsonElement>
        {
            ["start"] = CalendarContext.Json("""{"type":"string","description":"First day or moment of the range: a date like 2026-11-10 or a date-time like 2026-11-10T14:00, in the user's local time."}"""),
            ["end"] = CalendarContext.Json("""{"type":"string","description":"Last day or end of the range. A date alone (2026-11-12) includes that whole day; a date-time is the exclusive end. Defaults to a week after start."}"""),
            ["search"] = CalendarContext.Json("""{"type":"string","description":"Optional: only events whose title, location or description contain this text."}"""),
        },
        "start");

    public async Task<string> RunAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        if (!_c.TryTime(input, "start", out var start, out _)) throw new InvalidOperationException("start must be " + CalendarContext.TimeHelp + ".");

        DateTimeOffset end;
        if (input.ContainsKey("end"))
        {
            if (!_c.TryTime(input, "end", out end, out var dateOnly)) throw new InvalidOperationException("end must be " + CalendarContext.TimeHelp + ".");
            if (dateOnly) end = end.AddDays(1); // a date as the end means "through that day"
        }
        else end = start.AddDays(7);

        if (end <= start) throw new InvalidOperationException("end must be after start.");
        if (end - start > TimeSpan.FromDays(MaxDays)) end = start.AddDays(MaxDays);

        _c.RequireCalendars();
        var events = await _c.Nextcloud.EventsAsync(_c.Account, start, end, _c.Zone, ct, _c.Only);
        return CalendarText.WithTimelineHint(CalendarText.Agenda(events, start, end, _c.Zone, CalendarContext.Text(input, "search")));
    }
}

/// <summary>Lets Claude add an event to the user's calendar.</summary>
public sealed class CalendarAddTool : IAgentTool
{
    public const string ToolName = "calendar_add_event";
    readonly CalendarContext _c;

    public CalendarAddTool(CalendarContext context) => _c = context;

    public string Name => ToolName;
    public string Status => "adding it to your calendar…";

    public ToolSpec Definition => ToolSpec.Object(
        ToolName,
        "Adds an event to the user's Nextcloud calendar. Use it only when the user asked for something to be put in their " +
        "calendar. Times are in the user's own time zone. A timed event lasts an hour unless an end is given; a date alone " +
        "makes an all-day event. Returns the event as added, with its id.",
        new Dictionary<string, JsonElement>
        {
            ["title"] = CalendarContext.Json("""{"type":"string","description":"What the event is, short, as it should appear in the calendar."}"""),
            ["start"] = CalendarContext.Json("""{"type":"string","description":"When it starts: a date-time like 2026-11-10T14:00, or a date like 2026-11-10 for an all-day event, in the user's local time."}"""),
            ["end"] = CalendarContext.Json("""{"type":"string","description":"Optional: when it ends (a date-time), or the last day of an all-day event (a date)."}"""),
            ["all_day"] = CalendarContext.Json("""{"type":"boolean","description":"Optional: true for an all-day event. A start given as a date alone already means all day."}"""),
            ["location"] = CalendarContext.Json("""{"type":"string","description":"Optional: where it is."}"""),
            ["description"] = CalendarContext.Json("""{"type":"string","description":"Optional: notes for the event."}"""),
            ["calendar"] = CalendarContext.Json("""{"type":"string","description":"Optional: the name of the calendar to add it to. Defaults to the user's own default calendar."}"""),
        },
        "title", "start");

    public async Task<string> RunAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        var title = CalendarContext.Text(input, "title");
        if (string.IsNullOrEmpty(title)) throw new InvalidOperationException("title is needed.");
        if (!input.ContainsKey("start")) throw new InvalidOperationException("start is needed.");
        var when = _c.When(input, old: null);

        _c.RequireCalendars();
        var calendars = (await _c.Nextcloud.ListCalendarsAsync(_c.Account, ct)).Where(c => c.Writable && _c.Uses(c.Segment)).ToList();
        if (calendars.Count == 0) throw new InvalidOperationException("The user has no calendar that Longhand may add events to.");
        CalendarInfo calendar;
        if (CalendarContext.Text(input, "calendar") is { Length: > 0 } wanted)
            calendar = calendars.FirstOrDefault(c => c.Name.Equals(wanted, StringComparison.CurrentCultureIgnoreCase))
                ?? throw new InvalidOperationException("There is no calendar called \"" + wanted + "\" that events can be added to. The calendars are: " + string.Join(", ", calendars.Select(c => c.Name)) + ".");
        else calendar = calendars.FirstOrDefault(c => c.Segment == "personal") ?? calendars[0];

        var uid = Guid.NewGuid().ToString("N") + "@longhand";
        var id = calendar.Segment + "/" + Guid.NewGuid().ToString("N") + ".ics";
        var ics = ICalendar.NewEvent(uid, title, when, CalendarContext.Text(input, "location"), CalendarContext.Text(input, "description"), _c.Now());
        await _c.Nextcloud.PutEventAsync(_c.Account, id, ics, etag: null, ct);
        return "Added " + _c.Describe(title, when, calendar.Name, id) + ".";
    }
}

/// <summary>Lets Claude change an event in the user's calendar: its title, time, location or notes.</summary>
public sealed class CalendarChangeTool : IAgentTool
{
    public const string ToolName = "calendar_change_event";
    readonly CalendarContext _c;

    public CalendarChangeTool(CalendarContext context) => _c = context;

    public string Name => ToolName;
    public string Status => "changing your calendar…";

    public ToolSpec Definition => ToolSpec.Object(
        ToolName,
        "Changes an event in the user's Nextcloud calendar. Use it only when the user asked for that change, and only after " +
        "reading the calendar to find the event and its id; if it isn't clear which event they mean, ask instead. Give only " +
        "what changes: moving the start keeps the event's length unless an end is given. Repeating events can't be changed. " +
        "Returns the event as it is now.",
        new Dictionary<string, JsonElement>
        {
            ["id"] = CalendarContext.Json("""{"type":"string","description":"The event's id, as reading the calendar shows it ({id: …})."}"""),
            ["title"] = CalendarContext.Json("""{"type":"string","description":"Optional: a new title."}"""),
            ["start"] = CalendarContext.Json("""{"type":"string","description":"Optional: a new start, a date-time like 2026-11-10T14:00 or a date for an all-day event, in the user's local time."}"""),
            ["end"] = CalendarContext.Json("""{"type":"string","description":"Optional: a new end (a date-time), or the last day of an all-day event (a date)."}"""),
            ["all_day"] = CalendarContext.Json("""{"type":"boolean","description":"Optional: make it all-day (true) or timed (false; then give a start time)."}"""),
            ["location"] = CalendarContext.Json("""{"type":"string","description":"Optional: a new location; an empty string removes it."}"""),
            ["description"] = CalendarContext.Json("""{"type":"string","description":"Optional: new notes; an empty string removes them."}"""),
        },
        "id");

    public async Task<string> RunAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        var id = CalendarContext.Id(input);
        var (ics, etag, old) = await _c.ReadEventAsync(id, ct);
        bool timeChanges = input.ContainsKey("start") || input.ContainsKey("end") || input.ContainsKey("all_day");
        var change = new EventChange(
            CalendarContext.Text(input, "title") is { Length: > 0 } title ? title : null,
            timeChanges ? _c.When(input, old) : null,
            CalendarContext.Text(input, "location"),
            CalendarContext.Text(input, "description"));
        if (change == new EventChange()) throw new InvalidOperationException("Nothing to change was given.");

        var changed = ICalendar.Change(ics, change, _c.Now(), out var problem) ?? throw new InvalidOperationException(problem);
        await _c.Nextcloud.PutEventAsync(_c.Account, id, changed, etag, ct);
        var when = change.When ?? new EventWhen(old.Start, old.End, old.AllDay);
        return "Changed it. It is now " + _c.Describe(change.Title ?? old.Title, when, old.Calendar, id) + ".";
    }
}

/// <summary>Lets Claude delete an event from the user's calendar.</summary>
public sealed class CalendarDeleteTool : IAgentTool
{
    public const string ToolName = "calendar_delete_event";
    readonly CalendarContext _c;

    public CalendarDeleteTool(CalendarContext context) => _c = context;

    public string Name => ToolName;
    public string Status => "removing it from your calendar…";

    public ToolSpec Definition => ToolSpec.Object(
        ToolName,
        "Deletes an event from the user's Nextcloud calendar. Use it only when the user clearly asked for that event to be " +
        "removed or cancelled, and only after reading the calendar to find it and its id; if it isn't clear which event they " +
        "mean, ask instead. Repeating events can't be deleted. Returns what was deleted, so the reply can say so.",
        new Dictionary<string, JsonElement>
        {
            ["id"] = CalendarContext.Json("""{"type":"string","description":"The event's id, as reading the calendar shows it ({id: …})."}"""),
        },
        "id");

    public async Task<string> RunAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        var id = CalendarContext.Id(input);
        var (_, etag, old) = await _c.ReadEventAsync(id, ct);
        await _c.Nextcloud.DeleteEventAsync(_c.Account, id, etag, ct);
        return "Deleted " + _c.Describe(old.Title, new EventWhen(old.Start, old.End, old.AllDay), old.Calendar, id) + ".";
    }
}
