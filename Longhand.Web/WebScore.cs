using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Longhand.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Longhand.Web;

/// <summary>
/// Longhand Score on the web (<c>/score</c>): the server keeps the score (<see cref="ScoreSheet"/>, saved as <c>score.json</c> in the
/// data folder) and reads the ink the page sends it; the page shows the engraving and the ink not read yet. Undo is kept here for
/// as long as the server runs. No model is involved, so it needs no sign-in.
/// </summary>
public sealed class WebScore
{
    const int MaxStrokes = 400, MaxPoints = 4000;
    readonly string _path;
    readonly SemaphoreSlim _lock = new(1, 1);
    ScoreSheet? _sheet;

    public WebScore(WebStore store) => _path = Path.Combine(store.Dir, "score.json");

    async Task<ScoreSheet> SheetAsync()
    {
        if (_sheet is not null) return _sheet;
        string? json = null;
        try { json = File.Exists(_path) ? await File.ReadAllTextAsync(_path) : null; }
        catch (IOException) { }
        return _sheet = ScoreSheet.FromJson(json);
    }

    /// <summary>Does something to the score, one request at a time, saves it when it changed, and answers with how it looks now.</summary>
    async Task<IResult> WithSheetAsync(Func<ScoreSheet, (bool Changed, object? Extra)> work)
    {
        await _lock.WaitAsync();
        try
        {
            var sheet = await SheetAsync();
            var (changed, extra) = work(sheet);
            if (changed) await WebStore.WriteAtomicAsync(_path, sheet.ToJson());
            return Results.Ok(new { svg = sheet.Svg(), width = ScoreSheet.PageWidth, height = sheet.Height, canUndo = sheet.CanUndo, changed, extra });
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Map(WebApplication app)
    {
        app.MapGet("/api/score", () => WithSheetAsync(_ => (false, null)));

        // The ink written since the pen paused (with what is still waiting from before): read into notation where it can be.
        app.MapPost("/api/score/read", (ReadRequest request) =>
        {
            var strokes = (request.Strokes ?? new()).Take(MaxStrokes)
                .Select(s => new MusicStroke(s.Id, (s.Points ?? new()).Take(MaxPoints).Where(p => p.Length >= 2).Select(p => (p[0], p[1])).ToList()))
                .Where(s => s.Points.Count > 0).ToList();
            return WithSheetAsync(sheet =>
            {
                var read = sheet.Read(strokes);
                return (read.Changed, new { used = read.Used, summary = read.Summary });
            });
        });

        app.MapPost("/api/score/erase", (EraseRequest request) =>
        {
            var path = (request.Path ?? new()).Take(MaxPoints).Where(p => p.Length >= 2).Select(p => (p[0], p[1])).ToList();
            return WithSheetAsync(sheet => (sheet.EraseAlong(path, Math.Clamp(request.Reach ?? 8, 2, 40)), null));
        });

        app.MapPost("/api/score/undo", () => WithSheetAsync(sheet => (sheet.Undo(), null)));

        app.MapPost("/api/score/clear", () => WithSheetAsync(sheet =>
        {
            bool had = sheet.Items.Count > 0;
            sheet.Clear();
            return (had || sheet.CanUndo, null);
        }));

        // What the score sounds like, as a WAV the page plays.
        app.MapGet("/api/score/play.wav", async () =>
        {
            await _lock.WaitAsync();
            try
            {
                var sheet = await SheetAsync();
                return Results.File(ScoreAudio.Wav(sheet.Playback(), sheet.Tempo), "audio/wav");
            }
            finally
            {
                _lock.Release();
            }
        });
    }

    public sealed record StrokeIn(uint Id, List<double[]>? Points);
    public sealed record ReadRequest(List<StrokeIn>? Strokes);
    public sealed record EraseRequest(List<double[]>? Path, double? Reach);
}
