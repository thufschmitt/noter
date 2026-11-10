using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Longhand.Services;

/// <summary>A page with no screen: ink is a list of heights, and measuring a note is a guess at how many lines its words take.</summary>
sealed class FakeView : IPageView
{
    public List<StrokeSpan> Ink = new();
    uint _next = 1;
    public readonly List<string> Toasts = new();
    public PageState State;
    public double? Idle;
    public int Snapshots, Shown;
    public SnapshotContext? LastContext;
    public IReadOnlyList<uint>? LastIds;
    public readonly List<NoteModel> Visible = new();

    public uint Draw(double top, double bottom)
    {
        var id = _next++;
        Ink.Add(new StrokeSpan(id, top, bottom));
        return id;
    }

    public void SeeIds(IEnumerable<uint> ids) => _next = Math.Max(_next, ids.DefaultIfEmpty(0u).Max() + 1);

    public IReadOnlyList<StrokeSpan> Strokes() => Ink.ToList();
    public void ShiftStrokes(double anchor, double delta, IReadOnlySet<uint> keep)
    {
        for (int i = 0; i < Ink.Count; i++)
            if (!keep.Contains(Ink[i].Id) && Ink[i].Top >= anchor) Ink[i] = Ink[i] with { Top = Ink[i].Top + delta, Bottom = Ink[i].Bottom + delta };
    }
    public bool DeleteStroke(uint id) => Ink.RemoveAll(s => s.Id == id) > 0;
    public void RestoreStrokes(object token) => Ink.AddRange((List<StrokeSpan>)token);
    public void ClearStrokes() => Ink.Clear();

    public void NoteAdded(NoteModel note) => Visible.Add(note);
    public void NoteChanged(NoteModel note) { }
    public void NoteRemoved(NoteModel note) => Visible.Remove(note);
    public void DrawingAdded(NoteModel note, DrawingModel drawing) { }
    public void DrawingsCleared(NoteModel note) { }
    public async Task MeasureAsync(NoteModel note)
    {
        await Task.Yield(); // a real view may take its time
        note.Lines = Math.Max(note.Drawings.Count > 0 && note.Shown.Length == 0 ? 0 : 1, (int)Math.Ceiling(note.Shown.Length / 30.0));
        foreach (var d in note.Drawings) d.Lines = 6;
    }
    public void Place(NoteModel note) { }
    public void ContentChanged() { }
    public void EnsureVisible(double bottom) { }
    public void PageShown() => Shown++;

    public Task<byte[]> RenderSnapshotAsync(IReadOnlyList<uint> strokeIds, SnapshotContext? context)
    {
        Snapshots++;
        LastIds = strokeIds;
        LastContext = context;
        return Task.FromResult(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="));
    }

    public Task<string?> RequestApiKeyAsync() => Task.FromResult<string?>(null);
    public void Toast(string message, double seconds = 3.5) => Toasts.Add(message);
    public void StateChanged(PageState state) => State = state;
    public void IdleProgress(double? progress) => Idle = progress;
}

/// <summary>Conversations kept in memory, with the ink of each saved alongside its notes.</summary>
sealed class FakeData : IPageData
{
    readonly FakeView _view;
    readonly string _baseUrl;
    string _indexJson = "";
    public readonly Dictionary<string, (List<SavedNote> Notes, List<StrokeSpan> Ink)> Pages = new();
    public string? ApiKey { get; set; } = "sk-ant-test";

    public FakeData(FakeView view, string baseUrl)
    {
        _view = view;
        _baseUrl = baseUrl;
    }

