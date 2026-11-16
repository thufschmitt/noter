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

    public Task<string> RenameAsync(string id, string title, CancellationToken ct)
    {
        var (e, editable) = Events[id];
        Renames.Add($"{id} {title}");
        Events[id] = (e with { Title = title }, editable);
        return Task.FromResult(id);
    }

    public readonly List<string> Renames = new();
    int _added;
    public Task<(string Id, string Said)> AddAsync(string title, DateTime start, DateTime end, CancellationToken ct)
    {
        var id = "cal|new" + ++_added;
        Add(id, title, start, end);
        return Task.FromResult((id, "Added."));
    }
}

/// <summary>The calendar app's data, with no ChatGPT sign-in: it never asks a model.</summary>
sealed class FakeDayData : IPageData, IDayData
{
    public readonly FakeDaySource Source;
    public FakeDayData(FakeDaySource source) { Source = source; }
    public CodexSignIn? SignIn => null;
    public string DefaultModel { get; set; } = "gpt-6.1-sol";
    public Task<ConversationIndex> LoadIndexAsync() => Task.FromResult(new ConversationIndex());
    public Task SaveIndexAsync(ConversationIndex index) => Task.CompletedTask;
    public Task<List<SavedNote>?> LoadPageAsync(string id) => Task.FromResult<List<SavedNote>?>(null);
    public Task SavePageAsync(string id, IReadOnlyList<SavedNote> notes) => throw new InvalidOperationException("the calendar app saves nothing");
    public Task DeletePageAsync(string id) => Task.CompletedTask;
    public Task<PreparedAsk> PrepareAskAsync(CodexSignIn signIn, string model, CancellationToken ct) => throw new InvalidOperationException("the calendar app asks no model");
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

        // ----- reading events from ink, with no model -----
        var grid = new TimelineGeometry(200, 64 / 60.0, 7 * 60, 21 * 60, 120, 800); // 07:00 at y 200
        double Y(int h, int m) => 200 + ((h - 7) * 60 + m) * 64 / 60.0;
        List<(double, double)> Rect(double l, double t, double r, double b) => new() { (l, t), (r, t), (r, b), (l, b), (l + 2, t + 3) };
        List<(double, double)> Word(double x, double y) => new() { (x, y), (x + 8, y - 12), (x + 16, y), (x + 24, y - 10) };
        var boxed = InkEvents.Find(new[] { new InkPath(1, Rect(300, Y(14, 2), 600, Y(15, 28))), new InkPath(2, Word(350, Y(14, 40))), new InkPath(3, Word(390, Y(14, 42))) }, grid);
        check("a box over the hours is an event from its top to its bottom, snapped, with the writing in it as its title",
            boxed.Count == 1 && boxed[0].Start == 14 * 60 && boxed[0].End == 15 * 60 + 30 && boxed[0].Shape.SequenceEqual(new uint[] { 1 }) && boxed[0].Words.SequenceEqual(new uint[] { 2, 3 }));
        var lined = InkEvents.Find(new[] { new InkPath(1, new List<(double, double)> { (300, Y(9, 0)), (303, Y(9, 50)), (301, Y(10, 30)) }), new InkPath(2, Word(330, Y(9, 50))) }, grid);
        check("a line down the hours is an event too, titled by the writing to its right", lined.Count == 1 && lined[0].Start == 9 * 60 && lined[0].End == 10 * 60 + 30 && lined[0].Words.SequenceEqual(new uint[] { 2 }));
        var written = InkEvents.Find(new[] { new InkPath(1, Word(300, Y(12, 11))), new InkPath(2, Word(340, Y(12, 13))), new InkPath(3, Word(300, Y(17, 5))) }, grid);
        check("writing on its own is an hour from where each line of it starts", written.Count == 2 && written[0].Start == 12 * 60 && written[0].End == 13 * 60 && written[0].Words.Count == 2 && written[1].Start == 17 * 60);
        var two = InkEvents.Find(new[] { new InkPath(1, Rect(300, Y(8, 0), 600, Y(9, 0))), new InkPath(2, Rect(300, Y(11, 0), 600, Y(12, 0))), new InkPath(3, Word(400, Y(11, 30))) }, grid);
        check("several boxes are several events, each with its own writing (or none)", two.Count == 2 && two[0].Words.Count == 0 && two[1].Words.SequenceEqual(new uint[] { 3 }));
        check("ink away from the timeline is no event", InkEvents.Find(new[] { new InkPath(1, Rect(300, 20, 600, 90)), new InkPath(2, Word(300, Y(23, 0) + 400)) }, grid).Count == 0);
        var withItems = grid with { Items = new Dictionary<string, HitRect> { ["e1"] = new(130, Y(9, 0), 800, Y(9, 30)), ["e2"] = new(130, 150, 800, 180) } };
        var over = InkEvents.Find(new[] { new InkPath(1, Word(300, Y(9, 25))), new InkPath(2, Word(340, Y(9, 26))), new InkPath(3, Word(300, 172)) }, withItems);
        check("writing over an event already there is a new title for it, also over an all-day one above the hours",
            over.Count == 2 && over.Any(e => e.Item == "e1" && e.Words.SequenceEqual(new uint[] { 1, 2 })) && over.Any(e => e.Item == "e2" && e.Words.SequenceEqual(new uint[] { 3 })));
        var boxOver = InkEvents.Find(new[] { new InkPath(1, Rect(200, Y(8, 30), 700, Y(9, 45))), new InkPath(2, Word(300, Y(9, 25))) }, withItems);
        check("but writing in a box drawn over it is the box's", boxOver.Count == 1 && boxOver[0].Item is null && boxOver[0].Words.SequenceEqual(new uint[] { 2 }));
        check("a small closed letter is writing, not a box", InkEvents.Find(new[] { new InkPath(1, Rect(300, Y(10, 0), 320, Y(10, 15))) }, grid).Single().Shape.Count == 0);

