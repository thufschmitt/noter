using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anthropic.Exceptions;
using static Longhand.Services.PageLayout;

namespace Longhand.Services;

/// <summary>
/// The notebook page and what happens on it: strokes waiting to be answered, Claude's notes, asking, undo, saving and the
/// conversations. Everything here is the same on every platform; what the page looks like is the <see cref="IPageView"/>'s
/// business, and where things are kept is the <see cref="IPageData"/>'s.
///
/// Call it from one thread at a time (the UI thread, or a single-threaded context on a server). Methods that await continue where
/// they started.
/// </summary>
public sealed class PageSession
{
    abstract record UndoStep;
    sealed record AddedStroke(uint StrokeId) : UndoStep;
    sealed record ErasedStrokes(object Token) : UndoStep;

    /// <summary>One answer being written, so the layout updates that follow it can be merged.</summary>
    sealed class Answer
    {
        public required NoteModel Note;
        public required HashSet<uint> Ids;
        public double Occupied;
        /// <summary>The note the user typed, which this answers (it sits just above), and the room it takes.</summary>
        public NoteModel? Typed;
        public double TypedOccupied;
        public bool Pumping, Again;
        public Task Pump = Task.CompletedTask;
    }

    readonly IPageView _view;
    readonly IPageData _data;
    readonly List<NoteModel> _notes = new();
    readonly HashSet<uint> _pending = new();       // strokes written since Claude last answered
    readonly Stack<UndoStep> _undo = new();
    readonly SemaphoreSlim _saveLock = new(1, 1);
    CancellationTokenSource? _askCts, _idleCts, _saveCts;
    bool _busy, _loaded, _dirty, _switching, _discardOnCancel;
    ConversationIndex _index = new();
    string _currentId = "";

    public PageSession(IPageView view, IPageData data)
    {
        _view = view;
        _data = data;
    }

    public ConversationIndex Index => _index;
    public string CurrentId => _currentId;
    public bool Busy => _busy;
    public int PendingCount => _pending.Count;
    public IReadOnlyList<NoteModel> Notes => _notes;
    public bool IsBlankNow => _view.Strokes().Count == 0 && !_notes.Any(n => !n.IsHint);

    /// <summary>Opens the conversation that was open last time.</summary>
    public async Task StartAsync()
    {
        _index = await _data.LoadIndexAsync();
        var start = _index.Find(_index.CurrentId) ?? _index.Newest().FirstOrDefault() ?? _index.Create(DateTimeOffset.UtcNow);
        await ShowConversationAsync(start.Id);
        await _data.SaveIndexAsync(_index);
        _loaded = true;
        if (_data.ApiKey is null)
            _view.Toast("Add your Anthropic API key (gear button) so Claude can answer. You can write in the meantime.", 6);
    }

    /// <summary>The bottom of the lowest thing on the page, which the sheet has to reach past.</summary>
    public double ContentBottom()
    {
        double bottom = Top;
        foreach (var s in _view.Strokes()) bottom = Math.Max(bottom, s.Bottom);
        foreach (var n in _notes) bottom = Math.Max(bottom, n.Bottom);
        return bottom;
    }

    void UpdateState() => _view.StateChanged(new PageState(_busy, _busy || _pending.Count > 0, _idleCts is not null));

    /// <summary>The page was resized, so every note is measured again at the new width.</summary>
    public async Task RelayoutAsync()
    {
        foreach (var n in _notes.ToList())
        {
            try { await LayoutAsync(n); }
            catch (Exception) { /* a view that can't measure keeps the last layout */ }
        }
        _view.ContentChanged();
    }

    // ---------- ink events ----------

    public void StrokeStarted() => CancelIdle();

    public void StrokesCollected(IEnumerable<uint> ids)
    {
        foreach (var id in ids)
        {
            _pending.Add(id);
            _undo.Push(new AddedStroke(id));
        }
        _view.ContentChanged();
        UpdateState();
        ScheduleIdle();
        ScheduleSave();
    }