    public Task<ConversationIndex> LoadIndexAsync() => Task.FromResult(ConversationIndex.Parse(_indexJson));
    public Task SaveIndexAsync(ConversationIndex index) { _indexJson = index.ToJson(); return Task.CompletedTask; }
    public Task<List<SavedNote>?> LoadPageAsync(string id)
    {
        if (!Pages.TryGetValue(id, out var page)) return Task.FromResult<List<SavedNote>?>(null);
        _view.Ink.AddRange(page.Ink);
        _view.SeeIds(page.Ink.Select(s => s.Id));
        return Task.FromResult<List<SavedNote>?>(page.Notes);
    }
    public Task SavePageAsync(string id, IReadOnlyList<SavedNote> notes) { Pages[id] = (notes.ToList(), _view.Ink.ToList()); return Task.CompletedTask; }
    public Task DeletePageAsync(string id) { Pages.Remove(id); return Task.CompletedTask; }
    public Task<PreparedAsk> PrepareAskAsync(string apiKey, System.Threading.CancellationToken ct) =>
        Task.FromResult(new PreparedAsk(new ClaudeService(apiKey, model: "claude-haiku-5-5", baseUrl: _baseUrl), Array.Empty<LegendEntry>(), Array.Empty<string>()));
}

/// <summary>Checks for the page itself: asking, annotating, undo, conversations and saving, run against a fake screen and a fake API.</summary>
static class PageChecks
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
        await using var api = new FakeAnthropic { AskForTool = false };
        var view = new FakeView();
        var data = new FakeData(view, api.BaseUrl);
        var session = new PageSession(view, data);

        // ----- a fresh page -----
        await session.StartAsync();
        check("a new page has only the hint, and nothing to answer", session.Notes.Count == 1 && session.Notes[0].IsHint && session.PendingCount == 0 && session.Index.Items.Count == 1);
        check("a blank page is what New conversation would give", session.IsBlankNow);
        check("the page is shown once on start", view.Shown == 1 && !view.State.CanAsk);

        // ----- writing and asking -----
        var first = view.Draw(160, 200);
        session.StrokesCollected(new[] { first });
        check("writing makes the page askable and starts the pause", view.State.CanAsk && session.PendingCount == 1 && view.Idle is not null && view.State.Counting);
        session.StrokeStarted();
        check("putting the pen down again stops the pause", view.Idle is null && !view.State.Counting);

        await session.AskAsync();
        var reply = session.Notes.Single(n => !n.IsHint);
        check("the reply lands on the rule after the ink", reply.Y == 200 && reply.Text == "You have a standup at 9." && reply.Done && reply.Shown == reply.Text);
        check("the reply was heard and measured", reply.Heard == "what is on tomorrow" && reply.Lines == 1 && view.Visible.Contains(reply));
        check("the answered ink is no longer pending, and the button is idle", session.PendingCount == 0 && !view.State.Busy && !view.State.CanAsk);
        check("the first snapshot is of the new ink alone", view.LastContext is null && view.LastIds!.SequenceEqual(new[] { first }));
        check("the history holds the exchange", session.History().Count == 1 && session.History()[0].Heard == "what is on tomorrow");

        check("the page is saved a second after the change", await Until(() => data.Pages.Count == 1 && data.Pages.Values.First().Notes.Any(n => !n.IsHint), 3000));
        check("the conversation is titled after what was heard", session.Index.Items[0].AutoTitle == "what is on tomorrow" && !session.Index.Items[0].Blank);

        // ----- a second question, further down -----
        view.Draw(400, 440);
        session.StrokesCollected(new[] { view.Ink[^1].Id });
        await session.AskAsync();
        var second = session.Notes.Where(n => !n.IsHint).OrderBy(n => n.Y).Last();
        check("a later question is answered at the end of the page", second.Y == 440 && session.Notes.Count(n => !n.IsHint) == 2);
        check("earlier turns are sent as text", api.Requests.Last(r => r.Path.EndsWith("/messages")).Body.Contains("(earlier handwriting, transcribed) what is on tomorrow"));

        // ----- writing over an earlier reply -----
        var over = view.Draw(205, 230);
        session.StrokesCollected(new[] { over });
        await session.AskAsync();
        check("ink over a reply sends the reply along", view.LastContext is { } ctx && ctx.Replies.Contains(reply) && ctx.EarlierInk.Contains(first) && !ctx.EarlierInk.Contains(over));
        check("Claude is told the picture has earlier parts", api.Requests.Last(r => r.Path.EndsWith("/messages")).Body.Contains("grey ink"));
        check("that answer is at the end, away from the annotation", session.Notes.Where(n => !n.IsHint).Max(n => n.Y) > 440);

