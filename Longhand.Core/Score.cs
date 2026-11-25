using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Longhand.Services;

public enum ScoreKind { Note, Rest, Bar, Accidental, Slur }
public enum Accidental { None, Sharp, Flat, Natural }
public enum Clef { Treble, Bass }
public enum StemDir { Auto, Up, Down }

/// <summary>
/// One thing engraved on the score: a note, a rest, a bar line, an accidental waiting for its note, or a slur between two notes.
/// Positions are the page's: <see cref="X"/> in pixels, height as a <see cref="Step"/> on the staff.
/// </summary>
public sealed class ScoreItem
{
    public string Id { get; set; } = "";
    public ScoreKind Kind { get; set; }
    public int Staff { get; set; }
    /// <summary>The middle of the notehead (or of the rest, the bar line, the accidental).</summary>
    public double X { get; set; }
    /// <summary>Half staff spaces below the top line: 0 is the top line, 8 the bottom one, -2 the first ledger line above.</summary>
    public int Step { get; set; }
    /// <summary>1 whole, 2 half, 4 quarter, 8 eighth, 16 sixteenth.</summary>
    public int Duration { get; set; } = 4;
    public bool Dotted { get; set; }
    public Accidental Accidental { get; set; }
    /// <summary>Which way the stem goes: the way it was written, or worked out from where the note is.</summary>
    public StemDir Stem { get; set; }
    /// <summary>Notes beamed together share this.</summary>
    public string? Beam { get; set; }
    /// <summary>A slur's two notes, and whether it curves under them.</summary>
    public string? From { get; set; }
    public string? To { get; set; }
    public bool Below { get; set; }

    public ScoreItem Copy() => (ScoreItem)MemberwiseClone();
}

/// <summary>A pen stroke as the recognizer sees it: its id on the screen and its points on the page.</summary>
public sealed record MusicStroke(uint Id, IReadOnlyList<(double X, double Y)> Points);

/// <summary>What reading the ink did: the strokes it turned into notation (the screen takes them off), and what it made of them.</summary>
public sealed record MusicRead(IReadOnlyList<uint> Used, bool Changed, string Summary);

/// <summary>A sound to play: when it starts and how long it lasts, in beats, and its MIDI pitch.</summary>
public readonly record struct PlayedNote(double Start, double Length, int Midi, string Id);

/// <summary>
/// Longhand Score's page: staves one under the other, each with its clef, and what has been engraved on them. The pen's ink is read
/// into it by <see cref="MusicInk"/>, it is drawn by <see cref="ScoreEngraver"/>, and it plays through <see cref="ScoreAudio"/>.
/// Every change can be undone. Shared by the Windows app and the web version; the screen only keeps the ink not read yet.
/// </summary>
public sealed class ScoreSheet
{
    /// <summary>The page's own width; the screen scales it to fit.</summary>
    public const double PageWidth = 1000;
    /// <summary>The distance between two staff lines, in page pixels: large, as it is written on by hand.</summary>
    public const double Space = 24;
    /// <summary>Where the first staff's top line is, and how far each staff is from the next.</summary>
    public const double Top = 4 * Space, StaffPitch = 11 * Space;
    public const double Left = 32, Right = 32;
    /// <summary>Where notes may start, past the clef.</summary>
    public const double ClefWidth = 3.4 * Space;
    /// <summary>At least this many staves, and always this many empty ones under the last one written on.</summary>
    public const int MinStaves = 5, SpareStaves = 2;
    const int MaxUndo = 200;

    public List<Clef> Clefs { get; private set; } = new();
    public List<ScoreItem> Items { get; private set; } = new();
    public int Tempo { get; set; } = 96;
    int _next = 1;
    readonly List<string> _undo = new();

    public int StaffCount => Math.Max(MinStaves, (Items.Count == 0 ? -1 : Items.Max(i => i.Staff)) + 1 + SpareStaves);
    public double Height => Top + (StaffCount - 1) * StaffPitch + 4 * Space + 5 * Space;
    public bool CanUndo => _undo.Count > 0;

    public double StaffTop(int staff) => Top + staff * StaffPitch;
    public double StepY(int staff, int step) => StaffTop(staff) + step * Space / 2;
    /// <summary>The staff a height on the page belongs to: the nearest one, by its middle line.</summary>
    public int StaffAt(double y) => Math.Clamp((int)Math.Round((y - Top - 2 * Space) / StaffPitch), 0, StaffCount - 1);
    public int StepAt(int staff, double y) => (int)Math.Round((y - StaffTop(staff)) / (Space / 2));
    public Clef ClefOf(int staff) => staff < Clefs.Count ? Clefs[staff] : Clef.Treble;