    /// <param name="restore">What the view needs to put the strokes back if the erasing is undone.</param>
    public void StrokesErased(IEnumerable<uint> ids, object restore)
    {
        bool any = false;
        foreach (var id in ids)
        {
            _pending.Remove(id);
            any = true;
        }
        if (any) _undo.Push(new ErasedStrokes(restore));
        UpdateState();
        ScheduleIdle();
        ScheduleSave();
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        switch (_undo.Pop())
        {
            case AddedStroke added:
                _view.DeleteStroke(added.StrokeId);
                _pending.Remove(added.StrokeId);
                break;
            case ErasedStrokes erased:
                _view.RestoreStrokes(erased.Token);
                break;
        }
        UpdateState();
        ScheduleSave();
    }

    // ---------- pause-to-ask ----------

    public void ScheduleIdle()
    {
        CancelIdle();
        if (_pending.Count == 0 || _busy || _data.ApiKey is null) return;
        var cts = _idleCts = new CancellationTokenSource();
        _view.IdleProgress(0);
        UpdateState();
        _ = RunIdleAsync(cts);
    }

    public void CancelIdle()
    {
        var cts = _idleCts;
        _idleCts = null;
        if (cts is null) return;
        cts.Cancel();
        cts.Dispose();
        _view.IdleProgress(null);
        UpdateState();
    }