        // ----- undo -----
        int before = view.Ink.Count;
        var scribble = view.Draw(900, 930);
        session.StrokesCollected(new[] { scribble });
        session.Undo();
        check("undoing a stroke removes it and what it was waiting for", view.Ink.Count == before && session.PendingCount == 0);
        var erased = view.Ink.First(s => s.Id == first);
        view.Ink.Remove(erased);
        session.StrokesErased(new[] { first }, new List<StrokeSpan> { erased });
        check("erasing is noticed", !view.Ink.Any(s => s.Id == first));
        session.Undo();
        check("undoing an erase puts the stroke back", view.Ink.Any(s => s.Id == first));

        // ----- a failing request -----
        var stroke = view.Draw(1200, 1240);
        session.StrokesCollected(new[] { stroke });
        int notesBefore = session.Notes.Count;
        api.FailStatus = 401;
        await session.AskAsync();
        api.FailStatus = 0;
        check("a rejected key says so and leaves no half answer", view.Toasts.Any(t => t.Contains("rejected")) && session.Notes.Count == notesBefore && !view.State.Busy);
        check("the writing can be asked again", session.PendingCount == 1 && view.State.CanAsk);

        // ----- pause-to-ask -----
        view.Toasts.Clear();
        session.ScheduleIdle();
        check("the pause counts down and then asks by itself", await Until(() => session.Notes.Count == notesBefore + 1 && !session.Busy, 6000) && session.PendingCount == 0 && session.Notes.All(n => n.Done));

        // ----- conversations -----
        var firstId = session.CurrentId;
        int answers = session.Notes.Count(n => !n.IsHint);
        int inkCount = view.Ink.Count;
        await session.NewConversationAsync();
        check("a new conversation starts on an empty page", session.CurrentId != firstId && view.Ink.Count == 0 && session.Notes.Count == 1 && session.Notes[0].IsHint && session.Index.Items.Count == 2);
        check("the one left was saved whole", data.Pages[firstId].Notes.Count(n => !n.IsHint) == answers && data.Pages[firstId].Ink.Count == inkCount);

        await session.NewConversationAsync();
        check("asking for another new one on a blank page changes nothing", session.Index.Items.Count == 2);

        await session.OpenConversationAsync(firstId);
        check("opening it again brings back its ink and replies", session.CurrentId == firstId && view.Ink.Count == inkCount && session.Notes.Count(n => !n.IsHint) == answers);
        check("the blank one is dropped on the way", session.Index.Items.Count == 1);
        check("what was answered is not asked again", session.PendingCount == 0);

        // ----- changing conversation while Claude is answering -----
        await session.NewConversationAsync();
        var busyId = session.CurrentId;
        var wrote = view.Draw(160, 200);
        session.StrokesCollected(new[] { wrote });
        api.ReplyDelayMs = 1200;
        var asking = session.AskAsync();
        check("the page is busy while Claude answers", await Until(() => session.Busy, 2000));
        await session.OpenConversationAsync(firstId);
        await asking;
        api.ReplyDelayMs = 0;
        check("leaving drops the half-written answer", session.CurrentId == firstId && !session.Busy && !view.Toasts.Any(t => t.Contains("couldn't answer")));
        check("the conversation left keeps its writing as not yet answered", data.Pages.TryGetValue(busyId, out var left) && left.Ink.Count == 1 && left.Notes.All(n => n.IsHint));
        await session.OpenConversationAsync(busyId);
        check("and asks for it again when reopened", session.PendingCount == 1);

        // ----- deleting -----
        await session.DeleteConversationAsync(busyId);
        check("deleting the open conversation opens another", session.CurrentId == firstId && !session.Index.Items.Any(c => c.Id == busyId) && !data.Pages.ContainsKey(busyId));
        await session.RenameConversationAsync(firstId, "  My   notes ");
        check("a name the user gave is kept", session.Index.Find(firstId)!.CustomTitle == "My notes" && session.Index.Find(firstId)!.Title == "My notes");

        // ----- no key -----
        data.ApiKey = null;
        var late = view.Draw(2000, 2040);
        session.StrokesCollected(new[] { late });
        check("without a key there is no pause to wait for", view.Idle is null);
        view.Toasts.Clear();
        await session.AskAsync();
        check("asking without a key says what to do", view.Toasts.Any(t => t.Contains("API key")) && session.PendingCount == 1);
    }
}