        // ----- the calendar app -----
        var source = new FakeDaySource();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var t = (int h, int m) => today.ToDateTime(new TimeOnly(h, m));
        source.Add("cal|a", "Standup", t(9, 0), t(9, 30));
        var view = new FakeView();
        var data = new FakeDayData(source);
        var session = new PageSession(view, data, calendarDay: true);
        await session.StartAsync();
        var dayNote = session.Notes.SingleOrDefault(n => n.IsDay);
        check("the calendar app opens on today, with the day as a layout and nothing else, with no ChatGPT sign-in", session.Day == today && dayNote is { Drawings.Count: 1 } && session.Notes.Count == 1 && session.Index.Items.Count == 0 && view.Toasts.Count == 0);
        var drawing = dayNote!.Drawings[0];
        var item = Layout.Parse(drawing.Markup)!.Descendants().First(n => n.Tag == "box").Id!;

        // Moving an event moves it in the calendar, at once.
        bool ok = session.EditDrawing(dayNote, drawing, new[] { new DrawingChange(item, "move", To: $"d{today:yyyyMMdd}", At: "11:00") });
        check("moving an event changes the calendar straight away", ok && await Until(() => source.Moves.Contains("cal|a 11:00-11:30")));
        check("and nothing is waiting to be added", !view.State.CanAsk && !session.HasEdits);
        check("the day is read again afterwards", await Until(() => drawing.Markup.Contains("start=\"11:00\"")));
        item = Layout.Parse(drawing.Markup)!.Descendants().First(n => n.Tag == "box").Id!;
        session.EditDrawing(dayNote, drawing, new[] { new DrawingChange(item, "resize", End: "12:00") });
        check("resizing an event changes its end in the calendar", await Until(() => source.Moves.Contains("cal|a 11:00-12:00")));
        session.Undo();
        check("undo puts it back as it was, in the calendar", await Until(() => source.Moves.Count == 3 && source.Moves[^1] == "cal|a 11:00-11:30"));
        check("turning an event isn't a change of the calendar", !session.EditDrawing(dayNote, drawing, new[] { new DrawingChange(item, "rotate", Degrees: 10) }) && source.Moves.Count == 3);

