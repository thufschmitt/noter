using System;
using System.Collections.Generic;
using System.Linq;
using Longhand.Services;

/// <summary>
/// Checks for Longhand Score: handwriting drawn the way a pen would (many points, a little wobble) is read into the notes, rests,
/// clefs and marks it means, cleaned up on the staff, and played.
/// </summary>
static class ScoreChecks
{
    const double S = ScoreSheet.Space;
    static uint _ids;
    static readonly Random Wobble = new(7);

    public static MusicStroke Stroke(IEnumerable<(double X, double Y)> points) =>
        new(++_ids, points.Select(p => (p.X + (Wobble.NextDouble() - 0.5) * 0.8, p.Y + (Wobble.NextDouble() - 0.5) * 0.8)).ToList());

    public static IEnumerable<(double X, double Y)> Line(double x0, double y0, double x1, double y1, int n = 20) =>
        Enumerable.Range(0, n + 1).Select(i => (x0 + (x1 - x0) * i / n, y0 + (y1 - y0) * i / n));

    /// <summary>An oval drawn once round (a little more, as a hand closes it).</summary>
    public static IEnumerable<(double X, double Y)> Oval(double cx, double cy, double rx = 0.65 * S, double ry = 0.45 * S, double turns = 1.08, double from = 0.6) =>
        Enumerable.Range(0, 41).Select(i => from + turns * 2 * Math.PI * i / 40).Select(a => (cx + rx * Math.Cos(a), cy + ry * Math.Sin(a)));

    /// <summary>A head filled in: round and round, smaller and smaller.</summary>
    public static IEnumerable<(double X, double Y)> Filled(double cx, double cy, double rx = 0.6 * S, double ry = 0.42 * S) =>
        Enumerable.Range(0, 121).Select(i => (a: 3.2 * 2 * Math.PI * i / 120, k: 1 - 0.75 * i / 120.0)).Select(t => (cx + rx * t.k * Math.Cos(t.a), cy + ry * t.k * Math.Sin(t.a)));

    public static IEnumerable<(double X, double Y)> Dot(double x, double y) => new[] { (x, y), (x + 1.5, y + 1), (x + 2, y + 2.5) };

    /// <summary>The y of a step on a staff.</summary>
    static double Y(ScoreSheet sheet, int staff, int step) => sheet.StepY(staff, step);