    async Task RunIdleAsync(CancellationTokenSource cts)
    {
        var start = DateTime.UtcNow;
        try
        {
            while (true)
            {
                await Task.Delay(40, cts.Token);
                double progress = (DateTime.UtcNow - start).TotalMilliseconds / PauseBeforeAsking.TotalMilliseconds;
                if (progress >= 1) break;
                _view.IdleProgress(progress);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (_idleCts != cts) return;
        CancelIdle();
        await AskAsync();
    }

    // ---------- asking Claude ----------

    /// <summary>The Ask button: stops the answer being written, or asks for one.</summary>
    public async Task AskOrStopAsync()
    {
        if (_busy) _askCts?.Cancel();
        else await AskAsync();
    }

    public Task AskAsync() => AskCoreAsync(null);

    /// <summary>Sends a note typed with the keyboard, together with any writing that hasn't been answered yet.</summary>
    public async Task TypeAsync(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return;
        if (text.Length > MaxTyped)
        {
            _view.Toast($"That note is too long to send (the most is {MaxTyped} characters).");
            _view.RestoreTyped(text);
            return;
        }
        if (_busy || _switching)
        {
            _view.Toast("Claude is still answering. Send it again in a moment.");
            _view.RestoreTyped(text);
            return;
        }
        await AskCoreAsync(text);
    }

    async Task AskCoreAsync(string? typed)
    {
        CancelIdle();
        if (_busy || (_pending.Count == 0 && typed is null)) return;

        var conversation = _currentId;
        var key = _data.ApiKey ?? await _view.RequestApiKeyAsync();
        if (_switching || conversation != _currentId) return; // moved to another conversation meanwhile
        if (key is null)
        {
            _view.Toast("Add your Anthropic API key (gear button) to get answers.");
            if (typed is not null) _view.RestoreTyped(typed);
            return;
        }

        var ids = new HashSet<uint>(_pending);
        var spans = _view.Strokes().Where(s => ids.Contains(s.Id)).ToList();
        if (spans.Count == 0 && typed is null)
        {
            _pending.Clear();
            UpdateState();
            return;
        }
        var strokeIds = spans.Select(s => s.Id).ToList();

        // Writing over earlier replies (annotating them) sends those replies along, so Claude sees what the ink refers to.
        var context = spans.Count > 0 ? ContextAround(spans, ids) : null;

        // A typed note on its own has no picture.
        byte[]? png = null;
        try
        {
            if (spans.Count > 0)
            {
                try
                {
                    png = await _view.RenderSnapshotAsync(strokeIds, context);
                }
                catch (Exception) when (context is not null)
                {
                    // The earlier replies are a bonus: rather than not asking, send the new ink alone.
                    context = null;
                    _view.Toast("Claude will only see your new ink this time.");
                    png = await _view.RenderSnapshotAsync(strokeIds, null);
                }
            }
        }
        catch (Exception ex)
        {
            _view.Toast("Couldn't capture the handwriting: " + ex.Message);
            if (typed is not null) _view.RestoreTyped(typed);
            return;
        }
        if (_switching || conversation != _currentId) return;
        _pending.Clear();

        // What was typed goes on the page first, below the writing, and the answer follows it.
        NoteModel? typedNote = null;
        double typedOccupied = 0;
        if (typed is not null)
        {
            // The hint says what to do on an empty page, and the typed note takes its place.
            foreach (var hint in _notes.Where(n => n.IsHint).ToList()) RemoveNote(hint);
            typedNote = await AddNoteAsync(EndOfPage(), typed, isHint: false, done: true, typed: true);
            typedOccupied = typedNote.Span;
            ShiftBelow(typedNote.Y, typedOccupied, typedNote, ids);
        }

        // The answer goes at the end of the page, so it never lands in the middle of what is already there. Anything the user
        // writes below it while it streams is pushed down out of the way.
        var note = await AddNoteAsync(EndOfPage(), "…", isHint: false, done: false);
        note.Text = "";
        var answer = new Answer { Note = note, Ids = ids, Occupied = note.Span, Typed = typedNote, TypedOccupied = typedOccupied };
        ShiftBelow(note.Y, answer.Occupied, note, ids);
        _view.EnsureVisible(note.Bottom);

        bool answered = false;
        PreparedAsk? prepared = null;
        _busy = true;
        _askCts = new CancellationTokenSource();
        var ct = _askCts.Token;
        UpdateState();
        try
        {
            // Connected apps whose sign-in can't be renewed are skipped; say so, since Claude won't have them.
            prepared = await _data.PrepareAskAsync(key, ct);
            if (prepared.ExpiredApps.Count > 0) _view.Toast("Sign in again to " + string.Join(", ", prepared.ExpiredApps) + " (apps button).", 6);

            var (summary, after) = Conversation(prepared.Claude.Model);
            var result = await prepared.Claude.AskAsync(png, after.Select(ToTurn).ToList(), progress =>
            {
                note.Heard = progress.Heard;
                note.Text = progress.Body;
                bool hasDrawing = progress.Drawing || progress.Drawings is { Count: > 0 };
                note.Shown = progress.Body.Length > 0 ? progress.Body : progress.Drawing ? "drawing…" : hasDrawing ? "" : "…";
                _view.NoteChanged(note);
                AddNewDrawings(note, progress.Drawings);
                RequestGrow(answer);
            }, ct, status =>
            {
                // A tool is running: show what it is doing in place of the reply, which is written after it finishes.
                note.Text = "";
                ClearDrawings(note);
                note.Shown = status;
                _view.NoteChanged(note);
                RequestGrow(answer);
            }, hasContext: context is not null, legend: prepared.Legend, typed: typed, summary: summary);

            if (result.SummaryRejected)
                foreach (var n in _notes.Where(n => n.Summary?.Model == prepared.Claude.Model)) n.Summary = null;

            note.Heard = result.Heard;
            AddNewDrawings(note, result.Drawings);
            note.Text = result.Outcome switch
            {
                ReplyOutcome.Refused when result.Body.Length == 0 && note.Drawings.Count == 0 => "I can't help with that one.",
                ReplyOutcome.Truncated => result.Body + " …",
                _ => result.Body.Length > 0 || note.Drawings.Count > 0 ? result.Body : "I couldn't come up with a reply. Try writing it again?",
            };
            note.Done = true;
            note.Shown = note.Text;
            _view.NoteChanged(note);
            await FlushGrowAsync(answer);
            answered = true;
        }
        catch (OperationCanceledException)
        {
            if ((note.Text.Length > 0 || note.Drawings.Count > 0) && !_discardOnCancel) await FinishPartialAsync(answer);
            else await DiscardAsync(answer);
        }
        catch (AnthropicUnauthorizedException)
        {
            await DiscardAsync(answer);
            _view.Toast("Anthropic rejected the API key. Check it with the gear button, then tap Ask.", 6);
        }
        catch (AnthropicRateLimitException)
        {
            await DiscardAsync(answer);
            _view.Toast("Too many requests right now. Wait a moment, then tap Ask.", 6);
        }
        catch (Exception ex)
        {
            await DiscardAsync(answer);
            _view.Toast("Claude couldn't answer: " + ex.Message + " Tap Ask to try again.", 7);
        }
        finally
        {
            _busy = false;
            _askCts?.Dispose();
            _askCts = null;
            UpdateState();
            ScheduleSave();
            // Only re-arm the pause timer after a good answer, so a failing request is never retried in a loop.
            if (answered && _pending.Count > 0) ScheduleIdle();
        }
        if (answered && prepared is not null) _ = CompactAsync(prepared.Claude);
    }

    bool _compacting;

    /// <summary>
    /// Once a long run of turns follows the page summary (or the page has no summary yet), folds all but the latest few into a new
    /// summary with the API's compaction, and keeps it on the last note it covers. Runs after an answer, so no answer waits for it;
    /// if it fails, the next answer is sent the latest turns as they are.
    /// </summary>
    async Task CompactAsync(ClaudeService claude)
    {
        if (_compacting) return;
        var (summary, after) = Conversation(claude.Model);
        if (after.Count < ClaudeService.CompactAt) return;
        var fold = after.Take(after.Count - ClaudeService.KeepRecent).ToList();
        var page = _currentId;
        _compacting = true;
        try
        {
            var made = await claude.CompactAsync(summary, fold.Select(ToTurn).ToList(), default);
            if (made is null || page != _currentId || !_notes.Contains(fold[^1])) return;
            fold[^1].Summary = made;
            ScheduleSave();
        }
        catch (Exception)
        {
            // Not needed for the answer that was just written; the next answer will try again.
        }
        finally
        {
            _compacting = false;
        }
    }

    /// <summary>The exchanges on the page, top to bottom.</summary>
    List<NoteModel> Exchanges() => _notes
        .Where(n => !n.IsHint && !n.IsTyped && n.Done && n.HasContent)
        .OrderBy(n => n.Y)
        .ToList();

    /// <summary>The latest page summary this model can be sent, and the exchanges after it (all of them when there is none).</summary>
    (PageSummary? Summary, List<NoteModel> After) Conversation(string model)
    {
        var all = Exchanges();
        int last = all.FindLastIndex(n => n.Summary?.Model == model);
        return last < 0 ? (null, all) : (all[last].Summary, all.Skip(last + 1).ToList());
    }

    static Turn ToTurn(NoteModel n) =>
        new(n.Heard.Length > 0 ? n.Heard : "(unclear handwriting)", (n.Text + string.Concat(n.Drawings.Select(d => "\n" + Html.Text(d.Markup)))).Trim());

    /// <summary>The first free rule below all the ink and all the replies.</summary>
    double EndOfPage()
    {
        double ink = _view.Strokes().Select(s => s.Bottom).DefaultIfEmpty(Top).Max() - LineHeight * 0.3;
        double notes = _notes.Where(n => !n.IsHint).Select(n => n.Bottom).DefaultIfEmpty(Top).Max();
        return RuleAtOrBelow(Math.Max(ink, notes));
    }

    /// <summary>
    /// When the new ink overlaps earlier replies, what Claude should be shown of the page around it: those replies, and the
    /// writing they answered. Null when the ink simply follows the page.
    /// </summary>
    SnapshotContext? ContextAround(List<StrokeSpan> strokes, HashSet<uint> ids)
    {
        var replies = _notes.Where(n => !n.IsHint && !n.IsTyped && n.Done && n.HasContent).OrderBy(n => n.Y).ToList();
        var band = AnnotationContext.Find(
            strokes.Min(s => s.Top),
            strokes.Max(s => s.Bottom),
            replies.Select(n => (n.Y, n.Y + n.Covered)).ToList(),
            Top);
        if (band is not { } found) return null;

        var earlier = _view.Strokes()
            .Where(s => !ids.Contains(s.Id) && s.Top < found.To && s.Bottom > found.From)
            .Select(s => s.Id)
            .ToList();
        return new SnapshotContext(earlier, found.Replies.Select(i => replies[i]).ToList());
    }

    /// <summary>What Claude is told of the earlier exchanges on the page, top to bottom.</summary>
    public IReadOnlyList<Turn> History() => Exchanges().Select(ToTurn).ToList();

    // ----- keeping the layout in step with a growing answer -----

    /// <summary>Measuring can take a while (a browser has to do it), so updates that arrive meanwhile are merged into one more round.</summary>
    void RequestGrow(Answer a)
    {
        a.Again = true;
        if (a.Pumping) return;
        a.Pumping = true;
        a.Pump = PumpAsync(a);
    }

    async Task PumpAsync(Answer a)
    {
        try
        {
            while (a.Again)
            {
                a.Again = false;
                await GrowAsync(a);
            }
        }
        finally
        {
            a.Pumping = false;
        }
    }

    Task FlushGrowAsync(Answer a)
    {
        RequestGrow(a);
        return a.Pump;
    }

    async Task GrowAsync(Answer a)
    {
        var note = a.Note;
        try { await LayoutAsync(note); }
        catch (Exception) { return; } // a view that can't measure just keeps the last layout
        if (!_notes.Contains(note)) return; // discarded meanwhile
        if (note.Span != a.Occupied)
        {
            ShiftBelow(note.Y + a.Occupied, note.Span - a.Occupied, note, a.Ids);
            a.Occupied = note.Span;
        }
        _view.EnsureVisible(note.Bottom);
    }

    async Task FinishPartialAsync(Answer a)
    {
        var note = a.Note;
        if (note.Text.Length > 0) note.Text += " …";
        note.Shown = note.Text;
        note.Done = true;
        _view.NoteChanged(note);
        await FlushGrowAsync(a);
    }

    /// <summary>Removes an answer that never arrived and gives the writing back so it can be asked again.</summary>
    async Task DiscardAsync(Answer a)
    {
        await a.Pump; // let a measurement in flight finish first
        RemoveNote(a.Note);
        ShiftBelow(a.Note.Y + a.Occupied, -a.Occupied, null, a.Ids);
        if (a.Typed is { } typed)
        {
            // The words go back to where they were typed, unless the user is leaving the conversation (then they go with it).
            RemoveNote(typed);
            ShiftBelow(typed.Y + a.TypedOccupied, -a.TypedOccupied, null, a.Ids);
            if (!_discardOnCancel) _view.RestoreTyped(typed.Text);
        }
        foreach (var s in _view.Strokes()) if (a.Ids.Contains(s.Id)) _pending.Add(s.Id);
    }

    // ---------- notes ----------

    async Task<NoteModel> AddNoteAsync(double y, string text, bool isHint, bool done, bool typed = false)
    {
        var note = new NoteModel { Y = y, IsHint = isHint, IsTyped = typed, Done = done, Text = text, Shown = text };
        _notes.Add(note);
        _view.NoteAdded(note);
        await LayoutAsync(note);
        return note;
    }

    async Task LayoutAsync(NoteModel note)
    {
        await _view.MeasureAsync(note);
        _view.Place(note);
    }

    void RemoveNote(NoteModel note)
    {
        _view.NoteRemoved(note);
        _notes.Remove(note);
    }

    /// <summary>Pushes everything below <paramref name="anchor"/> down (or up) to make room for an answer.</summary>
    void ShiftBelow(double anchor, double delta, NoteModel? except, HashSet<uint>? keepStrokes)
    {
        if (delta == 0) return;
        _view.ShiftStrokes(anchor, delta, keepStrokes ?? new HashSet<uint>());
        foreach (var n in _notes)
        {
            if (n == except || n.Y < anchor) continue;
            n.Y += delta;
            _view.Place(n);
        }
        _view.ContentChanged();
    }

    /// <summary>Adds one of Claude's SVG drawings under the note's words. False when the drawing isn't safe or sensible to show.</summary>
    bool AddDrawing(NoteModel note, string raw)
    {
        if (note.Drawings.Count >= Html.MaxBlocks || Html.Sanitize(raw) is not { } block) return false;
        // The view works out its shape when it lays the block out at the page's width.
        var drawing = new DrawingModel { Markup = block, Alt = Html.Title(block) };
        note.Drawings.Add(drawing);
        _view.DrawingAdded(note, drawing);
        return true;
    }

    /// <summary>Takes in the drawings of a reply that haven't been added yet.</summary>
    void AddNewDrawings(NoteModel note, IReadOnlyList<string>? drawings)
    {
        if (drawings is null) return;
        for (; note.DrawingsSeen < drawings.Count; note.DrawingsSeen++)
            if (!AddDrawing(note, drawings[note.DrawingsSeen])) _view.Toast("A table or drawing from Claude couldn't be shown.");
    }

    void ClearDrawings(NoteModel note)
    {
        _view.DrawingsCleared(note);
        note.Drawings.Clear();
        note.DrawingsSeen = 0;
    }

    // ---------- conversations ----------

    /// <summary>Empties the page. What was on it must have been saved already.</summary>
    void ResetPage()
    {
        CancelIdle();
        _view.ClearStrokes();
        foreach (var n in _notes.ToList()) RemoveNote(n);
        _pending.Clear();
        _undo.Clear();
    }

    /// <summary>Loads a conversation's page and makes it the open one.</summary>
    async Task ShowConversationAsync(string id)
    {
        ResetPage();
        _currentId = id;
        _index.CurrentId = id;

        var saved = await _data.LoadPageAsync(id);
        if (saved is { Count: > 0 })
            foreach (var n in saved)
            {
                var note = await AddNoteAsync(n.Y, n.Text, n.IsHint, done: true, typed: n.Typed);
                note.Heard = n.Heard;
                note.Summary = n.Summary;
                foreach (var svg in n.Drawings ?? new List<string>()) AddDrawing(note, svg);
                note.DrawingsSeen = note.Drawings.Count;
                await LayoutAsync(note);
            }
        else if (_view.Strokes().Count == 0)
            await AddNoteAsync(Top, HintText, isHint: true, done: true);

        // Writing below the last reply hasn't been answered yet (this survives a restart or a switch away).
        double answered = _notes.Where(n => !n.IsHint).Select(n => n.Y + n.Covered).DefaultIfEmpty(0).Max();
        foreach (var s in _view.Strokes())
            if (s.Bottom > answered) _pending.Add(s.Id);

        _dirty = false;
        _view.PageShown();
        UpdateState();
    }

    /// <summary>Runs a change of conversation: stops any answer being written, saves the open one, then makes the change.</summary>
    async Task SwitchAsync(Func<Task> change)
    {
        if (_switching) return;
        _switching = true;
        try
        {
            if (_busy)
            {
                // The half-written reply is dropped and its ink goes back to "not yet answered".
                _discardOnCancel = true;
                _askCts?.Cancel();
                while (_busy) await Task.Delay(20);
            }
            CancelIdle();
            CancelSave();
            await SaveAsync();
            await change();
        }
        catch (Exception ex)
        {
            _view.Toast("Couldn't switch conversation: " + ex.Message);
        }
        finally
        {
            _discardOnCancel = false;
            _switching = false;
        }
    }

    public async Task NewConversationAsync()
    {
        // An empty page already is a fresh conversation.
        if (IsBlankNow) return;
        await SwitchAsync(async () =>
        {
            await DropBlankAsync(keepId: null);
            var created = _index.Create(DateTimeOffset.UtcNow);
            await ShowConversationAsync(created.Id);
            await SaveIndexAsync();
        });
    }

    public async Task OpenConversationAsync(string id)
    {
        if (id == _currentId) return;
        await SwitchAsync(async () =>
        {
            await DropBlankAsync(keepId: id);
            await ShowConversationAsync(id);
            await SaveIndexAsync();
        });
    }

    public async Task DeleteConversationAsync(string id)
    {
        if (id != _currentId)
        {
            _index.Delete(id);
            await _data.DeletePageAsync(id);
            await SaveIndexAsync();
            return;
        }
        await SwitchAsync(async () =>
        {
            var next = _index.Delete(id);
            await _data.DeletePageAsync(id);
            _dirty = false; // what was just deleted must not be saved again
            _currentId = "";
            await ShowConversationAsync(next ?? _index.Create(DateTimeOffset.UtcNow).Id);
            await SaveIndexAsync();
        });
    }

    public async Task RenameConversationAsync(string id, string name)
    {
        _index.Rename(id, name);
        await SaveIndexAsync();
    }

    /// <summary>Conversations nothing was written in don't stay in the list once you move away from them.</summary>
    async Task DropBlankAsync(string? keepId)
    {
        foreach (var id in _index.RemoveBlank(keepId)) await _data.DeletePageAsync(id);
    }

    async Task SaveIndexAsync()
    {
        await _saveLock.WaitAsync();
        try { await _data.SaveIndexAsync(_index); }
        catch (Exception ex) { _view.Toast("Couldn't save the conversation list: " + ex.Message); }
        finally { _saveLock.Release(); }
    }

    // ---------- saving ----------

    /// <summary>Saves a second after the last change.</summary>
    void ScheduleSave()
    {
        if (!_loaded) return;
        _dirty = true;
        if (_switching) return; // the switch saves when it has stopped the answer
        CancelSave();
        var cts = _saveCts = new CancellationTokenSource();
        _ = SaveLaterAsync(cts);
    }

    void CancelSave()
    {
        var cts = _saveCts;
        _saveCts = null;
        cts?.Cancel();
        cts?.Dispose();
    }

    async Task SaveLaterAsync(CancellationTokenSource cts)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(1), cts.Token); }
        catch (OperationCanceledException) { return; }
        if (_saveCts == cts) _saveCts = null;
        if (!_switching) await SaveAsync();
    }

    public async Task SaveAsync()
    {
        await _saveLock.WaitAsync();
        try
        {
            if (!_dirty || _currentId.Length == 0) return;
            _dirty = false;
            var id = _currentId;
            var notes = _notes.Where(n => n.Done)
                .Select(n => new SavedNote(n.Y, n.Text, n.Heard, n.IsHint, n.Drawings.Count > 0 ? n.Drawings.Select(d => d.Markup).ToList() : null, n.IsTyped, n.Summary))
                .ToList();
            await _data.SavePageAsync(id, notes);

            _index.Touch(id, DateTimeOffset.UtcNow, blank: IsBlankNow);
            // The title comes from the first thing Claude heard on the page.
            var first = _notes.Where(n => !n.IsHint && n.Heard.Length > 0).OrderBy(n => n.Y).FirstOrDefault();
            if (first is not null) _index.SetAutoTitle(id, first.Heard);
            await _data.SaveIndexAsync(_index);
        }
        catch (Exception ex)
        {
            _dirty = true;
            _view.Toast("Couldn't save the page: " + ex.Message);
        }
        finally
        {
            _saveLock.Release();
        }
    }
}