        // Drawing an event adds it, with no model.
        var box = view.DrawPoints(Rect(300, Y(16, 0), 600, Y(17, 0)));
        var title = view.DrawPoints(Word(380, Y(16, 30)));
        var stray = view.DrawPoints(Word(300, Y(23, 0) + 500));
        session.StrokesCollected(new[] { box });
        session.StrokesCollected(new[] { title });
        session.StrokesCollected(new[] { stray });
        check("a drawing waits to be added", view.State.CanAsk);
        await session.AskAsync();
        check("Add puts the event drawn in the calendar, at the drawn times, with its title read from the writing", source.Events.Values.Any(e => e.Event.Title == "Gym" && e.Event.Start.DateTime == t(16, 0) && e.Event.End.DateTime == t(17, 0)) && view.TitlesAsked.SequenceEqual(new[] { "16:00–17:00" }));
        check("it shows on the day at once", drawing.Markup.Contains("Gym"));
        check("the ink it was drawn with comes off the page; other ink stays", view.Ink.Select(s => s.Id).SequenceEqual(new[] { stray }));
        check("it says what was added, and how to take it back", view.Toasts.Last().Contains("Added \"Gym\"") && view.Toasts.Last().Contains("Ctrl+Z"));
        session.Undo();
        check("undo deletes it from the calendar and brings the ink back", await Until(() => source.Deletes.Count == 1 && !drawing.Markup.Contains("Gym")) && view.Ink.Count == 3 && view.State.CanAsk);

        // A title that can't be read, and is not typed, skips the event.
        view.Title = _ => null;
        await session.AskAsync();
        check("an event left without a title isn't added, and its ink stays", !source.Events.Values.Any(e => e.Event.Title == "Gym") && view.Ink.Count == 3);
        view.Title = words => words.Count > 0 ? "Gym" : null;
        session.Undo(); // nothing to undo from that one: undoes nothing harmful
        view.Ink.Clear();
        // Writing over an event renames it.
        view.Timeline = view.Timeline! with { Items = Layout.Parse(drawing.Markup)!.Descendants().Where(n => n.Tag == "box" && n.Words() == "Standup").ToDictionary(n => n.Id!, _ => new HitRect(130, Y(11, 0), 800, Y(11, 30))) };
        view.Title = words => words.Count > 0 ? "Daily" : null;
        var rename = view.DrawPoints(Word(300, Y(11, 25)));
        session.StrokesCollected(new[] { rename });
        await session.AskAsync();
        check("writing over an event gives it the title written, in the calendar", source.Renames.SequenceEqual(new[] { "cal|a Daily" }) && view.TitlesAsked[^1] == "11:00–11:30 Standup" && !source.Events.ContainsKey("cal|new2"));
        check("the writing comes off, and the day shows the new title", view.Ink.Count == 0 && drawing.Markup.Contains("Daily") && view.Toasts.Last().Contains("Renamed \"Standup\" to \"Daily\""));
        session.Undo();
        check("undo gives it its old title back, and the writing", await Until(() => source.Renames.Count == 2 && drawing.Markup.Contains("Standup")) && view.Ink.Count == 1);
        source.Events["cal|a"] = (source.Events["cal|a"].Event, false);
        await session.RefreshDayAsync();
        view.Timeline = view.Timeline! with { Items = Layout.Parse(drawing.Markup)!.Descendants().Where(n => n.Tag == "box" && n.Words() == "Standup").ToDictionary(n => n.Id!, _ => new HitRect(130, Y(11, 0), 800, Y(11, 30))) };
        await session.AskAsync();
        check("an event of a calendar that can't be changed keeps its title, and says so", source.Renames.Count == 2 && view.Toasts.Last().Contains("can't be changed") && view.Ink.Count == 1);
        source.Events["cal|a"] = (source.Events["cal|a"].Event, true);
        view.Ink.Clear();
        view.Timeline = view.Timeline! with { Items = null };

        session.StrokesCollected(new[] { view.DrawPoints(Word(300, 60)) });
        await session.AskAsync();
        check("ink that marks no event says how to draw one", view.Toasts.Last().StartsWith("No event found"));
        view.Ink.Clear();

        // Another day.
        await session.MoveDayAsync(1);
        check("the next day starts afresh, without the ink", session.Day == today.AddDays(1) && view.Ink.Count == 0 && session.Notes.Count == 1 && !session.Notes[0].Drawings[0].Markup.Contains("Standup"));
        await session.TodayAsync();
        check("today comes back", session.Day == today && session.Notes[0].Drawings[0].Markup.Contains("Standup"));
    }
}

