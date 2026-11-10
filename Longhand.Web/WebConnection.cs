using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Longhand.Services;
using static Longhand.Services.PageLayout;

namespace Longhand.Web;

/// <summary>
/// One open browser tab. It owns the notebook page for that tab (a <see cref="PageSession"/>) and is the page's screen: what the
/// session does to the page is sent to the browser as small JSON messages, and what the browser does comes back the same way.
/// The browser keeps the pixels (ink, the handwriting font, measuring text), the server keeps everything else.
/// </summary>
public sealed class WebConnection : IPageView, IPageData
{
    const int MaxMessage = 32 * 1024 * 1024; // a snapshot of a long page is a few megabytes of base64
    static readonly TimeSpan MeasureTimeout = TimeSpan.FromSeconds(15), SnapshotTimeout = TimeSpan.FromSeconds(60);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly WebSocket _ws;
    readonly WebStore _store;
    readonly SerialContext _ctx = new();
    readonly Channel<string> _out = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _requests = new();
    readonly PageSession _session;
    readonly List<WebStroke> _ink = new();
    int _requestSeq, _noteSeq, _drawingSeq;
    double _sheetWidth = 800;
    bool _started, _closed;

    public WebConnection(WebSocket ws, WebStore store)
    {
        _ws = ws;
        _store = store;
        _session = new PageSession(this, this);
    }

    double TextWidth => Math.Max(200, _sheetWidth - MarginX - RightPad - 8);

    // ---------- the connection ----------

