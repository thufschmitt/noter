using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    /// <summary>A drawing as it was before the user edited it, or before the model replaced it.</summary>
    sealed record EditedDrawing(NoteModel Note, DrawingModel Drawing, string Markup, Dictionary<string, Svg.EditEntry> Edits, Dictionary<string, Box> Boxes) : UndoStep;

    /// <summary>One answer being written, so the layout updates that follow it can be merged.</summary>
    sealed class Answer
    {
        public required NoteModel Note;
        public required HashSet<uint> Ids;
        public double Occupied;
        /// <summary>The note the user typed, which this answers (it sits just above), and the room it takes.</summary>
        public NoteModel? Typed;
        public double TypedOccupied;
        /// <summary>The edits this answer responds to, taken off the drawings when it was asked (put back if it never arrives).</summary>
        public List<(DrawingModel Drawing, Dictionary<string, Svg.EditEntry> Edits)> TakenEdits = new();
        public bool Pumping, Again;
        public Task Pump = Task.CompletedTask;
    }

    readonly IPageView _view;
    readonly IPageData _data;
    readonly List<NoteModel> _notes = new();
    readonly HashSet<uint> _pending = new();       // strokes written since Claude last answered
    readonly Stack<UndoStep> _undo = new();
    readonly SemaphoreSlim _saveLock = new(1, 1);
    CancellationTokenSource? _askCts, _saveCts;
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
        var start = _index.Find(_index.CurrentId) ?? _index.Newest().FirstOrDefault() ?? _index.Create(DateTimeOffset.UtcNow, _data.DefaultModel);
        await ShowConversationAsync(start.Id);
        await _data.SaveIndexAsync(_index);
        _loaded = true;
        if (_data.SignIn is null)
            _view.Toast("Sign in with ChatGPT (gear button) to get answers. You can write in the meantime.", 6);
    }

    /// <summary>The bottom of the lowest thing on the page, which the sheet has to reach past.</summary>
    public double ContentBottom()
    {
        double bottom = Top;
        foreach (var s in _view.Strokes()) bottom = Math.Max(bottom, s.Bottom);
        foreach (var n in _notes) bottom = Math.Max(bottom, n.Bottom);
        return bottom;
    }

    /// <summary>True when the user has changed a drawing since the model last heard (and not changed it back).</summary>
    public bool HasEdits => _notes.Any(n => n.Drawings.Any(HasNetEdits));

    static bool HasNetEdits(DrawingModel d) =>
        d.Edits.Any(e => e.Value.IsNet(!e.Value.Deleted && d.Boxes.TryGetValue(e.Key, out var b) ? b : null));

    void UpdateState() => _view.StateChanged(new PageState(_busy, _busy || _pending.Count > 0 || HasEdits));

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

    public void StrokesCollected(IEnumerable<uint> ids)
    {
        var list = ids.ToList();
        if (list.Count == 1 && IsAskStar(list[0]))
        {
            // The star asks: it comes off the page (Ctrl+Z puts it back) and what was written before it goes to Claude.
            if (_view.TakeStroke(list[0]) is { } token) _undo.Push(new ErasedStrokes(token));
            ScheduleSave();
            _ = AskAsync();
            return;
        }
        foreach (var id in list)
        {
            _pending.Add(id);
            _undo.Push(new AddedStroke(id));
        }
        _view.ContentChanged();
        UpdateState();
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
        ScheduleSave();
    }

    /// <summary>
    /// Whether a stroke is the star that asks Claude: a five-point star drawn in one go (<see cref="StarGesture"/>), standing
    /// apart from the writing and from Claude's replies (a star on or touching them belongs to them), with something written
    /// (or a drawing edited) that waits to be asked. Anything else is ink like any other.
    /// </summary>
    bool IsAskStar(uint id)
    {
        if (_busy || (_pending.Count == 0 && !HasEdits)) return false;
        if (_view.StrokePoints(id) is not { } points || !StarGesture.IsStar(points)) return false;
        const double Gap = 4;
        var star = Box(points);
        foreach (var other in _view.Strokes())
            if (other.Id != id && _view.StrokePoints(other.Id) is { } p && Box(p) is var box
                && box.Left < star.Right + Gap && star.Left < box.Right + Gap && box.Top < star.Bottom + Gap && star.Top < box.Bottom + Gap)
                return false;
        // Claude's replies run the width of the page, so being level with one is being on it.
        foreach (var n in _notes)
            if (!n.IsHint && n.Y < star.Bottom && star.Top < n.Y + n.Covered) return false;
        return true;
    }

    static (double Left, double Top, double Right, double Bottom) Box(IReadOnlyList<(double X, double Y)> points) =>
        (points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));

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
            case EditedDrawing edit:
                if (_notes.Contains(edit.Note) && edit.Note.Drawings.Contains(edit.Drawing))
                {
                    edit.Drawing.Markup = edit.Markup;
                    edit.Drawing.Alt = Svg.Title(edit.Markup);
                    edit.Drawing.Edits = edit.Edits;
                    edit.Drawing.Boxes = edit.Boxes;
                    _view.DrawingChanged(edit.Note, edit.Drawing);
                    _ = RelayoutChangedAsync(edit.Note);
                }
                break;
        }
        UpdateState();
        ScheduleSave();
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
            _view.Toast("Still answering. Send it again in a moment.");
            _view.RestoreTyped(text);
            return;
        }
        await AskCoreAsync(text);
    }

    async Task AskCoreAsync(string? typed)
    {
        if (_busy || (_pending.Count == 0 && typed is null && !HasEdits)) return;

        var conversation = _currentId;
        var signIn = _data.SignIn ?? await _view.RequestSignInAsync();
        if (_switching || conversation != _currentId) return; // moved to another conversation meanwhile
        if (signIn is null)
        {
            _view.Toast("Sign in with ChatGPT (gear button) to get answers.");
            if (typed is not null) _view.RestoreTyped(typed);
            return;
        }

        var ids = new HashSet<uint>(_pending);
        var spans = _view.Strokes().Where(s => ids.Contains(s.Id)).ToList();
        if (spans.Count == 0 && typed is null && !HasEdits)
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
                    _view.Toast("Only your new ink is sent this time.");
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
        // What the user did to the drawings goes along; the drawings count as heard from here on.
        var editReport = TakeEdits(out var takenEdits);
        if (png is null && typed is null && editReport is null)
        {
            // Only edits that cancel out: nothing to say.
            UpdateState();
            return;
        }

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
        var answer = new Answer { Note = note, Ids = ids, Occupied = note.Span, Typed = typedNote, TypedOccupied = typedOccupied, TakenEdits = takenEdits };
        ShiftBelow(note.Y, answer.Occupied, note, ids);
        _view.EnsureVisible(note.Bottom);

        PreparedAsk? prepared = null;
        _busy = true;
        _askCts = new CancellationTokenSource();
        var ct = _askCts.Token;
        UpdateState();
        try
        {
            prepared = await _data.PrepareAskAsync(signIn, CurrentModel, ct);
            // Connected apps that can't be used this time are left out; say so, since the model won't have them.
            if (prepared.Unavailable is { Count: > 0 } missing) _view.Toast("Not available this time: " + string.Join(", ", missing) + " (check them with the apps button).", 6);
            var result = await prepared.Model.AskAsync(png, History(), progress =>
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
            }, hasContext: context is not null, legend: prepared.Legend, typed: typed, cacheKey: conversation, edits: editReport, canEdit: _view.CanEdit);

            note.Heard = result.Heard;
            AddNewDrawings(note, result.Drawings);
            // A drawing with the id of one already on the page replaces that one, where it is.
            int replaced = result.Outcome == ReplyOutcome.Done ? await ReplaceFiguresAsync(note, result.Drawings) : 0;
            note.Text = result.Outcome switch
            {
                ReplyOutcome.Refused when result.Body.Length == 0 && note.Drawings.Count == 0 => "I can't help with that one.",
                ReplyOutcome.Truncated => result.Body + " …",
                _ when result.Body.Length > 0 => result.Body,
                _ when note.Drawings.Count > 0 => "",
                _ when replaced > 0 => "Updated the drawing above.",
                _ => "I couldn't come up with a reply. Try writing it again?",
            };
            note.Done = true;
            note.Shown = note.Text;
            _view.NoteChanged(note);
            await FlushGrowAsync(answer);
        }
        catch (OperationCanceledException)
        {
            if ((note.Text.Length > 0 || note.Drawings.Count > 0) && !_discardOnCancel) await FinishPartialAsync(answer);
            else await DiscardAsync(answer);
        }
        catch (CodexSignInException ex)
        {
            await DiscardAsync(answer);
            _view.Toast(ex.Message, 8);
        }
        catch (CodexRateLimitException ex)
        {
            await DiscardAsync(answer);
            _view.Toast("ChatGPT is limiting requests right now (" + ex.Message.TrimEnd('.') + "). Wait a moment, then tap Ask.", 7);
        }
        catch (Exception ex)
        {
            await DiscardAsync(answer);
            _view.Toast("No answer: " + ex.Message + " Tap Ask to try again.", 7);
        }
        finally
        {
            _busy = false;
            _askCts?.Dispose();
            _askCts = null;
            UpdateState();
            ScheduleSave();
        }
    }

    /// <summary>The exchanges on the page, top to bottom.</summary>
    List<NoteModel> Exchanges() => _notes
        .Where(n => !n.IsHint && !n.IsTyped && n.Done && n.HasContent)
        .OrderBy(n => n.Y)
        .ToList();

    static Turn ToTurn(NoteModel n) =>
        new(n.Heard.Length > 0 ? n.Heard : "(unclear handwriting)", (n.Text + string.Concat(n.Drawings.Select(d => "\n" + Svg.Text(d.Markup)))).Trim());

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
        foreach (var (drawing, edits) in a.TakenEdits)
            foreach (var (id, entry) in edits)
                if (!drawing.Edits.ContainsKey(id)) drawing.Edits[id] = entry;
        a.TakenEdits = new();
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

    /// <summary>Adds one of the model's SVG drawings under the note's words. False when the drawing isn't safe or sensible to show.</summary>
    bool AddDrawing(NoteModel note, string raw)
    {
        if (note.Drawings.Count >= Svg.MaxBlocks || Svg.Sanitize(raw) is not { } block) return false;
        // The view works out its shape when it lays the drawing out at the page's width.
        var drawing = new DrawingModel { Markup = block, Alt = Svg.Title(block) };
        note.Drawings.Add(drawing);
        _view.DrawingAdded(note, drawing);
        return true;
    }

    /// <summary>Takes in the drawings of a reply that haven't been added yet. Those that replace a drawing already on the page wait for <see cref="ReplaceFiguresAsync"/>.</summary>
    void AddNewDrawings(NoteModel note, IReadOnlyList<string>? drawings)
    {
        if (drawings is null) return;
        for (; note.DrawingsSeen < drawings.Count; note.DrawingsSeen++)
        {
            var raw = drawings[note.DrawingsSeen];
            if (Svg.Sanitize(raw) is { } clean && FindFigure(note, Svg.FigureId(clean)) is not null) continue;
            if (!AddDrawing(note, raw)) _view.Toast("A drawing in the answer couldn\'t be shown.");
        }
    }

    /// <summary>The drawing on the page (in another, finished note) whose root has this id.</summary>
    (NoteModel Note, DrawingModel Drawing)? FindFigure(NoteModel except, string? id)
    {
        if (id is null) return null;
        foreach (var n in _notes)
        {
            if (n == except || !n.Done) continue;
            foreach (var d in n.Drawings)
                if (Svg.FigureId(d.Markup) == id) return (n, d);
        }
        return null;
    }

    /// <summary>
    /// A drawing in the reply whose id is one already on the page replaces that drawing, where it is (its edits are history now,
    /// and undo brings the old one back). Returns how many were replaced.
    /// </summary>
    async Task<int> ReplaceFiguresAsync(NoteModel note, IReadOnlyList<string> drawings)
    {
        int replaced = 0;
        foreach (var raw in drawings)
        {
            if (Svg.Sanitize(raw) is not { } clean || FindFigure(note, Svg.FigureId(clean)) is not { } target) continue;
            var d = target.Drawing;
            _undo.Push(new EditedDrawing(target.Note, d, d.Markup, CopyEdits(d.Edits), new Dictionary<string, Box>(d.Boxes)));
            d.Markup = clean;
            d.Alt = Svg.Title(clean);
            d.Edits = new();
            d.Boxes = new();
            _view.DrawingChanged(target.Note, d);
            await RelayoutChangedAsync(target.Note);
            replaced++;
        }
        return replaced;
    }

    static Dictionary<string, Svg.EditEntry> CopyEdits(Dictionary<string, Svg.EditEntry> edits) => edits.ToDictionary(p => p.Key, p => p.Value.Copy());

    /// <summary>Measures a note again after one of its drawings changed, and makes room (or closes up) below it.</summary>
    async Task RelayoutChangedAsync(NoteModel note)
    {
        double before = note.Span;
        try { await LayoutAsync(note); }
        catch (Exception) { return; } // a view that can\'t measure keeps the last layout
        if (!_notes.Contains(note)) return;
        if (note.Span != before) ShiftBelow(note.Y + before, note.Span - before, note, new HashSet<uint>());
    }

    // ---------- the user's edits to drawings ----------

    /// <summary>
    /// The user moved, turned or deleted parts of a drawing. The screen has already shown it; this writes it into the drawing,
    /// remembers it for the next Ask (nothing is sent now) and lets Ctrl+Z take it back.
    /// </summary>
    /// <param name="boxes">Where every element with an id is now, measured by the screen, in the drawing\'s own coordinates.</param>
    /// <returns>False when the edit wasn\'t allowed; the screen is then told to show the drawing as it is.</returns>
    public bool EditDrawing(NoteModel note, DrawingModel drawing, IReadOnlyList<DrawingChange> changes, IReadOnlyDictionary<string, Box> boxes)
    {
        if (!_notes.Contains(note) || !note.Drawings.Contains(drawing)) return false;
        var applied = note.Done && changes.Count > 0 ? Svg.ApplyEdits(drawing.Markup, changes) : null;
        if (applied is null)
        {
            _view.DrawingChanged(note, drawing);
            return false;
        }
        _undo.Push(new EditedDrawing(note, drawing, drawing.Markup, CopyEdits(drawing.Edits), new Dictionary<string, Box>(drawing.Boxes)));
        foreach (var c in changes)
        {
            if (!drawing.Edits.TryGetValue(c.Id, out var entry))
            {
                entry = new Svg.EditEntry { First = drawing.Boxes.TryGetValue(c.Id, out var was) ? was : null };
                drawing.Edits[c.Id] = entry;
            }
            if (c.Op == "delete") entry.Deleted = true;
            else if (c.Op == "rotate") entry.Degrees += c.Degrees;
        }
        drawing.Markup = applied;
        drawing.Boxes = new Dictionary<string, Box>(boxes);
        UpdateState();
        ScheduleSave();
        return true;
    }

    /// <summary>
    /// What the model is told of the edits since it last heard: for each edited drawing, the list of changes with their geometry
    /// and the drawing\'s current source. Takes the edits off the drawings (null when there is nothing to tell).
    /// </summary>
    string? TakeEdits(out List<(DrawingModel Drawing, Dictionary<string, Svg.EditEntry> Edits)> taken)
    {
        taken = new();
        var sb = new System.Text.StringBuilder();
        foreach (var note in _notes.OrderBy(n => n.Y))
            foreach (var d in note.Drawings)
            {
                if (d.Edits.Count == 0) continue;
                taken.Add((d, d.Edits));
                var list = Svg.DescribeEdits(d.Markup, d.Edits, d.Boxes);
                d.Edits = new();
                if (list is null) continue;
                var name = Svg.FigureId(d.Markup) is { } id ? $"\"{id}\"" : $"\"{d.Alt}\"";
                sb.Append($"Drawing {name}:\n{list}\nIts source now:\n{d.Markup}\n\n");
            }
        if (sb.Length == 0) return null;
        return "I changed your drawings by hand since your last reply. (Nothing was changed anywhere else: it is up to you whether it means something should change elsewhere.) "
            + "Positions are in each drawing\'s own coordinates (its viewBox, x to the right and y down).\n\n" + sb.ToString().TrimEnd();
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
        // A conversation from before each had its own model keeps the one it is opened with, whatever is chosen elsewhere later.
        if (_index.Find(id) is { Model: null }) _index.SetModel(id, _data.DefaultModel);

        var saved = await _data.LoadPageAsync(id);
        if (saved is { Count: > 0 })
            foreach (var n in saved)
            {
                var note = await AddNoteAsync(n.Y, n.Text, n.IsHint, done: true, typed: n.Typed);
                note.Heard = n.Heard;
                foreach (var svg in n.Drawings ?? new List<string>()) AddDrawing(note, svg);
                note.DrawingsSeen = note.Drawings.Count;
                foreach (var e in n.Edits ?? new List<SavedEdit>())
                    if (e.Drawing >= 0 && e.Drawing < note.Drawings.Count)
                        note.Drawings[e.Drawing].Edits[e.Id] = new Svg.EditEntry
                        {
                            First = e.First is { Length: 4 } f ? new Box(f[0], f[1], f[2], f[3]) : null,
                            Deleted = e.Deleted,
                            Degrees = e.Degrees,
                        };
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

    /// <summary>The model the open conversation is answered by.</summary>
    public string CurrentModel => ModelChoice.Resolve(_index.Find(_currentId)?.Model ?? _data.DefaultModel);

    /// <summary>
    /// Changes the model the open conversation is answered by. It also becomes the one new conversations start with, so the
    /// last choice carries on.
    /// </summary>
    public async Task SetModelAsync(string model)
    {
        model = ModelChoice.Resolve(model);
        _data.DefaultModel = model;
        if (_currentId.Length == 0 || _index.Find(_currentId) is null) return;
        _index.SetModel(_currentId, model);
        await SaveIndexAsync();
    }

    public async Task NewConversationAsync()
    {
        // An empty page already is a fresh conversation.
        if (IsBlankNow) return;
        await SwitchAsync(async () =>
        {
            await DropBlankAsync(keepId: null);
            var created = _index.Create(DateTimeOffset.UtcNow, _data.DefaultModel);
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
            await ShowConversationAsync(next ?? _index.Create(DateTimeOffset.UtcNow, _data.DefaultModel).Id);
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

    static List<SavedEdit>? SavedEdits(NoteModel n)
    {
        var list = new List<SavedEdit>();
        for (int i = 0; i < n.Drawings.Count; i++)
            foreach (var (id, e) in n.Drawings[i].Edits)
                list.Add(new SavedEdit(i, id, e.First is { } b ? new[] { b.X, b.Y, b.W, b.H } : null, e.Deleted, e.Degrees));
        return list.Count > 0 ? list : null;
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
                .Select(n => new SavedNote(n.Y, n.Text, n.Heard, n.IsHint, n.Drawings.Count > 0 ? n.Drawings.Select(d => d.Markup).ToList() : null, n.IsTyped, SavedEdits(n)))
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