/// <summary>Choosing between what French and English handwriting recognizers read, with a dictionary of each.</summary>
static class HandwritingChecks
{
    static readonly HashSet<string> French = new(StringComparer.OrdinalIgnoreCase) { "déjeuner", "avec", "réunion", "équipe", "pain" };
    static readonly HashSet<string> English = new(StringComparer.OrdinalIgnoreCase) { "team", "lunch", "with", "pain", "sam" };
    static bool? Known(string language, string word) => language.StartsWith("fr") ? French.Contains(word) : language.StartsWith("en") ? English.Contains(word) : null;
    static IReadOnlyList<IReadOnlyList<string>> W(params string[][] words) => words;

    public static void Run(Action<string, bool> check)
    {
        // Written in French: the French reading is all real words, the English one isn't.
        var (g1, sure1) = HandwritingChoice.Rank(new[]
        {
            new HandwritingReading("fr-FR", W(new[] { "Réunion" }, new[] { "équipe" })),
            new HandwritingReading("en-US", W(new[] { "Reunion" }, new[] { "equips" })),
        }, Known);
        check("French writing reads as French, sure enough to use as it is", g1[0].Text == "Réunion équipe" && g1[0].Language == "fr-FR" && sure1 && g1.Count == 2);

        // Written in English: the English reading wins although French comes first.
        var (g2, sure2) = HandwritingChoice.Rank(new[]
        {
            new HandwritingReading("fr-FR", W(new[] { "Team" }, new[] { "lunche" })),
            new HandwritingReading("en-US", W(new[] { "Team" }, new[] { "lunch" })),
        }, Known);
        check("English writing reads as English, though French is the main language", g2[0].Text == "Team lunch" && g2[0].Language == "en-US" && sure2);

        // A word the dictionary doesn't know is swapped for an alternative guess it does know.
        var (g3, sure3) = HandwritingChoice.Rank(new[]
        {
            new HandwritingReading("fr-FR", W(new[] { "Déjeumer", "Déjeuner" }, new[] { "avec" }, new[] { "Sam" })),
            new HandwritingReading("en-US", W(new[] { "Dejeumer" }, new[] { "arec", "with" }, new[] { "Sam" })),
        }, Known);
        check("a word not in the dictionary gives way to another guess that is", g3[0].Text == "Déjeuner avec Sam" && g3.Any(g => g.Text == "Déjeumer avec Sam"));
        check("a mixed title isn't sure, so it is asked, with the best guess first", !sure3);

        // Both read the same: sure, whatever the dictionaries say.
        var (g4, sure4) = HandwritingChoice.Rank(new[]
        {
            new HandwritingReading("fr-FR", W(new[] { "Zorglub" })),
            new HandwritingReading("en-US", W(new[] { "Zorglub" })),
        }, Known);
        check("readings that agree are one guess, and sure", g4.Count == 1 && sure4);

        // No dictionary at all: the main language comes first, and the user is asked.
        var (g5, sure5) = HandwritingChoice.Rank(new[]
        {
            new HandwritingReading("fr-FR", W(new[] { "Piscine" })),
            new HandwritingReading("en-US", W(new[] { "Piscina" })),
        }, (_, _) => null);
        check("with no dictionary, the main language comes first, and it is asked", g5[0].Text == "Piscine" && g5[1].Text == "Piscina" && !sure5);
        check("two real readings in different words aren't sure", !HandwritingChoice.Rank(new[]
        {
            new HandwritingReading("fr-FR", W(new[] { "Pain" })),
            new HandwritingReading("en-US", W(new[] { "Team" })),
        }, Known).Sure);
        check("nothing read is no guess", HandwritingChoice.Rank(Array.Empty<HandwritingReading>(), Known).Guesses.Count == 0);
        check("times and numbers count as real words", HandwritingChoice.Rank(new[] { new HandwritingReading("en-US", W(new[] { "Lunch" }, new[] { "12:30" })) }, Known).Guesses[0].Score == 1);

        check("recognizers' languages are read from their names, in English or their own language",
            HandwritingChoice.LanguageOf("Microsoft English (US) Handwriting Recognizer") == "en-US"
            && HandwritingChoice.LanguageOf("Microsoft English (United Kingdom) Handwriting Recognizer") == "en-GB"
            && HandwritingChoice.LanguageOf("Microsoft Reconnaissance d'écriture manuscrite – Français") == "fr-FR"
            && HandwritingChoice.LanguageOf("Microsoft Handwriting Recognizer - Klingon") is null);
    }
}
