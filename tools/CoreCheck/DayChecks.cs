using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Longhand.Services;

/// <summary>A calendar kept in memory: events by id, and what was asked of it.</summary>
sealed class FakeDaySource : IDaySource
{
    public readonly Dictionary<string, (CalendarEvent Event, bool Editable)> Events = new();
    public readonly List<string> Moves = new(), Deletes = new();
    public readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;

    public string Provider => DefaultCalendar.Google;
    public bool CanWrite => true;
    public bool Owns(string id) => id.StartsWith("cal|", StringComparison.Ordinal);

    public void Add(string id, string title, DateTime start, DateTime end, bool editable = true) =>
        Events[id] = (new CalendarEvent("Work", title, new DateTimeOffset(start, TimeSpan.Zero), new DateTimeOffset(end, TimeSpan.Zero), false, "", "", id), editable);

    public Task<List<DayEvent>> EventsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        Task.FromResult(Events.Values.Where(e => e.Event.End > from && e.Event.Start < to).Select(e => new DayEvent(e.Event, e.Editable)).ToList());

    public Task<string> MoveAsync(string id, DateTime start, DateTime end, CancellationToken ct)
    {
        var (e, editable) = Events[id];
        Moves.Add($"{id} {start:HH:mm}-{end:HH:mm}");
        Events[id] = (e with { Start = new DateTimeOffset(start, TimeSpan.Zero), End = new DateTimeOffset(end, TimeSpan.Zero) }, editable);
        return Task.FromResult(id);
    }

    public Task DeleteAsync(string id, CancellationToken ct)
    {
        Deletes.Add(id);
        Events.Remove(id);
        return Task.CompletedTask;
    }
}

/// <summary>The add tool as the model sees it, adding to the fake calendar.</summary>
sealed class FakeAddTool : IAgentTool
{
    readonly FakeDaySource _source;
    public FakeAddTool(FakeDaySource source) => _source = source;
    public string Name => "google_calendar_add_event";
    public string Status => "adding…";
    public ToolSpec Definition => ToolSpec.Object(Name, "Adds an event.", new Dictionary<string, JsonElement>(), "title");
    public Task<string> RunAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        var start = DateTime.Parse(input["start"].GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        _source.Add("cal|new", input["title"].GetString()!, start, start.AddHours(1));
        return Task.FromResult("Added.");
    }
}

sealed class FakeDayData : IPageData, IDayData
{
    readonly FakeCodex _api;
    public readonly FakeDaySource Source;
    public FakeDayData(FakeCodex api, FakeDaySource source) { _api = api; Source = source; }
    public CodexSignIn? SignIn => _api.SignIn;
    public string DefaultModel { get; set; } = "gpt-6.1-sol";
    public Task<ConversationIndex> LoadIndexAsync() => Task.FromResult(new ConversationIndex());
    public Task SaveIndexAsync(ConversationIndex index) => Task.CompletedTask;
    public Task<List<SavedNote>?> LoadPageAsync(string id) => Task.FromResult<List<SavedNote>?>(null);
    public Task SavePageAsync(string id, IReadOnlyList<SavedNote> notes) => throw new InvalidOperationException("the calendar app saves nothing");
    public Task DeletePageAsync(string id) => Task.CompletedTask;
    public Task<PreparedAsk> PrepareAskAsync(CodexSignIn signIn, string model, CancellationToken ct) =>
        Task.FromResult(new PreparedAsk(new CodexService(signIn, model: model, agentTools: new IAgentTool[] { new FakeAddTool(Source) }, baseUrl: _api.BaseUrl, refreshUrl: _api.BaseUrl + "/oauth/token"), Array.Empty<LegendEntry>()));
    public DayCalendar? DayCalendar() => new(new IDaySource[] { Source }, TimeZoneInfo.Utc);
    public TimeZoneInfo Zone => TimeZoneInfo.Utc;
}