    public ScoreItem? Find(string? id) => id is null ? null : Items.FirstOrDefault(i => i.Id == id);

    internal string NewId() => "n" + _next++;

    internal void SetClef(int staff, Clef clef)
    {
        while (Clefs.Count <= staff) Clefs.Add(Clef.Treble);
        Clefs[staff] = clef;
    }

    /// <summary>Keeps how the score is now, for <see cref="Undo"/>. Called before every change.</summary>
    internal void Remember()
    {
        _undo.Add(Snapshot());
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
    }

    internal void Forget() { if (_undo.Count > 0) _undo.RemoveAt(_undo.Count - 1); }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        Restore(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        return true;
    }

    /// <summary>Reads pen strokes into notation (see <see cref="MusicInk"/>).</summary>
    public MusicRead Read(IReadOnlyList<MusicStroke> strokes) => MusicInk.Read(this, strokes);

    /// <summary>The score drawn as clean notation (see <see cref="ScoreEngraver"/>).</summary>
    public string Svg() => ScoreEngraver.Svg(this);

    /// <summary>Rubs out what an eraser went over: every item whose shape is within reach of its path. True when something went.</summary>
    public bool EraseAlong(IReadOnlyList<(double X, double Y)> path, double reach = 8)
    {
        if (path.Count == 0) return false;
        var hit = Items.Where(i => i.Kind != ScoreKind.Slur && Touches(Bounds(i), path, reach)).Select(i => i.Id).ToHashSet();
        // A slur goes with either of its notes, or when the eraser crosses its middle.
        foreach (var slur in Items.Where(i => i.Kind == ScoreKind.Slur))
            if (hit.Contains(slur.From!) || hit.Contains(slur.To!) || Touches(Bounds(slur), path, reach)) hit.Add(slur.Id);
        if (hit.Count == 0) return false;
        Remember();
        Remove(hit);
        return true;
    }

    public void Clear()
    {
        if (Items.Count == 0 && Clefs.All(c => c == Clef.Treble)) return;
        Remember();
        Items.Clear();
        Clefs.Clear();
    }

    /// <summary>Takes items off, with the slurs that hung on them; a beam left with one note is no beam.</summary>
    internal void Remove(ICollection<string> ids)
    {
        Items.RemoveAll(i => ids.Contains(i.Id) || i.Kind == ScoreKind.Slur && (ids.Contains(i.From!) || ids.Contains(i.To!)));
        foreach (var group in Items.Where(i => i.Beam is not null).GroupBy(i => i.Beam).Where(g => g.Count() < 2).ToList())
            foreach (var lone in group) lone.Beam = null;
    }

    static bool Touches((double X0, double Y0, double X1, double Y1) b, IReadOnlyList<(double X, double Y)> path, double reach)
    {
        for (int k = 0; k < path.Count; k++)
        {
            var a = path[k];
            var c = k + 1 < path.Count ? path[k + 1] : a;
            // The segment against the box grown by the reach: sample it finely enough.
            double len = Math.Sqrt((c.X - a.X) * (c.X - a.X) + (c.Y - a.Y) * (c.Y - a.Y));
            int n = Math.Max(1, (int)(len / 3));
            for (int s = 0; s <= n; s++)
            {
                double x = a.X + (c.X - a.X) * s / n, y = a.Y + (c.Y - a.Y) * s / n;
                if (x >= b.X0 - reach && x <= b.X1 + reach && y >= b.Y0 - reach && y <= b.Y1 + reach) return true;
            }
        }
        return false;
    }

    /// <summary>Roughly where an item is drawn: the head (with its stem), the rest, the bar line, the accidental, the slur.</summary>
    public (double X0, double Y0, double X1, double Y1) Bounds(ScoreItem item)
    {
        double s = Space;
        switch (item.Kind)
        {
            case ScoreKind.Note:
                double y = StepY(item.Staff, item.Step);
                return (item.X - 0.7 * s, y - 0.55 * s, item.X + 0.7 * s, y + 0.55 * s);
            case ScoreKind.Rest:
                double mid = StepY(item.Staff, 4);
                return (item.X - 0.6 * s, mid - 1.5 * s, item.X + 0.6 * s, mid + 1.5 * s);
            case ScoreKind.Bar:
                return (item.X - 0.3 * s, StaffTop(item.Staff), item.X + 0.3 * s, StaffTop(item.Staff) + 4 * s);
            case ScoreKind.Accidental:
                double ay = StepY(item.Staff, item.Step);
                return (item.X - 0.5 * s, ay - 1.3 * s, item.X + 0.5 * s, ay + 1.3 * s);
            case ScoreKind.Slur when Find(item.From) is { } a && Find(item.To) is { } b:
                double ya = StepY(a.Staff, a.Step), yb = StepY(b.Staff, b.Step);
                double off = item.Below ? 1.2 * s : -1.2 * s;
                return (Math.Min(a.X, b.X), Math.Min(ya, yb) + Math.Min(0, off) - 0.3 * s, Math.Max(a.X, b.X), Math.Max(ya, yb) + Math.Max(0, off) + 0.3 * s);
            default:
                return (item.X, 0, item.X, 0);
        }
    }