    public static void Run(Action<string, bool> check)
    {
        // ----- notes -----
        var sheet = new ScoreSheet();
        double y = Y(sheet, 0, 6); // G4 on a treble staff
        var read = sheet.Read(new[] { Stroke(Filled(300, y + 3)) });
        var note = sheet.Items.SingleOrDefault();
        check("a filled-in head is a quarter note, on the line it is nearest", read.Changed && note is { Kind: ScoreKind.Note, Duration: 4, Step: 6 } && read.Used.Count == 1);
        check("on a treble staff the second line is G4", note is { } n0 && ScoreSheet.PitchName(sheet.Diatonic(n0), n0.Accidental) == "G4");

        sheet = new ScoreSheet();
        sheet.Read(new[] { Stroke(Oval(300, Y(sheet, 0, 7))) });
        check("an open head alone is a whole note, in its space", sheet.Items.SingleOrDefault() is { Duration: 1, Step: 7 });
        sheet.Read(new[] { Stroke(Line(300 + 0.62 * S, Y(sheet, 0, 7), 300 + 0.6 * S, Y(sheet, 0, 7) - 3.4 * S)) });
        check("a stem drawn on it afterwards makes it a half note, stem up", sheet.Items.SingleOrDefault() is { Duration: 2, Stem: StemDir.Up });

        sheet = new ScoreSheet();
        y = Y(sheet, 0, 2);
        // Round the head from its left side, and straight on down into the stem.
        var headThenStem = Oval(400, y, turns: 1.0, from: Math.PI).Concat(Line(400 - 0.65 * S, y, 400 - 0.65 * S, y + 3.5 * S).Skip(1)).ToList();
        read = sheet.Read(new[] { Stroke(headThenStem) });
        check("an open head and its stem written in one stroke are a half note, stem down", sheet.Items.SingleOrDefault() is { Kind: ScoreKind.Note, Duration: 2, Stem: StemDir.Down, Step: 2 });

        sheet = new ScoreSheet();
        y = Y(sheet, 1, 5);
        read = sheet.Read(new[] { Stroke(Filled(300, y)), Stroke(Line(300 + 0.6 * S, y - 0.2 * S, 300 + 0.6 * S, y - 3.5 * S)) });
        check("on the second staff, a head with its stem is a quarter on that staff", sheet.Items.SingleOrDefault() is { Staff: 1, Step: 5, Duration: 4, Stem: StemDir.Up } && read.Used.Count == 2);

        // A flag on the stem's end, then another.
        sheet = new ScoreSheet();
        y = Y(sheet, 0, 6);
        sheet.Read(new[] { Stroke(Filled(300, y)), Stroke(Line(300 + 0.6 * S, y, 300 + 0.6 * S, y - 3.5 * S)) });
        var flag = Enumerable.Range(0, 21).Select(i => i / 20.0).Select(t => (300 + 0.6 * S + 1.1 * S * Math.Sin(t * Math.PI * 0.8), y - 3.5 * S + 2.6 * S * t)).ToList();
        read = sheet.Read(new[] { Stroke(flag) });
        check("a flag on a quarter's stem makes it an eighth", read.Changed && sheet.Items.Single().Duration == 8);
        sheet.Read(new[] { Stroke(flag.Select(p => (p.Item1, p.Item2 + 0.8 * S))) });
        check("a second flag makes it a sixteenth", sheet.Items.Single().Duration == 16);

        // Two quarters, then a beam over their stems.
        sheet = new ScoreSheet();
        y = Y(sheet, 0, 5);
        var strokes = new List<MusicStroke>();
        foreach (double x in new[] { 300.0, 360.0, 420.0 })
        {
            strokes.Add(Stroke(Filled(x, y)));
            strokes.Add(Stroke(Line(x + 0.6 * S, y, x + 0.6 * S, y - 3.4 * S)));
        }
        sheet.Read(strokes);
        read = sheet.Read(new[] { Stroke(Line(300 + 0.6 * S, y - 3.4 * S, 420 + 0.6 * S, y - 3.2 * S)) });
        var beamed = sheet.Items.Where(i => i.Kind == ScoreKind.Note).ToList();
        check("a beam across three stems makes them eighths, beamed together", read.Changed && beamed.Count == 3 && beamed.All(n => n.Duration == 8 && n.Beam is not null && n.Beam == beamed[0].Beam));
        sheet.Read(new[] { Stroke(Line(300 + 0.6 * S, y - 2.6 * S, 420 + 0.6 * S, y - 2.4 * S)) });
        check("a second beam under it makes them sixteenths", beamed.All(n => n.Duration == 16));

        // Notes, stems and a beam all in one go.
        sheet = new ScoreSheet();
        y = Y(sheet, 0, 3);
        strokes.Clear();
        foreach (double x in new[] { 300.0, 350.0 })
        {
            strokes.Add(Stroke(Filled(x, y)));
            strokes.Add(Stroke(Line(x - 0.6 * S, y, x - 0.6 * S, y + 3.5 * S)));
        }
        strokes.Add(Stroke(Line(300 - 0.6 * S, y + 3.5 * S, 350 - 0.6 * S, y + 3.5 * S)));
        sheet.Read(strokes);
        check("heads, stems down and a beam written together are beamed eighths, stems down", sheet.Items.Count(i => i.Kind == ScoreKind.Note) == 2 && sheet.Items.All(n => n.Duration == 8 && n.Stem == StemDir.Down && n.Beam is not null));

        // A dot after a note.
        sheet = new ScoreSheet();
        y = Y(sheet, 0, 4);
        sheet.Read(new[] { Stroke(Oval(300, y)), Stroke(Line(300 + 0.6 * S, y, 300 + 0.6 * S, y - 3.5 * S)) });
        sheet.Read(new[] { Stroke(Dot(300 + 1.2 * S, y - 0.4 * S)) });
        check("a dot to the right of a half note dots it", sheet.Items.Single() is { Duration: 2, Dotted: true });

        // A chord: three heads on one stem.
        sheet = new ScoreSheet();
        strokes.Clear();
        foreach (int step in new[] { 8, 6, 4 }) strokes.Add(Stroke(Filled(300, Y(sheet, 0, step))));
        strokes.Add(Stroke(Line(300 + 0.6 * S, Y(sheet, 0, 8), 300 + 0.6 * S, Y(sheet, 0, 4) - 3.2 * S)));
        sheet.Read(strokes);
        var chord = sheet.Items.Where(i => i.Kind == ScoreKind.Note).ToList();
        check("three heads on one stem are a chord of quarters", chord.Count == 3 && chord.All(n => n.Duration == 4 && n.Stem == StemDir.Up));
        var heard = sheet.Playback();
        check("a chord sounds at once: E4, G4, B4", heard.Count == 3 && heard.All(h => h.Start == 0 && h.Length == 1) && heard.Select(h => h.Midi).OrderBy(m => m).SequenceEqual(new[] { 64, 67, 71 }));

        // Ledger lines: a head below the staff with its ledger line drawn through it.
        sheet = new ScoreSheet();
        y = Y(sheet, 0, 10);
        read = sheet.Read(new[] { Stroke(Filled(300, y)), Stroke(Line(300 - 0.9 * S, y, 300 + 0.9 * S, y)) });
        check("middle C with its ledger line drawn is a C4, the line taken in", sheet.Items.SingleOrDefault() is { Step: 10 } c4 && ScoreSheet.PitchName(sheet.Diatonic(c4), Accidental.None) == "C4" && read.Used.Count == 2);

        // ----- accidentals -----
        sheet = new ScoreSheet();
        y = Y(sheet, 0, 7);
        // Down the upright, then the bowl: out to the right and up, and back round to the upright a little above its foot.
        var flat = Line(300, y - 1.8 * S, 300, y + 0.5 * S).Concat(Enumerable.Range(1, 20).Select(i => i / 20.0)
            .Select(t => (300 + 0.7 * S * Math.Sin(t * Math.PI), y + 0.5 * S - 2.6 * S * Math.Sin(t * Math.PI / 2) * (1 - t) - 0.1 * S * t))).ToList();
        sheet.Read(new[] { Stroke(flat) });
        var acc = sheet.Items.SingleOrDefault();
        check("a \"b\" is a flat, waiting for its note", acc is { Kind: ScoreKind.Accidental, Accidental: Accidental.Flat });
        sheet.Read(new[] { Stroke(Filled(300 + 1.8 * S, Y(sheet, 0, acc?.Step ?? 7))) });
        var flatNote = sheet.Items.SingleOrDefault();
        check("the note written next to it takes it", flatNote is { Kind: ScoreKind.Note, Accidental: Accidental.Flat });

        sheet = new ScoreSheet();
        y = Y(sheet, 0, 5);
        var sharp = new[]
        {
            Stroke(Line(300 - 0.3 * S, y - 1.2 * S, 300 - 0.3 * S, y + 1.3 * S)),
            Stroke(Line(300 + 0.3 * S, y - 1.3 * S, 300 + 0.3 * S, y + 1.2 * S)),
            Stroke(Line(300 - 0.7 * S, y - 0.3 * S, 300 + 0.7 * S, y - 0.6 * S)),
            Stroke(Line(300 - 0.7 * S, y + 0.6 * S, 300 + 0.7 * S, y + 0.3 * S)),
        };
        sheet.Read(sharp.Concat(new[] { Stroke(Filled(300 + 2 * S, y)) }).ToList());
        var sharpNote = sheet.Items.SingleOrDefault();
        check("two short uprights crossed by two bars are a sharp, on the note beside them", sharpNote is { Kind: ScoreKind.Note, Accidental: Accidental.Sharp, Step: 5 });
        check("it is an A#4, and played as one", sharpNote is { } sn && ScoreSheet.PitchName(sheet.Diatonic(sn), sn.Accidental) == "A#4" && sheet.Playback().Single().Midi == 70);

        sheet = new ScoreSheet();
        y = Y(sheet, 0, 6);
        // Down the left upright, across (rising a little) to the right, and down the right one, in one stroke.
        var natural = Line(300 - 0.3 * S, y - 1.4 * S, 300 - 0.3 * S, y + 0.4 * S, 12).Concat(Line(300 - 0.3 * S, y + 0.4 * S, 300 + 0.3 * S, y - 0.1 * S, 6).Skip(1))
            .Concat(Line(300 + 0.3 * S, y - 0.1 * S, 300 + 0.3 * S, y + 1.4 * S, 12).Skip(1)).ToList();
        sheet.Read(new[] { Stroke(natural), Stroke(Filled(300 + 1.9 * S, y)) });
        check("an upright with a hook each way, in one stroke, is a natural on the note beside it", sheet.Items.SingleOrDefault() is { Kind: ScoreKind.Note, Accidental: Accidental.Natural, Step: 6 });

        // ----- rests, bar lines, clefs, slurs -----
        sheet = new ScoreSheet();
        double mid = Y(sheet, 0, 4);
        var zig = new[] { (300.0, mid - 1.5 * S), (300 + 0.6 * S, mid - 0.6 * S), (300 - 0.2 * S, mid + 0.2 * S), (300 + 0.5 * S, mid + 0.8 * S), (300 - 0.1 * S, mid + 1.4 * S) };
        var zigPts = Enumerable.Range(0, zig.Length - 1).SelectMany(i => Line(zig[i].Item1, zig[i].Item2, zig[i + 1].Item1, zig[i + 1].Item2, 8)).ToList();
        sheet.Read(new[] { Stroke(zigPts) });
        check("a zigzag down the middle of the staff is a quarter rest", sheet.Items.SingleOrDefault() is { Kind: ScoreKind.Rest, Duration: 4 });

        sheet = new ScoreSheet();
        var seven = Line(400 - 0.5 * S, Y(sheet, 0, 3), 400 + 0.5 * S, Y(sheet, 0, 3) - 0.1 * S, 8).Concat(Line(400 + 0.5 * S, Y(sheet, 0, 3) - 0.1 * S, 400 - 0.1 * S, Y(sheet, 0, 7), 16)).ToList();
        sheet.Read(new[] { Stroke(seven) });
        check("a 7 is an eighth rest", sheet.Items.SingleOrDefault() is { Kind: ScoreKind.Rest, Duration: 8 });

        sheet = new ScoreSheet();
        sheet.Read(new[] { Stroke(Line(300, Y(sheet, 0, 2) + 0.25 * S, 300 + 1.2 * S, Y(sheet, 0, 2) + 0.25 * S)), Stroke(Line(400, Y(sheet, 0, 4) - 0.25 * S, 400 + 1.2 * S, Y(sheet, 0, 4) - 0.25 * S)) });
        var blocks = sheet.Items.OrderBy(i => i.X).ToList();
        check("a dash under the fourth line is a whole rest, one on the middle line a half rest", blocks.Count == 2 && blocks[0] is { Kind: ScoreKind.Rest, Duration: 1 } && blocks[1] is { Kind: ScoreKind.Rest, Duration: 2 });

        sheet = new ScoreSheet();
        read = sheet.Read(new[] { Stroke(Line(500, Y(sheet, 0, 0) - 0.2 * S, 501, Y(sheet, 0, 8) + 0.2 * S)) });
        check("a line down the whole staff is a bar line", sheet.Items.SingleOrDefault() is { Kind: ScoreKind.Bar });

        sheet = new ScoreSheet();
        double g = Y(sheet, 2, 6);
        var curl = Enumerable.Range(0, 200).Select(i => i / 199.0).Select(t =>
        {
            // A spiral round the G line rising into a tall loop and coming down below the staff: tall and full of turns.
            double a = t * 5 * Math.PI;
            double r = 0.3 * S + 0.9 * S * t;
            return (100 + r * Math.Cos(a) * 0.7, g - 3.2 * S * t + r * Math.Sin(a) * 0.5 + (t > 0.8 ? (t - 0.8) * 30 * S : 0));
        }).ToList();
        sheet.Read(new[] { Stroke(curl) });
        check("a tall curl is a treble clef", sheet.ClefOf(2) == Clef.Treble && sheet.Items.Count == 0);
        double f = Y(sheet, 2, 2);
        var bass = Enumerable.Range(0, 30).Select(i => i / 29.0).Select(t => (90 + 1.6 * S * Math.Sin(t * Math.PI * 0.95) * (1 - 0.3 * t), f - 0.6 * S + 3 * S * t * t)).ToList();
        read = sheet.Read(new[] { Stroke(bass), Stroke(Dot(90 + 2.2 * S, f - 0.5 * S)), Stroke(Dot(90 + 2.2 * S, f + 0.5 * S)) });
        check("a curve with two dots to its right is a bass clef", sheet.ClefOf(2) == Clef.Bass && read.Used.Count == 3);
        sheet.Read(new[] { Stroke(Filled(300, Y(sheet, 2, 8))) });
        check("on a bass staff the bottom line is G2", sheet.Items.Single() is { } g2 && ScoreSheet.PitchName(sheet.Diatonic(g2), Accidental.None) == "G2");

        sheet = new ScoreSheet();
        y = Y(sheet, 0, 6);
        sheet.Read(new[] { Stroke(Filled(300, y)), Stroke(Filled(400, y - S)) });
        var arc = Enumerable.Range(0, 25).Select(i => i / 24.0).Select(t => (300 + 100 * t, y - 1.0 * S - 0.5 * S * t - 1.2 * S * Math.Sin(t * Math.PI))).ToList();
        sheet.Read(new[] { Stroke(arc) });
        check("an arc from one note to another is a slur over them", sheet.Items.SingleOrDefault(i => i.Kind == ScoreKind.Slur) is { Below: false } slur && slur.From != slur.To);

        // ----- what isn't music, rubbing out, undo -----
        sheet = new ScoreSheet();
        read = sheet.Read(new[] { Stroke(Line(300, Y(sheet, 0, 4), 340, Y(sheet, 0, 4) - 10, 3)) });
        check("a short slanted line means nothing: it stays as ink", !read.Changed && read.Used.Count == 0 && sheet.Items.Count == 0 && !sheet.CanUndo);

        sheet = new ScoreSheet();
        y = Y(sheet, 0, 6);
        sheet.Read(new[] { Stroke(Filled(300, y)), Stroke(Filled(360, y)) });
        var scribble = Enumerable.Range(0, 60).Select(i => (300 - 0.9 * S + (i % 2 == 0 ? 0 : 1.8 * S) + i * 0.1, y - 0.8 * S + i * 0.03 * S)).ToList();
        read = sheet.Read(new[] { Stroke(Enumerable.Range(0, scribble.Count - 1).SelectMany(i => Line(scribble[i].Item1, scribble[i].Item2, scribble[i + 1].Item1, scribble[i + 1].Item2, 4))) });
        check("a scribble over a note rubs it out, and only it", read.Changed && sheet.Items.Count == 1 && sheet.Items[0].X > 350);
        check("undo brings it back", sheet.Undo() && sheet.Items.Count == 2);
        check("the eraser rubs out what it goes over", sheet.EraseAlong(new[] { (360.0, y - 30), (360.0, y + 30) }) && sheet.Items.Count == 1 && sheet.Items[0].X < 350);
        // Erasing as it moves: a stroke of the eraser over both notes, in pieces, is one undo.
        sheet.Undo();
        bool firstGone = sheet.EraseAlong(new[] { (300.0, y - 30), (300.0, y + 30) });
        bool secondGone = sheet.EraseAlong(new[] { (360.0, y - 30), (360.0, y + 30) }, remember: false);
        check("rubbing out as the eraser moves takes each note it reaches, and one undo brings them all back",
            firstGone && secondGone && sheet.Items.Count == 0 && sheet.Undo() && sheet.Items.Count == 2);

        // ----- engraving, saving, playing -----
        sheet = new ScoreSheet();
        y = Y(sheet, 0, 5);
        strokes.Clear();
        foreach (double x in new[] { 200.0, 260.0 })
        {
            strokes.Add(Stroke(Filled(x, y)));
            strokes.Add(Stroke(Line(x + 0.6 * S, y, x + 0.6 * S, y - 3.4 * S)));
        }
        sheet.Read(strokes);
        sheet.Read(new[] { Stroke(Line(200 + 0.6 * S, y - 3.4 * S, 260 + 0.6 * S, y - 3.4 * S)) });
        sheet.Read(new[] { Stroke(Oval(340, Y(sheet, 0, 1))), Stroke(Line(340 - 0.6 * S, Y(sheet, 0, 1), 340 - 0.6 * S, Y(sheet, 0, 1) + 3.5 * S)) });
        sheet.Read(new[] { Stroke(Line(400, Y(sheet, 0, 0), 400, Y(sheet, 0, 8))) });
        sheet.Read(new[] { Stroke(Filled(460, Y(sheet, 0, 12))) });
        var svg = sheet.Svg();
        var scene = SvgScene.Parse(svg);
        check("the score is drawn as SVG the Windows app can build", scene is { Width: ScoreSheet.PageWidth } && scene.Height == sheet.Height && scene.Root.Children.Count > 20);
        check("a note under the staff gets its ledger lines", svg.Split("<rect").Length > 4);
        var back = ScoreSheet.FromJson(sheet.ToJson());
        check("the score is saved and read back as it was", back.ToJson() == sheet.ToJson() && back.Items.Count == sheet.Items.Count && back.Svg() == svg);
        check("a damaged save is an empty score", ScoreSheet.FromJson("{not json").Items.Count == 0);
        var tune = sheet.Playback();
        check("it plays left to right: two eighths, a half, then the low A", tune.Count == 4 && tune[0].Start == 0 && tune[1].Start == 0.5 && tune[2].Start == 1 && tune[3].Start == 3 && tune[3].Midi == 57);
        var wav = ScoreAudio.Wav(tune, sheet.Tempo);
        check("and sounds as a WAV of the right length", wav.Length > 44 && System.Text.Encoding.ASCII.GetString(wav, 0, 4) == "RIFF" && Math.Abs((wav.Length - 44) / 2.0 / ScoreAudio.Rate - (4 * 60.0 / sheet.Tempo + 0.8)) < 0.05);
    }
}