/// <summary>The calendar app: a day of the calendar on the page, moved and resized straight in the calendar, drawings sent to the model.</summary>
static class DayChecks
{
    static async Task<bool> Until(Func<bool> condition, int ms = 4000)
    {
        for (int waited = 0; waited < ms; waited += 20)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    public static async Task RunAsync(Action<string, bool> check)
    {
        // ----- the layout of a day -----
        var day = new DateOnly(2026, 11, 16);
        var at = (int h, int m) => day.ToDateTime(new TimeOnly(h, m));
        var events = new List<DayEvent>
        {
            new(new CalendarEvent("Work", "Standup <daily>", new(at(9, 0), TimeSpan.Zero), new(at(9, 30), TimeSpan.Zero), false, "", "", "cal|a"), true),
            new(new CalendarEvent("Home", "Dentist", new(at(14, 0), TimeSpan.Zero), new(at(15, 30), TimeSpan.Zero), false, "", "", "cal|b"), false),
            new(new CalendarEvent("Work", "Trip", new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), new(day.AddDays(2).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), true, "", "", "cal|c"), true),
            new(new CalendarEvent("Work", "Night shift", new(at(22, 0), TimeSpan.Zero), new(day.AddDays(1).ToDateTime(new TimeOnly(6, 0)), TimeSpan.Zero), false, "", "", "cal|d"), true),
            new(new CalendarEvent("Work", "Yesterday", new(at(8, 0).AddDays(-1), TimeSpan.Zero), new(at(9, 0).AddDays(-1), TimeSpan.Zero), false, "", "", "cal|e"), true),
        };
        var layout = DayLayout.Build(day, events, TimeZoneInfo.Utc);
        var root = Layout.Parse(layout.Markup)!;
        var items = root.Descendants().Where(n => n.Tag == "box").ToList();
        check("a day is a timeline column with an item for each event that touches it", items.Count == 4 && layout.Events.Count == 4 && Layout.IsTimeline(items[0].Parent!));
        check("an all-day event is listed above the hours, and isn't moved there", items[0].Words() == "Trip" && !items[0].Has("start") && !items[0].Has("edit"));
        var standup = items.First(i => i.Words().StartsWith("Standup"));
        check("a timed event sits at its times, and can be moved when its calendar can be changed", standup.Attrs["start"] == "09:00" && standup.Attrs["end"] == "09:30" && standup.Has("edit") && standup.Words() == "Standup <daily>");
        check("an event of a calendar that can't be changed can't be moved", !items.First(i => i.Words() == "Dentist").Has("edit"));
        var night = items.First(i => i.Words() == "Night shift");
        check("an event that runs past midnight shows to the end of the day, and can't be moved here", night.Attrs["end"] == "24:00" && !night.Has("edit"));
        check("each calendar has its colour when there are several", items.Select(i => i.Attrs.GetValueOrDefault("color") ?? "").Distinct().Count() == 2);
        check("the model reads the day as an agenda with the ids", layout.Agenda.Contains("{id: cal|a}") && layout.Agenda.Contains("Standup"));
        var standupId = layout.Events.First(p => p.Value.Event.Id == "cal|a").Key;
        var moved = Layout.ApplyEdits(layout.Markup, new[] { new DrawingChange(standupId, "move", To: "d20261116", At: "10:15") })!;
        check("an item's new times read as the day's local times", DayLayout.TimesIn(moved, standupId, day) == (at(10, 15), at(10, 45)));

        // ----- the default calendar for new events -----
        var settings = new MemorySettings();
        check("no default calendar is chosen to begin with", DefaultCalendar.Read(settings) is null);
        DefaultCalendar.Write(settings, new DefaultCalendar.Choice(DefaultCalendar.Nextcloud, "work", "Work"));
        check("a default calendar is kept, and given to its provider only", DefaultCalendar.For(settings, DefaultCalendar.Nextcloud) == "work" && DefaultCalendar.For(settings, DefaultCalendar.Google) is null);
        check("with both calendars connected the model is told where new events go", DefaultCalendar.Hint(DefaultCalendar.Read(settings), true, true)!.Contains("calendar_add_event") && DefaultCalendar.Hint(DefaultCalendar.Read(settings), true, false) is null);
        DefaultCalendar.Write(settings, null);
        check("the choice can be forgotten", DefaultCalendar.Read(settings) is null);

        // ----- the calendar app -----
        await using var api = new FakeCodex { AskForTool = true, ToolName = "google_calendar_add_event", FinalText = "HEARD: [a box from 16:00] gym\n---\nAdded Gym at 16:00." };
        var source = new FakeDaySource();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var t = (int h, int m) => today.ToDateTime(new TimeOnly(h, m));
        api.ToolArguments = JsonSerializer.Serialize(new { title = "Gym", start = t(16, 0).ToString("yyyy-MM-dd'T'HH:mm") });
        source.Add("cal|a", "Standup", t(9, 0), t(9, 30));
        var view = new FakeView();
        var data = new FakeDayData(api, source);
        var session = new PageSession(view, data, calendarDay: true);
        await session.StartAsync();
        var dayNote = session.Notes.SingleOrDefault(n => n.IsDay);
        check("the calendar app opens on today, with the day as a layout and nothing else", session.Day == today && dayNote is { Drawings.Count: 1 } && session.Notes.Count == 1 && session.Index.Items.Count == 0);
        var drawing = dayNote!.Drawings[0];
        var item = Layout.Parse(drawing.Markup)!.Descendants().First(n => n.Tag == "box").Id!;

        // Moving an event moves it in the calendar, at once, and nothing waits for the model.
        bool ok = session.EditDrawing(dayNote, drawing, new[] { new DrawingChange(item, "move", To: $"d{today:yyyyMMdd}", At: "11:00") });
        check("moving an event changes the calendar straight away", ok && await Until(() => source.Moves.Contains("cal|a 11:00-11:30")));
        check("and nothing is waiting to be asked", !view.State.CanAsk && !session.HasEdits);
        check("the day is read again afterwards", await Until(() => drawing.Markup.Contains("start=\"11:00\"")));
        item = Layout.Parse(drawing.Markup)!.Descendants().First(n => n.Tag == "box").Id!;
        session.EditDrawing(dayNote, drawing, new[] { new DrawingChange(item, "resize", End: "12:00") });
        check("resizing an event changes its end in the calendar", await Until(() => source.Moves.Contains("cal|a 11:00-12:00")));
        session.Undo();
        check("undo puts it back as it was, in the calendar", await Until(() => source.Moves.Count == 3 && source.Moves[^1] == "cal|a 11:00-11:30"));
        check("turning an event isn't a change of the calendar", !session.EditDrawing(dayNote, drawing, new[] { new DrawingChange(item, "rotate", Degrees: 10) }) && source.Moves.Count == 3);

        // Drawing goes to the model, with the day.
        var ink = view.Draw(700, 760);
        session.StrokesCollected(new[] { ink });
        check("a drawing waits to be asked", view.State.CanAsk);
        await session.AskAsync();
        var asked = api.Requests.Last(r => r.Path.EndsWith("/responses") && !r.Body.Contains("function_call_output")).Body;
        check("the picture shows the day under the drawing", view.LastContext is { } c && c.Replies.Contains(dayNote) && view.LastIds!.SequenceEqual(new[] { ink }));
        check("the model is told it is in the calendar app, that drawings likely mean new events, and the day's events", asked.Contains("calendar app") && asked.Contains("most likely meant to create new events") && asked.Contains("{id: cal|a}"));
        check("the event the model added shows on the day", await Until(() => drawing.Markup.Contains("Gym")));
        check("the ink it answered comes off the page", view.Ink.Count == 0);
        var reply = session.Notes.Single(n => !n.IsDay);
        check("the answer is written under the day", reply.Text == "Added Gym at 16:00." && reply.Y >= dayNote.Bottom);
        session.Undo();
        check("undo brings the ink back", view.Ink.Count == 1);

        // Another day.
        await session.MoveDayAsync(1);
        check("the next day starts afresh, without the ink or the answer", session.Day == today.AddDays(1) && view.Ink.Count == 0 && session.Notes.Count == 1 && !session.Notes[0].Drawings[0].Markup.Contains("Gym"));
        await session.TodayAsync();
        check("today comes back", session.Day == today && session.Notes[0].Drawings[0].Markup.Contains("Gym"));
    }
}
