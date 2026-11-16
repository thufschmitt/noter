using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Longhand.Services;

/// <summary>
/// The calendar new events go to when nobody names one: one calendar of one provider, chosen in the connected apps. Kept with the
/// settings as <c>{"provider":"google","id":"…","name":"…"}</c>; nothing kept means each provider's own default (Google's main
/// calendar, Nextcloud's personal one).
/// </summary>
public static class DefaultCalendar
{
    public const string Google = "google", Nextcloud = "nextcloud";
    const string Key = "calendar-default";

    public sealed record Choice(string Provider, string Id, string Name);

    public static Choice? Read(ISettings settings)
    {
        if (settings.Get(Key) is not { Length: > 0 } text) return null;
        try
        {
            var c = JsonSerializer.Deserialize<Choice>(text);
            return c is { Provider: Google or Nextcloud, Id.Length: > 0 } ? c with { Name = c.Name ?? c.Id } : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Keeps the choice; null goes back to each provider's own default.</summary>
    public static void Write(ISettings settings, Choice? choice)
    {
        if (choice is null) settings.Remove(Key);
        else settings.Set(Key, JsonSerializer.Serialize(choice));
    }

    /// <summary>
    /// The calendars new events can go to: the writable ones of each connected provider that the user chose to use (Google only
    /// when changes are allowed). A provider that can't be reached is named in the problems instead.
    /// </summary>
    public static async Task<(List<Choice> Options, List<string> Problems)> OptionsAsync(
        GoogleCalendar? google, bool googleCanWrite, IReadOnlyCollection<string>? googleOnly,
        Nextcloud? nextcloud, NextcloudAccount? account, IReadOnlyCollection<string>? nextcloudOnly, CancellationToken ct)
    {
        var options = new List<Choice>();
        var problems = new List<string>();
        if (google is not null && googleCanWrite)
        {
            try
            {
                options.AddRange((await google.ListCalendarsAsync(ct)).Where(c => c.Writable && GoogleCalendar.IsUsed(c, googleOnly)).Select(c => new Choice(Google, c.Id, c.Name)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { problems.Add("Google Calendar: " + ex.Message); }
        }
        if (nextcloud is not null && account is not null)
        {
            try
            {
                options.AddRange((await nextcloud.ListCalendarsAsync(account, ct)).Where(c => c.Writable && (nextcloudOnly is null || nextcloudOnly.Contains(c.Segment))).Select(c => new Choice(Nextcloud, c.Segment, c.Name)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { problems.Add("Nextcloud: " + ex.Message); }
        }
        return (options, problems);
    }

    /// <summary>How a calendar is named in a list where both providers' calendars can be.</summary>
    public static string Label(Choice c) => (c.Provider == Google ? "Google: " : "Nextcloud: ") + c.Name;

    /// <summary>The default's id when it is one of <paramref name="provider"/>'s calendars; null otherwise.</summary>
    public static string? For(ISettings settings, string provider) => Read(settings) is { } c && c.Provider == provider ? c.Id : null;

    /// <summary>
    /// What the model is told about where new events go, when the user chose a default and more than one provider is connected (with
    /// only one, the add tool already uses it). Null when there is nothing to say.
    /// </summary>
    public static string? Hint(Choice? choice, bool googleConnected, bool nextcloudConnected)
    {
        if (choice is null || !(googleConnected && nextcloudConnected)) return null;
        var tool = choice.Provider == Google ? GoogleCalendarAddTool.ToolName : CalendarAddTool.ToolName;
        return $"New events go to the user's default calendar, \"{choice.Name}\" ({(choice.Provider == Google ? "Google Calendar" : "Nextcloud")}): add them with {tool} unless they name another calendar.";
    }
}

/// <summary>One event of the day shown in the calendar view, and whether the user may move or resize it there.</summary>
public sealed record DayEvent(CalendarEvent Event, bool Editable);

/// <summary>One provider's calendars, as the day view reads and changes them.</summary>
public interface IDaySource
{
    string Provider { get; }
    /// <summary>Whether the model can add events here (Google may be connected read-only).</summary>
    bool CanWrite { get; }
    /// <summary>Whether an event id is one of this provider's.</summary>
    bool Owns(string id);
    Task<List<DayEvent>> EventsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
    /// <summary>Moves an event (one occurrence of a repeating one). Returns the event's id afterwards, which can change.</summary>
    Task<string> MoveAsync(string id, DateTime start, DateTime end, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
    /// <summary>Adds a timed event to the default calendar (the user's choice, else the provider's own). Returns a sentence saying where, and its id.</summary>
    Task<(string Id, string Said)> AddAsync(string title, DateTime start, DateTime end, CancellationToken ct);
    /// <summary>Gives an event (one occurrence of a repeating one) a new title. Returns its id afterwards.</summary>
    Task<string> RenameAsync(string id, string title, CancellationToken ct);
}

/// <summary>
/// The day view's changes go through the same tools the model uses, given the event's id and its new local times: so they keep
/// their rules (only calendars the user chose, the version the event was read at, one occurrence of a repeating event).
/// </summary>
static class DayTools
{
    static JsonElement Str(string s) => JsonDocument.Parse(JsonSerializer.Serialize(s)).RootElement.Clone();
    static string Local(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);

    static readonly Regex IdIn = new(@"\{id: ([^}]+)\}\.?$", RegexOptions.Compiled);

    public static async Task<string> MoveAsync(IAgentTool change, string id, DateTime start, DateTime end, CancellationToken ct)
    {
        var said = await change.RunAsync(new Dictionary<string, JsonElement> { ["id"] = Str(id), ["start"] = Str(Local(start)), ["end"] = Str(Local(end)) }, ct);
        return IdIn.Match(said.TrimEnd()) is { Success: true } m ? m.Groups[1].Value : id;
    }

    public static Task DeleteAsync(IAgentTool delete, string id, CancellationToken ct) =>
        delete.RunAsync(new Dictionary<string, JsonElement> { ["id"] = Str(id) }, ct);

    public static async Task<string> RenameAsync(IAgentTool change, string id, string title, CancellationToken ct)
    {
        var said = await change.RunAsync(new Dictionary<string, JsonElement> { ["id"] = Str(id), ["title"] = Str(title) }, ct);
        return IdIn.Match(said.TrimEnd()) is { Success: true } m ? m.Groups[1].Value : id;
    }

    public static async Task<(string Id, string Said)> AddAsync(IAgentTool add, string title, DateTime start, DateTime end, CancellationToken ct)
    {
        var said = await add.RunAsync(new Dictionary<string, JsonElement> { ["title"] = Str(title), ["start"] = Str(Local(start)), ["end"] = Str(Local(end)) }, ct);
        return (IdIn.Match(said.TrimEnd()) is { Success: true } m ? m.Groups[1].Value : "", said);
    }
}

public sealed class NextcloudDaySource : IDaySource
{
    readonly CalendarContext _c;
    readonly CalendarChangeTool _change;
    readonly CalendarDeleteTool _delete;
    readonly CalendarAddTool _add;

    /// <param name="defaultCalendar">The calendar (by segment) new events go to; null for the personal one.</param>
    public NextcloudDaySource(Nextcloud nextcloud, NextcloudAccount account, TimeZoneInfo zone, IReadOnlyCollection<string>? calendars, string? defaultCalendar = null)
    {
        _c = new CalendarContext(nextcloud, account, zone, () => DateTimeOffset.Now, calendars) { Default = defaultCalendar };
        _change = new CalendarChangeTool(_c);
        _delete = new CalendarDeleteTool(_c);
        _add = new CalendarAddTool(_c);
    }

    public string Provider => DefaultCalendar.Nextcloud;
    public bool CanWrite => true;
    public bool Owns(string id) => Nextcloud.SplitId(id, out _, out _);

    public async Task<List<DayEvent>> EventsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        if (_c.Only is { Count: 0 }) return new();
        var writable = (await _c.Nextcloud.ListCalendarsAsync(_c.Account, ct)).Where(c => c.Writable).Select(c => c.Segment).ToHashSet(StringComparer.Ordinal);
        var events = await _c.Nextcloud.EventsAsync(_c.Account, from, to, _c.Zone, ct, _c.Only);
        return events.Select(e => new DayEvent(e, e.Id.Length > 0 && writable.Contains(e.Id.Split('/')[0]))).ToList();
    }

    public Task<string> MoveAsync(string id, DateTime start, DateTime end, CancellationToken ct) => DayTools.MoveAsync(_change, id, start, end, ct);
    public Task DeleteAsync(string id, CancellationToken ct) => DayTools.DeleteAsync(_delete, id, ct);
    public Task<(string Id, string Said)> AddAsync(string title, DateTime start, DateTime end, CancellationToken ct) => DayTools.AddAsync(_add, title, start, end, ct);
    public Task<string> RenameAsync(string id, string title, CancellationToken ct) => DayTools.RenameAsync(_change, id, title, ct);
}

public sealed class GoogleDaySource : IDaySource
{
    readonly GoogleCalendarContext _c;
    readonly GoogleCalendarChangeTool _change;
    readonly GoogleCalendarDeleteTool _delete;
    readonly GoogleCalendarAddTool _add;

    /// <param name="defaultCalendar">The calendar (by Google's id) new events go to; null for the main one.</param>
    public GoogleDaySource(GoogleCalendar google, bool canWrite, TimeZoneInfo zone, IReadOnlyCollection<string>? calendars, string? defaultCalendar = null)
    {
        _c = new GoogleCalendarContext(google, zone, () => DateTimeOffset.Now, calendars) { Default = defaultCalendar };
        _change = new GoogleCalendarChangeTool(_c);
        _delete = new GoogleCalendarDeleteTool(_c);
        _add = new GoogleCalendarAddTool(_c);
        CanWrite = canWrite;
    }

    public string Provider => DefaultCalendar.Google;
    public bool CanWrite { get; }
    public bool Owns(string id) => GoogleCalendar.TryParseId(id, out _, out _);

    public async Task<List<DayEvent>> EventsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        if (_c.Only is { Count: 0 }) return new();
        var writable = CanWrite
            ? (await _c.Google.ListCalendarsAsync(ct)).Where(c => c.Writable).Select(c => c.Id).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>();
        var events = await _c.Google.EventsAsync(from, to, ct, _c.Only);
        return events.Select(e => new DayEvent(e, GoogleCalendar.TryParseId(e.Id, out var calendar, out _) && writable.Contains(calendar))).ToList();
    }

    public Task<string> MoveAsync(string id, DateTime start, DateTime end, CancellationToken ct) => CanWrite
        ? DayTools.MoveAsync(_change, id, start, end, ct)
        : throw new InvalidOperationException("Google Calendar is connected read-only. Allow changes in the connected apps to move events.");

    public Task DeleteAsync(string id, CancellationToken ct) => CanWrite
        ? DayTools.DeleteAsync(_delete, id, ct)
        : throw new InvalidOperationException("Google Calendar is connected read-only. Allow changes in the connected apps to delete events.");

    public Task<(string Id, string Said)> AddAsync(string title, DateTime start, DateTime end, CancellationToken ct) => CanWrite
        ? DayTools.AddAsync(_add, title, start, end, ct)
        : throw new InvalidOperationException("Google Calendar is connected read-only. Allow changes in the connected apps to add events.");

    public Task<string> RenameAsync(string id, string title, CancellationToken ct) => CanWrite
        ? DayTools.RenameAsync(_change, id, title, ct)
        : throw new InvalidOperationException("Google Calendar is connected read-only. Allow changes in the connected apps to rename events.");
}

/// <summary>Every connected calendar, as one: what the day view shows and changes.</summary>
public sealed class DayCalendar
{
    public IReadOnlyList<IDaySource> Sources { get; }
    public TimeZoneInfo Zone { get; }
    /// <summary>The calendar new events go to, if the user chose one.</summary>
    public DefaultCalendar.Choice? Default { get; }

    public DayCalendar(IReadOnlyList<IDaySource> sources, TimeZoneInfo zone, DefaultCalendar.Choice? @default = null)
    {
        Sources = sources;
        Zone = zone;
        Default = @default;
    }

    public bool CanAdd => Sources.Any(s => s.CanWrite);

    /// <summary>The connected calendars as the day view uses them; null when none is connected.</summary>
    public static DayCalendar? From(GoogleCalendar? google, bool googleCanWrite, IReadOnlyCollection<string>? googleOnly,
        Nextcloud? nextcloud, NextcloudAccount? account, IReadOnlyCollection<string>? nextcloudOnly, TimeZoneInfo zone, DefaultCalendar.Choice? @default)
    {
        var sources = new List<IDaySource>();
        string? DefaultIn(string provider) => @default is { } d && d.Provider == provider ? d.Id : null;
        if (nextcloud is not null && account is not null) sources.Add(new NextcloudDaySource(nextcloud, account, zone, nextcloudOnly, DefaultIn(DefaultCalendar.Nextcloud)));
        if (google is not null) sources.Add(new GoogleDaySource(google, googleCanWrite, zone, googleOnly, DefaultIn(DefaultCalendar.Google)));
        return sources.Count == 0 ? null : new DayCalendar(sources, zone, @default);
    }

    /// <summary>
    /// The day's events, from every source: those that touch the day in the user's zone. A source that fails is left out and named in
    /// <paramref name="failed"/>, so the others still show.
    /// </summary>
    public async Task<List<DayEvent>> EventsAsync(DateOnly day, CancellationToken ct, List<string>? failed = null)
    {
        var (from, to) = Bounds(day);
        var lists = await Task.WhenAll(Sources.Select(async s =>
        {
            try { return await s.EventsAsync(from, to, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (Sources) failed?.Add((s.Provider == DefaultCalendar.Google ? "Google Calendar" : "Nextcloud") + ": " + ex.Message);
                return new List<DayEvent>();
            }
        }));
        return lists.SelectMany(l => l).ToList();
    }

    /// <summary>Midnight to midnight of the day, in the user's zone.</summary>
    public (DateTimeOffset From, DateTimeOffset To) Bounds(DateOnly day) => (At(day.ToDateTime(TimeOnly.MinValue)), At(day.AddDays(1).ToDateTime(TimeOnly.MinValue)));

    DateTimeOffset At(DateTime local) => new(local, Zone.GetUtcOffset(local));

    IDaySource SourceOf(string id) => Sources.FirstOrDefault(s => s.Owns(id)) ?? throw new InvalidOperationException("That event's calendar isn't connected any more.");

    public Task<string> MoveAsync(string id, DateTime start, DateTime end, CancellationToken ct) => SourceOf(id).MoveAsync(id, start, end, ct);
    public Task DeleteAsync(string id, CancellationToken ct) => SourceOf(id).DeleteAsync(id, ct);
    public Task<string> RenameAsync(string id, string title, CancellationToken ct) => SourceOf(id).RenameAsync(id, title, ct);

    /// <summary>
    /// Adds a timed event where new events go: the default calendar's provider, else the first that can be written to (Nextcloud,
    /// then Google). Returns its id.
    /// </summary>
    public Task<(string Id, string Said)> AddAsync(string title, DateTime start, DateTime end, CancellationToken ct)
    {
        var source = Sources.FirstOrDefault(s => s.CanWrite && s.Provider == Default?.Provider) ?? Sources.FirstOrDefault(s => s.CanWrite)
            ?? throw new InvalidOperationException("None of your calendars can be changed from Longhand (Google Calendar is connected read-only). Allow changes in the connected apps.");
        return source.AddAsync(title, start, end, ct);
    }
}

/// <summary>
/// The day as the calendar view shows it: a layout with one timeline column, an item for each event. Items get short ids of their
/// own (a layout's ids can't hold a calendar's), and <see cref="Events"/> says which event each stands for.
/// </summary>
public sealed class DayLayout
{
    public const string LayoutId = "day";
    /// <summary>The hours shown when the events don't need more.</summary>
    public const int FromMinutes = 7 * 60, ToMinutes = 21 * 60;
    const int MaxEvents = 120, MaxTitle = 80;

    public DateOnly Day { get; }
    public string Markup { get; }
    /// <summary>The event each item stands for, by the item's id.</summary>
    public IReadOnlyDictionary<string, DayEvent> Events { get; }

    static readonly string[] Colors = { "", "blue", "green", "purple", "orange", "teal", "pink", "brown" };

    DayLayout(DateOnly day, string markup, Dictionary<string, DayEvent> events)
    {
        Day = day;
        Markup = markup;
        Events = events;
    }


    public static DayLayout Build(DateOnly day, IReadOnlyList<DayEvent> events, TimeZoneInfo zone)
    {
        var midnight = day.ToDateTime(TimeOnly.MinValue);
        DateTime Local(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, zone).DateTime;

        // Each calendar gets a colour of its own when there are several (the first keeps the ink colour).
        var calendars = events.Select(e => e.Event.Calendar).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase).ToList();
        string ColorOf(string calendar) => calendars.Count < 2 ? "" : Colors[calendars.IndexOf(calendar) % Colors.Length];

        var allDay = new List<(DayEvent E, string Text)>();
        var timed = new List<(DayEvent E, int Start, int End, bool Whole)>();
        foreach (var e in events)
        {
            var ev = e.Event;
            if (ev.AllDay)
            {
                var first = DateOnly.FromDateTime(ev.Start.UtcDateTime);
                var last = DateOnly.FromDateTime(ev.End.UtcDateTime).AddDays(-1);
                if (first <= day && day <= (last < first ? first : last)) allDay.Add((e, ev.Title));
                continue;
            }
            var start = Local(ev.Start);
            var end = Local(ev.End);
            if (end <= midnight || start >= midnight.AddDays(1)) continue;
            int s = (int)Math.Max(0, (start - midnight).TotalMinutes);
            int en = (int)Math.Min(Layout.DayMinutes, (end - midnight).TotalMinutes);
            if (en <= s) en = Math.Min(Layout.DayMinutes, s + Layout.SnapMinutes);
            // Only an event that lies within the day can be moved here: dragging part of a longer one would cut it.
            bool whole = start >= midnight && end <= midnight.AddDays(1) && end > start;
            timed.Add((e, s, en, whole));
        }

        var map = new Dictionary<string, DayEvent>();
        var sb = new StringBuilder();
        sb.Append($"<layout id=\"{LayoutId}\" title=\"{day.ToString("ddd d MMM", CultureInfo.InvariantCulture)}\">");
        sb.Append($"<col id=\"d{day:yyyyMMdd}\" timeline=\"{Layout.Hhmm(FromMinutes)}-{Layout.Hhmm(ToMinutes)}\">");
        int n = 0;
        foreach (var (e, text) in allDay.OrderBy(a => a.Text, StringComparer.CurrentCultureIgnoreCase).Take(MaxEvents))
        {
            var id = "e" + ++n;
            map[id] = e;
            sb.Append($"<box id=\"{id}\"{ColorAttr(ColorOf(e.Event.Calendar))}>{Escape(Short(text))}</box>");
        }
        foreach (var (e, s, en, whole) in timed.OrderBy(t => t.Start).ThenBy(t => t.End).Take(Math.Max(0, MaxEvents - n)))
        {
            var id = "e" + ++n;
            map[id] = e;
            var edit = e.Editable && whole ? " edit" : "";
            sb.Append($"<box id=\"{id}\"{ColorAttr(ColorOf(e.Event.Calendar))} start=\"{Layout.Hhmm(s)}\" end=\"{Layout.Hhmm(en)}\"{edit}>{Escape(Short(e.Event.Title))}</box>");
        }
        sb.Append("</col></layout>");

        var markup = Layout.Sanitize(sb.ToString()) ?? throw new InvalidOperationException("The day couldn't be laid out.");
        return new DayLayout(day, markup, map);
    }

    static string ColorAttr(string color) => color.Length > 0 ? $" color=\"{color}\"" : "";

    static string Short(string title)
    {
        var t = Regex.Replace(title ?? "", @"\s+", " ").Trim();
        if (t.Length == 0) t = "(no title)";
        return t.Length <= MaxTitle ? t : t[..MaxTitle].TrimEnd() + "…";
    }

    static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    /// <summary>When an item is now, in the layout as it is after the user's change: its local start and end on the day. Null when it has no time.</summary>
    public static (DateTime Start, DateTime End)? TimesIn(string markup, string itemId, DateOnly day)
    {
        if (Layout.Parse(markup) is not { } root || Layout.Find(root, itemId) is not { } node || Layout.TimesOf(node) is not { } t) return null;
        var midnight = day.ToDateTime(TimeOnly.MinValue);
        return (midnight.AddMinutes(t.Start), midnight.AddMinutes(t.End));
    }
}