    public async Task RunAsync(CancellationToken ct)
    {
        var writer = Task.Run(WriteLoopAsync);
        try
        {
            await ReceiveLoopAsync(ct);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
            // The tab went away.
        }
        finally
        {
            _closed = true;
            foreach (var r in _requests.Values) r.TrySetCanceled();
            // Let an answer being written stop (what was written so far is kept), then save the page.
            var done = new TaskCompletionSource();
            _ctx.Run(async () =>
            {
                try
                {
                    if (!_started) return;
                    _session.CancelIdle();
                    if (_session.Busy)
                    {
                        await _session.AskOrStopAsync();
                        while (_session.Busy) await Task.Delay(20);
                    }
                    await _session.SaveAsync();
                }
                finally
                {
                    done.TrySetResult();
                }
            });
            await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(20)));
            _out.Writer.TryComplete();
            await writer;
            _ctx.Complete();
        }
    }

    async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (_ws.State == WebSocketState.Open)
        {
            var result = await _ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) break;
            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxMessage) throw new IOException("message too large");
            if (!result.EndOfMessage) continue;
            var bytes = message.ToArray();
            message.SetLength(0);
            if (result.MessageType != WebSocketMessageType.Text) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(bytes); }
            catch (JsonException) { continue; }
            // Replies to what the page asked the browser go straight to the waiting call; everything else runs on the page's thread.
            var root = doc.RootElement.Clone();
            doc.Dispose();
            if (Str(root, "t") == "reply" && root.TryGetProperty("req", out var req) && req.TryGetInt32(out var id) && _requests.TryRemove(id, out var waiting))
            {
                if (root.TryGetProperty("error", out _)) waiting.TrySetException(new InvalidOperationException("the browser couldn't do it"));
                else waiting.TrySetResult(root.TryGetProperty("value", out var v) ? v : default);
                continue;
            }
            _ctx.Run(() => HandleAsync(root));
        }
    }

    async Task WriteLoopAsync()
    {
        var reader = _out.Reader;
        try
        {
            while (await reader.WaitToReadAsync())
            {
                while (reader.TryRead(out var text))
                {
                    if (_ws.State != WebSocketState.Open) continue;
                    await _ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
            if (_ws.State == WebSocketState.Open) await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
        {
            // Nobody to tell any more.
        }
    }

    // ---------- sending ----------

    void Send(string type, params (string Name, object? Value)[] fields)
    {
        var message = new Dictionary<string, object?> { ["t"] = type };
        foreach (var (name, value) in fields) message[name] = value;
        _out.Writer.TryWrite(JsonSerializer.Serialize(message, Json));
    }

    /// <summary>Asks the browser to do something only it can (measure text, draw a picture) and waits for its answer.</summary>
    async Task<JsonElement> RequestAsync(string type, TimeSpan timeout, params (string Name, object? Value)[] fields)
    {
        if (_closed) throw new OperationCanceledException();
        var id = Interlocked.Increment(ref _requestSeq);
        var waiting = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests[id] = waiting;
        try
        {
            Send(type, fields.Append(("req", id)).ToArray());
            return await waiting.Task.WaitAsync(timeout);
        }
        finally
        {
            _requests.TryRemove(id, out _);
        }
    }

    // ---------- what the browser sends ----------

    static string? Str(JsonElement m, string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static double? Num(JsonElement m, string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    bool SetWidth(JsonElement m)
    {
        if (Num(m, "width") is not { } w || !double.IsFinite(w)) return false;
        w = Math.Clamp(w, 240, PageMaxWidth);
        bool changed = Math.Abs(w - _sheetWidth) > 0.5;
        _sheetWidth = w;
        return changed;
    }

    async Task HandleAsync(JsonElement m)
    {
        switch (Str(m, "t"))
        {
            case "hello":
                SetWidth(m);
                if (_started) break;
                _started = true;
                await _session.StartAsync();
                break;
            case "width":
                if (SetWidth(m) && _started) await _session.RelayoutAsync();
                break;
            case "strokeStarted":
                _session.StrokeStarted();
                break;
            case "stroke":
                if (!_started || !m.TryGetProperty("stroke", out var raw)) break;
                var stroke = raw.Deserialize<WebStroke>(Json);
                if (stroke is null || !stroke.IsValid() || _ink.Any(s => s.Id == stroke.Id)) break;
                _ink.Add(stroke);
                _session.StrokesCollected(new[] { stroke.Id });
                break;
            case "erase":
                if (!_started || !m.TryGetProperty("ids", out var idList) || idList.ValueKind != JsonValueKind.Array) break;
                var wanted = idList.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number && e.TryGetUInt32(out _)).Select(e => e.GetUInt32()).ToHashSet();
                var gone = _ink.Where(s => wanted.Contains(s.Id)).ToList();
                if (gone.Count == 0) break;
                _ink.RemoveAll(s => wanted.Contains(s.Id));
                _session.StrokesErased(gone.Select(s => s.Id).ToList(), gone);
                break;
            case "undo":
                _session.Undo();
                break;
            case "ask":
                if (_started) await _session.AskOrStopAsync();
                break;
            case "keyChanged":
                _session.ScheduleIdle();
                break;
            case "new":
                await _session.NewConversationAsync();
                break;
            case "open":
                if (Str(m, "id") is { } open) await _session.OpenConversationAsync(open);
                break;
            case "delete":
                if (Str(m, "id") is { } delete && _session.Index.Find(delete) is not null) await _session.DeleteConversationAsync(delete);
                break;
            case "rename":
                if (Str(m, "id") is { } rename && _session.Index.Find(rename) is not null) await _session.RenameConversationAsync(rename, Str(m, "name") ?? "");
                break;
        }
    }

    // ---------- IPageView: the ink ----------

    public IReadOnlyList<StrokeSpan> Strokes() => _ink.Select(s => new StrokeSpan(s.Id, s.Top, s.Bottom)).ToList();

    public void ShiftStrokes(double anchor, double delta, IReadOnlySet<uint> keep)
    {
        var moved = _ink.Where(s => !keep.Contains(s.Id) && s.Top >= anchor).ToList();
        if (moved.Count == 0) return;
        foreach (var s in moved) s.MoveDown(delta);
        Send("move", ("ids", moved.Select(s => s.Id)), ("dy", delta));
    }

    public bool DeleteStroke(uint id)
    {
        if (_ink.RemoveAll(s => s.Id == id) == 0) return false;
        Send("delete", ("ids", new[] { id }));
        return true;
    }

    public void RestoreStrokes(object token)
    {
        var strokes = (List<WebStroke>)token;
        _ink.AddRange(strokes);
        Send("add", ("strokes", strokes));
    }

    public void ClearStrokes()
    {
        _ink.Clear();
        Send("clear");
    }

    // ---------- IPageView: the notes ----------

    static int NoteId(NoteModel note) => (int)note.View!;
    static int DrawingId(DrawingModel drawing) => (int)drawing.View!;

    public void NoteAdded(NoteModel note)
    {
        note.View = ++_noteSeq;
        Send("note", ("id", NoteId(note)), ("y", note.Y), ("hint", note.IsHint), ("text", note.Shown));
    }

    public void NoteChanged(NoteModel note) => Send("text", ("id", NoteId(note)), ("text", note.Shown));
    public void NoteRemoved(NoteModel note) => Send("noteRemoved", ("id", NoteId(note)));

    public void DrawingAdded(NoteModel note, DrawingModel drawing)
    {
        drawing.View = ++_drawingSeq;
        Send("drawing", ("note", NoteId(note)), ("id", DrawingId(drawing)), ("svg", drawing.Markup), ("alt", drawing.Alt));
    }

    public void DrawingsCleared(NoteModel note) => Send("drawingsCleared", ("note", NoteId(note)));

    /// <summary>The browser says how many lines the words take (it has the font); the drawings' sizes follow from their shape.</summary>
    public async Task MeasureAsync(NoteModel note)
    {
        var value = await RequestAsync("measure", MeasureTimeout, ("note", NoteId(note)), ("text", note.Shown), ("width", TextWidth));
        int words = value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 1;
        // A reply that is only a drawing has no line of words above it.
        note.Lines = Math.Max(note.Drawings.Count > 0 && note.Shown.Length == 0 ? 0 : 1, words);
        foreach (var d in note.Drawings)
        {
            var (_, h) = Svg.Fit(d.Aspect, Math.Min(TextWidth, 560), 480);
            d.Lines = (int)Math.Ceiling(h / LineHeight);
        }
    }

    public void Place(NoteModel note)
    {
        double top = note.Y + note.Lines * LineHeight;
        var drawings = new List<object>();
        foreach (var d in note.Drawings)
        {
            var (w, h) = Svg.Fit(d.Aspect, Math.Min(TextWidth, 560), 480);
            drawings.Add(new { id = DrawingId(d), top, w, h });
            top += d.Lines * LineHeight;
        }
        Send("place", ("id", NoteId(note)), ("y", note.Y), ("lines", note.Lines), ("drawLines", note.DrawLines), ("drawings", drawings));
    }

    public void ContentChanged() { } // the browser works the sheet's height out from what it was told
    public void EnsureVisible(double bottom) => Send("visible", ("bottom", bottom));
    public void PageShown() => Send("page", ("strokes", _ink), ("maxId", _ink.Select(s => s.Id).DefaultIfEmpty(0u).Max()));

    public async Task<byte[]> RenderSnapshotAsync(IReadOnlyList<uint> strokeIds, SnapshotContext? context)
    {
        var value = await RequestAsync("snapshot", SnapshotTimeout,
            ("ids", strokeIds),
            ("earlier", context?.EarlierInk ?? Array.Empty<uint>()),
            ("replies", context?.Replies.Select(NoteId).ToList() ?? new List<int>()));
        return Convert.FromBase64String(value.GetString() ?? throw new InvalidOperationException("the browser sent no picture"));
    }

    // ---------- IPageView: messages ----------

    public Task<string?> RequestApiKeyAsync()
    {
        Send("needKey");
        return Task.FromResult<string?>(null);
    }

    public void Toast(string message, double seconds = 3.5) => Send("toast", ("message", message), ("seconds", seconds));
    public void StateChanged(PageState state) => Send("state", ("busy", state.Busy), ("canAsk", state.CanAsk), ("counting", state.Counting));
    public void IdleProgress(double? progress) => Send("idle", ("progress", progress));

    // ---------- IPageData ----------

    public string? ApiKey => _store.ApiKey;

    public Task<ConversationIndex> LoadIndexAsync() => _store.LoadIndexAsync();

    public async Task SaveIndexAsync(ConversationIndex index)
    {
        await _store.SaveIndexAsync(index);
        Send("conversations",
            ("current", index.CurrentId),
            ("items", index.Newest().Select(c => new { id = c.Id, title = c.Title, updated = c.Updated })));
    }

    public async Task<List<SavedNote>?> LoadPageAsync(string id)
    {
        var (ink, notes) = await _store.LoadPageAsync(id);
        _ink.Clear();
        _ink.AddRange((ink ?? new()).Where(s => s.IsValid()));
        return notes;
    }

    public Task SavePageAsync(string id, IReadOnlyList<SavedNote> notes) => _store.SavePageAsync(id, _ink.ToList(), notes);
    public Task DeletePageAsync(string id) => _store.DeletePageAsync(id);

    public Task<PreparedAsk> PrepareAskAsync(string apiKey, CancellationToken ct) =>
        Task.FromResult(new PreparedAsk(new ClaudeService(apiKey, model: _store.Model, baseUrl: _store.AnthropicBaseUrl), _store.Legend(), Array.Empty<string>()));
}