    // ---------- pitches and playing ----------

    /// <summary>The note's pitch as a step of the C major scale, C4 being 28: what the clef makes of the line or space it is on.</summary>
    public int Diatonic(ScoreItem note) => (ClefOf(note.Staff) == Clef.Bass ? 26 : 38) - note.Step;

    static readonly int[] Semitones = { 0, 2, 4, 5, 7, 9, 11 };

    public static int Midi(int diatonic, int alter) => 12 * (Math.DivRem(diatonic, 7, out int r) + (r < 0 ? -1 : 0)) + Semitones[(r + 7) % 7] + 12 + alter;

    /// <summary>The name of a pitch, like C4 or F#5.</summary>
    public static string PitchName(int diatonic, Accidental accidental) =>
        "CDEFGAB"[((diatonic % 7) + 7) % 7] + (accidental switch { Accidental.Sharp => "#", Accidental.Flat => "b", _ => "" }) + Math.Floor(diatonic / 7.0);

    /// <summary>How long a note or rest lasts, in quarter-note beats.</summary>
    public static double Beats(ScoreItem item) => 4.0 / item.Duration * (item.Dotted ? 1.5 : 1);

    /// <summary>
    /// What the score sounds like, staff after staff, left to right. Notes within half a space of each other across are a chord. An
    /// accidental holds for the rest of the bar, on that line or space.
    /// </summary>
    public List<PlayedNote> Playback()
    {
        var played = new List<PlayedNote>();
        double time = 0;
        foreach (var staff in Items.Where(i => i.Kind is ScoreKind.Note or ScoreKind.Rest or ScoreKind.Bar).GroupBy(i => i.Staff).OrderBy(g => g.Key))
        {
            var held = new Dictionary<int, int>(); // diatonic → alteration, until the bar line
            var events = staff.OrderBy(i => i.X).ToList();
            for (int k = 0; k < events.Count;)
            {
                var first = events[k];
                if (first.Kind == ScoreKind.Bar) { held.Clear(); k++; continue; }
                var chord = new List<ScoreItem> { first };
                k++;
                while (k < events.Count && events[k].Kind != ScoreKind.Bar && events[k].X - first.X <= Space * 0.5) chord.Add(events[k++]);
                double length = chord.Min(Beats);
                foreach (var note in chord.Where(n => n.Kind == ScoreKind.Note))
                {
                    int d = Diatonic(note);
                    if (note.Accidental != Accidental.None) held[d] = note.Accidental switch { Accidental.Sharp => 1, Accidental.Flat => -1, _ => 0 };
                    played.Add(new PlayedNote(time, Beats(note), Midi(d, held.GetValueOrDefault(d)), note.Id));
                }
                time += length;
            }
        }
        return played;
    }

    // ---------- saving ----------

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    sealed class Saved
    {
        public int Version { get; set; } = 1;
        public List<Clef>? Clefs { get; set; }
        public List<ScoreItem>? Items { get; set; }
        public int Tempo { get; set; }
        public int Next { get; set; }
    }

    string Snapshot() => JsonSerializer.Serialize(new Saved { Clefs = Clefs, Items = Items, Tempo = Tempo, Next = _next }, Json);

    void Restore(string json)
    {
        var saved = JsonSerializer.Deserialize<Saved>(json, Json) ?? new Saved();
        Clefs = saved.Clefs ?? new();
        Items = (saved.Items ?? new()).Where(i => !string.IsNullOrEmpty(i.Id)).ToList();
        Tempo = saved.Tempo is >= 20 and <= 300 ? saved.Tempo : 96;
        _next = Math.Max(saved.Next, 1);
    }

    public string ToJson() => Snapshot();

    /// <summary>A score saved with <see cref="ToJson"/>; an empty one when there is none or it can't be read.</summary>
    public static ScoreSheet FromJson(string? json)
    {
        var sheet = new ScoreSheet();
        if (string.IsNullOrWhiteSpace(json)) return sheet;
        try { sheet.Restore(json); }
        catch (JsonException) { }
        return sheet;
    }
}
