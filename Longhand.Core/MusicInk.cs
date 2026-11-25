using System;
using System.Collections.Generic;
using System.Linq;

namespace Longhand.Services;

/// <summary>
/// Reads handwritten music on the staves, on the device and with no model: plain geometry against the staff lines, like the
/// calendar reads events against the hours. A batch of strokes (what was written since the pen last paused) is cut into simple
/// parts (a stroke that draws a notehead and its stem in one go becomes the two), each part is measured (size, how straight, how
/// closed, how densely inked, how often it turns back), and the parts are put together in a fixed order:
/// <list type="number">
/// <item>a scribble over something already engraved rubs it out;</item>
/// <item>clefs: a tall curl is a treble clef, a curve with two dots to its right a bass clef (they set that staff's clef);</item>
/// <item>accidentals: a "b" is a flat, two short uprights crossed by two short bars a sharp, an upright with a hook each way a natural;</item>
/// <item>noteheads, open or filled in, each with the stem drawn against it: open alone is a whole note, open with a stem a half,
/// filled a quarter; the line or space it is on is its pitch;</item>
/// <item>what changes notes already there (from this batch or before): a stem makes a whole note a half (or turns a stem round),
/// a beam across two or more stems makes them eighths (a second one sixteenths), a flag at a stem's end halves the note, a dot
/// to its right dots it, a short line through a head above or below the staff is a ledger line, taken in;</item>
/// <item>rests: a zigzag is a quarter, a "7" an eighth, a small block under the fourth line a whole, on the middle line a half;</item>
/// <item>a line down the whole staff is a bar line; an arc from one note to another a slur.</item>
/// </list>
/// What fits nothing stays as ink, and is tried again with the next batch (a sharp written in strokes far apart in time still
/// comes together). Engraving rules then decide how it all looks (<see cref="ScoreEngraver"/>).
/// </summary>
public static class MusicInk
{
    const double S = ScoreSheet.Space;

    /// <summary>A piece of a stroke, measured.</summary>
    sealed class Part
    {
        public required MusicStroke Source;
        public required List<(double X, double Y)> Pts;
        public double MinX, MaxX, MinY, MaxY, Length;
        public bool Used;
        public double W => MaxX - MinX;
        public double H => MaxY - MinY;
        public double Size => Math.Max(W, H);
        public double Cx => (MinX + MaxX) / 2;
        public double Cy => (MinY + MaxY) / 2;
        public (double X, double Y) Start => Pts[0];
        public (double X, double Y) End => Pts[^1];
        public double Chord => Dist(Start, End);
        /// <summary>1 for a straight line, less the more it wanders.</summary>
        public double Straight => Length > 0 ? Chord / Length : 1;
        /// <summary>How far the chord is from upright, in degrees (0 upright, 90 level).</summary>
        public double FromVertical => Math.Atan2(Math.Abs(End.X - Start.X), Math.Abs(End.Y - Start.Y)) * 180 / Math.PI;
        /// <summary>How much ink there is for its size: about 1 for a ring drawn once round, 2 and more for a head filled in.</summary>
        public double Density => Length / (Math.PI * Math.Max(1, (W + H) / 2));
        /// <summary>How many times it turns back on itself across.</summary>
        public int ReversalsX => Reversals(Pts, p => p.X);
        public int ReversalsY => Reversals(Pts, p => p.Y);
        /// <summary>The furthest the stroke gets from its chord.</summary>
        public double Sagitta
        {
            get
            {
                var (a, b) = (Start, End);
                double len = Dist(a, b);
                if (len < 1e-6) return Size;
                return Pts.Max(p => Math.Abs((b.X - a.X) * (a.Y - p.Y) - (a.X - p.X) * (b.Y - a.Y)) / len);
            }
        }
    }

    static double Dist((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    static int Reversals(List<(double X, double Y)> pts, Func<(double X, double Y), double> axis)
    {
        // A turn only counts once the pen has gone back far enough, so a wobble isn't one.
        double hysteresis = 0.2 * S, anchor = axis(pts[0]), extreme = anchor;
        int turns = 0, dir = 0;
        foreach (var p in pts)
        {
            double v = axis(p);
            if (dir == 0)
            {
                if (Math.Abs(v - anchor) > hysteresis) { dir = Math.Sign(v - anchor); extreme = v; }
            }
            else if (dir > 0)
            {
                if (v > extreme) extreme = v;
                else if (extreme - v > hysteresis) { turns++; dir = -1; extreme = v; }
            }
            else
            {
                if (v < extreme) extreme = v;
                else if (v - extreme > hysteresis) { turns++; dir = 1; extreme = v; }
            }
        }
        return turns;
    }

    static Part Measure(MusicStroke source, List<(double X, double Y)> pts)
    {
        var part = new Part { Source = source, Pts = pts, MinX = pts.Min(p => p.X), MaxX = pts.Max(p => p.X), MinY = pts.Min(p => p.Y), MaxY = pts.Max(p => p.Y) };
        // Measured between points a little apart, so the tremor of a hand on a dense digitizer doesn't count as length.
        var last = pts[0];
        for (int i = 1; i < pts.Count; i++)
            if (Dist(last, pts[i]) >= 0.12 * S || i == pts.Count - 1) { part.Length += Dist(last, pts[i]); last = pts[i]; }
        return part;
    }

    // ---------- shapes ----------

    static bool IsDot(Part p) => p.Size < 0.45 * S;

    static bool IsHead(Part p) =>
        p.Size >= 0.45 * S && p.Size <= 1.9 * S && Math.Min(p.W, p.H) >= 0.4 * p.Size && p.Straight < 0.6
        && (Dist(p.Start, p.End) <= 0.55 * p.Size || p.Density >= 1.6);

    static bool IsFilled(Part p) => p.Density >= 1.6;

    static bool IsUpright(Part p, double minLength) => p.Straight >= 0.9 && p.FromVertical <= 22 && p.H >= minLength;

    static bool IsLevelish(Part p, double maxAngle, double minLength) => p.Straight >= 0.85 && p.FromVertical >= 90 - maxAngle && p.Chord >= minLength;

    static bool IsTreble(Part p) => p.H >= 4.2 * S && p.W >= 0.8 * S && p.Length >= 2 * p.H;

    /// <summary>A "b": down a straight upright from the top, then a bowl out to the right at the bottom and back.</summary>
    static bool IsFlat(Part p)
    {
        if (p.H < 1.6 * S || p.H > 4.2 * S || p.W < 0.4 * S || p.W > 1.8 * S) return false;
        if (p.Start.Y - p.MinY > 0.25 * p.H) return false;
        int bottom = Enumerable.Range(0, p.Pts.Count).MaxBy(i => p.Pts[i].Y);
        if (bottom < 2 || bottom >= p.Pts.Count - 2) return false;
        var down = Measure(p.Source, p.Pts.GetRange(0, bottom + 1));
        if (down.Straight < 0.88 || down.FromVertical > 25 || down.H < 0.55 * p.H) return false;
        var bowl = p.Pts.Skip(bottom).ToList();
        double right = bowl.Max(q => q.X), top = bowl.Min(q => q.Y);
        double stemX = (p.Start.X + p.Pts[bottom].X) / 2;
        return right >= stemX + 0.35 * S && p.MaxY - top <= 0.65 * p.H && p.End.X <= stemX + 0.6 * S;
    }

    /// <summary>A natural drawn in one stroke: down, across to the right, down again, the two uprights apart.</summary>
    static bool IsNatural(Part p)
    {
        if (p.H < 1.8 * S || p.H > 4 * S || p.W < 0.35 * S || p.W > 1.4 * S || p.ReversalsY < 1 || p.ReversalsY > 2) return false;
        // Its left upright starts high and its right one ends low.
        return p.Start.X < p.Cx && p.End.X > p.Cx && p.Start.Y < p.Cy && p.End.Y > p.Cy && p.ReversalsX <= 1;
    }

    static bool IsZigzag(Part p) =>
        p.H >= 1.6 * S && p.H <= 4.5 * S && p.W >= 0.4 * S && p.W <= 2 * S && p.H >= 1.3 * p.W && p.ReversalsX >= 2 && p.Straight < 0.85;

    /// <summary>A "7": across the top to the right, then down and back to the left.</summary>
    static bool IsSeven(Part p)
    {
        if (p.H < 1.2 * S || p.H > 3.2 * S || p.W < 0.5 * S || p.W > 2 * S) return false;
        int right = Enumerable.Range(0, p.Pts.Count).MaxBy(i => p.Pts[i].X);
        var corner = p.Pts[right];
        return corner.Y - p.MinY <= 0.35 * p.H && p.Start.X < corner.X - 0.4 * S && p.Start.Y - p.MinY <= 0.5 * p.H
            && p.End.Y >= p.MinY + 0.75 * p.H && p.End.X < corner.X - 0.25 * S && p.ReversalsX <= 1;
    }

    static bool IsBlock(Part p) =>
        p.W >= 0.6 * S && p.W <= 2.2 * S && p.H <= 0.8 * S && p.W >= 1.5 * p.H && (p.Straight >= 0.85 && p.FromVertical >= 78 || p.ReversalsX >= 2 || p.ReversalsY >= 2);

    static bool IsCurve(Part p) =>
        p.W >= 1.5 * S && p.H <= 0.7 * p.W && p.Straight is >= 0.55 and <= 0.985 && p.Sagitta >= 0.08 * p.W && p.FromVertical >= 60 && p.ReversalsY <= 1;

    /// <summary>Back and forth over something, many times: the scrub that rubs out.</summary>
    static bool IsScribble(Part p) => p.Size >= 1.6 * S && Math.Max(p.ReversalsX, p.ReversalsY) >= 4 && p.Length >= 3.5 * p.Size;

    // ---------- cutting a stroke ----------

    /// <summary>
    /// A notehead and its stem written in one stroke, cut into the two: the longest straight upright run at either end, when it is
    /// long enough for a stem and what is left looks like a head. Anything else stays whole.
    /// </summary>
    static List<Part> Cut(MusicStroke stroke, List<(double X, double Y)> pts)
    {
        var whole = Measure(stroke, pts);
        if (pts.Count < 8 || whole.H < 2 * S) return new() { whole };
        // Shapes made of an upright and a bowl or a hook are read whole.
        if (IsFlat(whole) || IsTreble(whole) || IsNatural(whole) || IsZigzag(whole) || IsSeven(whole) || IsScribble(whole)) return new() { whole };
        foreach (bool fromEnd in new[] { true, false })
        {
            var seq = fromEnd ? pts : Enumerable.Reverse(pts).ToList();
            int k = StraightRunStart(seq);
            if (k <= 2) continue;
            var stem = Measure(stroke, seq.GetRange(k, seq.Count - k));
            if (stem.H < 2 * S || stem.FromVertical > 22) continue;
            var head = Measure(stroke, seq.GetRange(0, k + 1));
            if (!IsHead(head)) continue;
            return new() { head, stem };
        }
        return new() { whole };
    }

    /// <summary>The earliest index from which the rest of the points are a straight, upright line.</summary>
    static int StraightRunStart(List<(double X, double Y)> seq)
    {
        var end = seq[^1];
        int best = seq.Count - 1;
        for (int k = seq.Count - 2; k >= 0; k--)
        {
            var a = seq[k];
            double len = Dist(a, end);
            if (len < 1e-6) { best = k; continue; }
            // The line runs one way: going back along it gets ever further from its end (a head's curve doubling back stops it).
            if (len < Dist(seq[k + 1], end) - 0.05 * S) break;
            bool straight = true;
            for (int i = k + 1; i < seq.Count - 1 && straight; i++)
            {
                var p = seq[i];
                straight = Math.Abs((end.X - a.X) * (a.Y - p.Y) - (a.X - p.X) * (end.Y - a.Y)) / len <= 0.18 * S;
            }
            double angle = Math.Atan2(Math.Abs(end.X - a.X), Math.Abs(end.Y - a.Y)) * 180 / Math.PI;
            if (!straight || len > 0.6 * S && angle > 22) break;
            best = k;
        }
        return best;
    }

    /// <summary>How each stroke is cut and measured, and which shapes its parts pass: for working out why something wasn't read.</summary>
    public static IEnumerable<string> Explain(IReadOnlyList<MusicStroke> strokes)
    {
        foreach (var stroke in strokes)
            foreach (var p in Cut(stroke, Dedupe(stroke.Points)))
            {
                var shapes = new (string Name, bool Is)[]
                {
                    ("dot", IsDot(p)), ("head", IsHead(p)), ("filled", IsFilled(p)), ("upright", IsUpright(p, 1.1 * S)), ("treble", IsTreble(p)), ("flat", IsFlat(p)),
                    ("natural", IsNatural(p)), ("zigzag", IsZigzag(p)), ("seven", IsSeven(p)), ("block", IsBlock(p)), ("curve", IsCurve(p)), ("scribble", IsScribble(p)),
                };
                yield return $"stroke {stroke.Id}: {p.W / S:0.00}x{p.H / S:0.00} spaces at ({p.Cx:0},{p.Cy:0}), straight {p.Straight:0.00}, {p.FromVertical:0}° from upright, " +
                             $"density {p.Density:0.00}, turns {p.ReversalsX}/{p.ReversalsY}, sagitta {p.Sagitta / S:0.00}: " + string.Join(" ", shapes.Where(x => x.Is).Select(x => x.Name));
            }
    }

    // ---------- reading ----------

    public static MusicRead Read(ScoreSheet sheet, IReadOnlyList<MusicStroke> strokes)
    {
        var said = new List<string>();
        var parts = new List<Part>();
        foreach (var stroke in strokes)
        {
            var pts = Dedupe(stroke.Points);
            if (pts.Count == 0) continue;
            parts.AddRange(Cut(stroke, pts));
        }
        if (parts.Count == 0) return new MusicRead(Array.Empty<uint>(), false, "");

        sheet.Remember();
        int before = sheet.Items.Count;
        var clefsBefore = Enumerable.Range(0, sheet.StaffCount).Select(sheet.ClefOf).ToList();
        var used = new HashSet<uint>();
        bool changed = false;

        // A part used makes its whole stroke used: the other half of a cut stroke went with it.
        void Use(Part p) { p.Used = true; used.Add(p.Source.Id); }
        IEnumerable<Part> Free() => parts.Where(p => !p.Used);

        // 1. Scribbles over what is engraved rub it out (and any ink still waiting under them).
        foreach (var p in Free().Where(IsScribble).ToList())
        {
            var box = (p.MinX - 0.2 * S, p.MinY - 0.2 * S, p.MaxX + 0.2 * S, p.MaxY + 0.2 * S);
            var hit = sheet.Items.Where(i => i.Kind != ScoreKind.Slur && Inside(Centre(sheet.Bounds(i)), box)).Select(i => i.Id).ToList();
            if (hit.Count == 0) continue;
            sheet.Remove(hit);
            Use(p);
            foreach (var other in Free().Where(o => Inside((o.Cx, o.Cy), box)).ToList()) Use(other);
            said.Add(hit.Count == 1 ? "rubbed out 1 thing" : $"rubbed out {hit.Count} things");
            changed = true;
        }

        // 2. Clefs.
        foreach (var p in Free().Where(IsTreble).ToList())
        {
            sheet.SetClef(sheet.StaffAt(p.Cy), Clef.Treble);
            Use(p);
            said.Add("treble clef");
        }
        foreach (var p in Free().Where(p => !IsDot(p) && p.Straight < 0.85 && p.H >= 1.5 * S && p.H <= 4.5 * S && p.W >= 0.7 * S && p.W <= 3 * S).ToList())
        {
            var dots = Free().Where(d => IsDot(d) && d.Cx >= p.MaxX - 0.3 * S && d.Cx <= p.MaxX + 1.8 * S && d.Cy >= p.MinY - 0.5 * S && d.Cy <= p.MinY + 0.8 * p.H).Take(2).ToList();
            if (dots.Count < 2 || Math.Abs(dots[0].Cy - dots[1].Cy) < 0.5 * S) continue;
            sheet.SetClef(sheet.StaffAt(p.Cy), Clef.Bass);
            Use(p);
            dots.ForEach(Use);
            said.Add("bass clef");
        }

        // 3. Accidentals, before the uprights they are drawn with can be taken for stems.
        var accidentals = new List<ScoreItem>();
        void AddAccidental(double x, double y, Accidental kind)
        {
            int staff = sheet.StaffAt(y);
            var item = new ScoreItem { Id = sheet.NewId(), Kind = ScoreKind.Accidental, Staff = staff, X = x, Step = sheet.StepAt(staff, y), Accidental = kind };
            sheet.Items.Add(item);
            accidentals.Add(item);
        }
        foreach (var p in Free().Where(IsFlat).ToList())
        {
            // A flat sits on its bowl.
            var bowlTop = p.Pts.Skip(p.Pts.FindIndex(q => q.Y == p.MaxY)).Min(q => q.Y);
            AddAccidental(p.Cx, (bowlTop + p.MaxY) / 2, Accidental.Flat);
            Use(p);
        }
        foreach (var p in Free().Where(IsNatural).ToList())
        {
            AddAccidental(p.Cx, p.Cy, Accidental.Natural);
            Use(p);
        }
        var uprights = Free().Where(p => IsUpright(p, 1.1 * S) && p.H <= 3.6 * S).ToList();
        foreach (var a in uprights)
        {
            if (a.Used) continue;
            var b = uprights.FirstOrDefault(o => o != a && !o.Used && Math.Abs(o.Cx - a.Cx) is >= 0.25 * S and <= 1.3 * S
                && Math.Min(o.MaxY, a.MaxY) - Math.Max(o.MinY, a.MinY) >= 0.5 * Math.Min(o.H, a.H));
            if (b is null) continue;
            double x0 = Math.Min(a.Cx, b.Cx) - 0.6 * S, x1 = Math.Max(a.Cx, b.Cx) + 0.6 * S;
            double y0 = Math.Min(a.MinY, b.MinY), y1 = Math.Max(a.MaxY, b.MaxY);
            var bars = Free().Where(o => o != a && o != b && o.Chord >= 0.5 * S && o.Chord <= 2.2 * S && o.FromVertical >= 50 && o.Straight >= 0.8
                && o.Cx >= x0 && o.Cx <= x1 && o.Cy >= y0 && o.Cy <= y1).Take(2).ToList();
            if (bars.Count < 2) continue;
            AddAccidental((a.Cx + b.Cx) / 2, (bars[0].Cy + bars[1].Cy) / 2, Accidental.Sharp);
            Use(a); Use(b); bars.ForEach(Use);
        }
        if (accidentals.Count > 0) { said.Add(Count(accidentals.Count, "accidental")); changed = true; }

        // 4. Noteheads, with the stems drawn against them.
        var heads = Free().Where(IsHead).OrderBy(p => p.Cx).ToList();
        var stems = Free().Where(p => IsUpright(p, 1.5 * S)).ToList();
        var newNotes = new List<(ScoreItem Note, Part Head)>();
        foreach (var h in heads)
        {
            int staff = sheet.StaffAt(h.Cy);
            if (h.Cx < ScoreSheet.Left + ScoreSheet.ClefWidth * 0.6) continue; // over the clef
            var note = new ScoreItem
            {
                Id = sheet.NewId(), Kind = ScoreKind.Note, Staff = staff, X = h.Cx, Step = sheet.StepAt(staff, h.Cy),
                Duration = IsFilled(h) ? 4 : 1,
            };
            sheet.Items.Add(note);
            newNotes.Add((note, h));
            Use(h);
        }
        foreach (var stem in stems.Where(s => !s.Used))
        {
            // The head a stem starts at: one end against it, the other well away.
            var attached = newNotes.Where(n => StemFits(stem, n.Head.MinX, n.Head.MaxX, n.Head.Cy))
                .OrderBy(n => Math.Min(Math.Abs(stem.MinY - n.Head.Cy), Math.Abs(stem.MaxY - n.Head.Cy))).ToList();
            if (attached.Count == 0) continue;
            var main = attached[0];
            bool up = Math.Abs(stem.MaxY - main.Head.Cy) < Math.Abs(stem.MinY - main.Head.Cy);
            // A stem through several heads is a chord's.
            var chord = newNotes.Where(n => n == main || Math.Abs(n.Head.Cx - main.Head.Cx) <= 0.9 * S && n.Head.Cy >= stem.MinY - 0.6 * S && n.Head.Cy <= stem.MaxY + 0.6 * S);
            foreach (var (note, _) in chord)
            {
                note.Stem = up ? StemDir.Up : StemDir.Down;
                if (note.Duration == 1) note.Duration = 2;
            }
            Use(stem);
        }
        if (newNotes.Count > 0) { said.Add(Count(newNotes.Count, "note")); changed = true; }
        var touched = new HashSet<ScoreItem>(newNotes.Select(n => n.Note));

        // 5. What changes notes: stems added later, ledger lines, beams, flags, dots.
        var notes = sheet.Items.Where(i => i.Kind == ScoreKind.Note).ToList();
        foreach (var stem in Free().Where(p => IsUpright(p, 1.5 * S)).ToList())
        {
            var note = notes.Where(n => StemFits(stem, n.X - 0.75 * S, n.X + 0.75 * S, sheet.StepY(n.Staff, n.Step)))
                .OrderBy(n => Math.Abs(n.X - stem.Cx)).FirstOrDefault();
            if (note is null) continue;
            double y = sheet.StepY(note.Staff, note.Step);
            note.Stem = Math.Abs(stem.MaxY - y) < Math.Abs(stem.MinY - y) ? StemDir.Up : StemDir.Down;
            if (note.Duration == 1) note.Duration = 2;
            touched.Add(note);
            Use(stem);
        }
        foreach (var p in Free().Where(p => IsLevelish(p, 15, 0.6 * S) && p.W <= 2.8 * S).ToList())
        {
            int staff = sheet.StaffAt(p.Cy);
            double top = sheet.StaffTop(staff), bottom = top + 4 * S;
            if (p.Cy > top - 0.5 * S && p.Cy < bottom + 0.5 * S) continue;
            bool ledger = notes.Any(n => n.Staff == staff && (n.Step < 0 || n.Step > 8) && n.X >= p.MinX - 0.5 * S && n.X <= p.MaxX + 0.5 * S
                && (n.Step < 0 ? p.Cy >= sheet.StepY(staff, n.Step) - 0.6 * S : p.Cy <= sheet.StepY(staff, n.Step) + 0.6 * S));
            if (ledger) Use(p);
        }
        foreach (var p in Free().Where(p => IsLevelish(p, 35, 1.2 * S)).OrderBy(p => p.Cy).ToList())
        {
            var under = notes.Where(n => n.Duration >= 2 && n.X >= p.MinX - 0.9 * S && n.X <= p.MaxX + 0.9 * S && BeamReaches(sheet, n, p)).OrderBy(n => n.X).ToList();
            // One note per place across: a chord's notes are the same stem.
            if (under.Select(n => Math.Round(n.X / S)).Distinct().Count() < 2) continue;
            bool again = under.All(n => n.Beam is not null && n.Beam == under[0].Beam && n.Duration >= 8);
            string group = under.Select(n => n.Beam).FirstOrDefault(b => b is not null) ?? sheet.NewId();
            foreach (var n in notes.Where(n => n.Beam is not null && under.Any(u => u.Beam == n.Beam)).Concat(under).Distinct())
            {
                n.Beam = group;
                n.Duration = again ? 16 : Math.Max(n.Duration, 8);
                touched.Add(n);
            }
            foreach (var n in under)
                n.Stem = p.Cy < sheet.StepY(n.Staff, n.Step) ? StemDir.Up : StemDir.Down;
            Use(p);
            said.Add(again ? "second beam" : "beam");
        }
        foreach (var p in Free().Where(p => !IsDot(p) && p.H >= 0.8 * S && p.H <= 3.5 * S && p.W >= 0.3 * S && p.W <= 2.2 * S && p.Straight < 0.97).ToList())
        {
            var note = notes.Where(n => n.Beam is null && n.Duration >= 2 && FlagReaches(sheet, n, p)).OrderBy(n => Math.Abs(n.X - p.MinX)).FirstOrDefault();
            if (note is null) continue;
            note.Duration = note.Duration >= 4 ? Math.Min(16, note.Duration * 2) : 8;
            if (note.Stem == StemDir.Auto) note.Stem = p.Cy < sheet.StepY(note.Staff, note.Step) ? StemDir.Up : StemDir.Down;
            touched.Add(note);
            Use(p);
            said.Add("flag");
        }

        // 6. Rests (before dots, which can belong to them).
        var rests = new List<ScoreItem>();
        void AddRest(Part p, int duration)
        {
            int staff = sheet.StaffAt(p.Cy);
            var rest = new ScoreItem { Id = sheet.NewId(), Kind = ScoreKind.Rest, Staff = staff, X = p.Cx, Step = 4, Duration = duration };
            sheet.Items.Add(rest);
            rests.Add(rest);
            Use(p);
        }
        foreach (var p in Free().Where(IsZigzag).ToList()) AddRest(p, 4);
        foreach (var p in Free().Where(IsSeven).ToList())
        {
            AddRest(p, 8);
            // The blob at the top of the 7 may be a stroke of its own.
            foreach (var d in Free().Where(d => IsDot(d) && d.Cx < p.Cx && d.Cx >= p.MinX - 0.8 * S && Math.Abs(d.Cy - p.MinY) <= 0.7 * S).ToList()) Use(d);
        }
        foreach (var p in Free().Where(IsBlock).ToList())
        {
            int staff = sheet.StaffAt(p.Cy);
            double step = (p.Cy - sheet.StaffTop(staff)) / (S / 2);
            if (step < 1.5 || step > 4.6) continue;
            AddRest(p, step < 3 ? 1 : 2);
        }
        if (rests.Count > 0) { said.Add(Count(rests.Count, "rest")); changed = true; }

        // Dots after notes and rests.
        var dotted = sheet.Items.Where(i => i.Kind is ScoreKind.Note or ScoreKind.Rest).ToList();
        foreach (var p in Free().Where(IsDot).ToList())
        {
            var item = dotted.Where(i => !i.Dotted && p.Cx >= i.X + 0.4 * S && p.Cx <= i.X + 2.2 * S
                && Math.Abs(p.Cy - (i.Kind == ScoreKind.Rest ? sheet.StepY(i.Staff, 4) : sheet.StepY(i.Staff, i.Step))) <= (i.Kind == ScoreKind.Rest ? 1.5 : 1) * S)
                .OrderBy(i => p.Cx - i.X).FirstOrDefault();
            if (item is null) continue;
            item.Dotted = true;
            touched.Add(item);
            Use(p);
            said.Add("dot");
        }

        // 7. Bar lines and slurs.
        int barsAdded = 0;
        foreach (var p in Free().Where(p => IsUpright(p, 2.8 * S)).ToList())
        {
            int staff = sheet.StaffAt(p.Cy);
            double top = sheet.StaffTop(staff);
            if (p.MinY > top + 1.2 * S || p.MaxY < top + 2.8 * S || p.MinY < top - 2 * S || p.MaxY > top + 6 * S) continue;
            sheet.Items.Add(new ScoreItem { Id = sheet.NewId(), Kind = ScoreKind.Bar, Staff = staff, X = p.Cx, Step = 4 });
            Use(p);
            barsAdded++;
        }
        if (barsAdded > 0) { said.Add(Count(barsAdded, "bar line")); changed = true; }
        notes = sheet.Items.Where(i => i.Kind == ScoreKind.Note).ToList();
        foreach (var p in Free().Where(IsCurve).ToList())
        {
            var (left, right) = p.Start.X <= p.End.X ? (p.Start, p.End) : (p.End, p.Start);
            ScoreItem? Near((double X, double Y) end) => notes
                .Where(n => Math.Abs(n.X - end.X) <= 1.5 * S && Math.Abs(sheet.StepY(n.Staff, n.Step) - end.Y) <= 2.5 * S)
                .OrderBy(n => Dist((n.X, sheet.StepY(n.Staff, n.Step)), end)).FirstOrDefault();
            var a = Near(left);
            var b = Near(right);
            if (a is null || b is null || a == b) continue;
            // It bulges the way the middle of the stroke went from the chord.
            var mid = p.Pts.MaxBy(q => Math.Abs((right.X - left.X) * (left.Y - q.Y) - (left.X - q.X) * (right.Y - left.Y)));
            double chordY = left.Y + (right.Y - left.Y) * (mid.X - left.X) / Math.Max(1e-6, right.X - left.X);
            sheet.Items.Add(new ScoreItem { Id = sheet.NewId(), Kind = ScoreKind.Slur, Staff = a.Staff, X = a.X, From = a.Id, To = b.Id, Below = mid.Y > chordY });
            Use(p);
            said.Add("slur");
            changed = true;
        }

        // An accidental belongs to the note just to its right on the same line or space, whenever that note comes.
        foreach (var acc in sheet.Items.Where(i => i.Kind == ScoreKind.Accidental).ToList())
        {
            var note = sheet.Items.Where(n => n.Kind == ScoreKind.Note && n.Staff == acc.Staff && Math.Abs(n.Step - acc.Step) <= 1 && n.X > acc.X && n.X - acc.X <= 3.5 * S)
                .OrderBy(n => Math.Abs(n.Step - acc.Step) * 10 + n.X - acc.X).FirstOrDefault();
            if (note is null) continue;
            note.Accidental = acc.Accidental;
            sheet.Items.Remove(acc);
            touched.Add(note);
            changed = true;
        }

        changed |= touched.Count > 0 || sheet.Items.Count != before || Enumerable.Range(0, clefsBefore.Count).Any(i => sheet.ClefOf(i) != clefsBefore[i]);
        if (!changed) sheet.Forget();
        return new MusicRead(used.ToList(), changed, string.Join(", ", said.Distinct()));
    }

    static string Count(int n, string what) => n == 1 ? "1 " + what : $"{n} {what}s";

    static (double X, double Y) Centre((double X0, double Y0, double X1, double Y1) b) => ((b.X0 + b.X1) / 2, (b.Y0 + b.Y1) / 2);

    static bool Inside((double X, double Y) p, (double X0, double Y0, double X1, double Y1) b) => p.X >= b.X0 && p.X <= b.X1 && p.Y >= b.Y0 && p.Y <= b.Y1;

    /// <summary>A stem against a head: beside it or through its edge, one end at the head, the other a stem's length away.</summary>
    static bool StemFits(Part stem, double headMinX, double headMaxX, double headY)
    {
        double x = stem.Cx;
        if (x < headMinX - 0.5 * S || x > headMaxX + 0.5 * S) return false;
        double near = Math.Min(Math.Abs(stem.MinY - headY), Math.Abs(stem.MaxY - headY));
        double far = Math.Max(Math.Abs(stem.MinY - headY), Math.Abs(stem.MaxY - headY));
        return near <= 0.9 * S && far >= 1.6 * S;
    }

    /// <summary>Whether a beam passes where a note's stem would end: above the head for one going up, below for one going down.</summary>
    static bool BeamReaches(ScoreSheet sheet, ScoreItem note, Part beam)
    {
        double head = sheet.StepY(note.Staff, note.Step);
        double t = beam.End.X == beam.Start.X ? 0.5 : (note.X - beam.Start.X) / (beam.End.X - beam.Start.X);
        double y = beam.Start.Y + (beam.End.Y - beam.Start.Y) * Math.Clamp(t, 0, 1);
        double d = head - y; // above the head when positive
        if (note.Stem == StemDir.Up && d < 0 || note.Stem == StemDir.Down && d > 0) return false;
        return Math.Abs(d) is >= 1.5 * S and <= 5.5 * S;
    }

    /// <summary>Whether a stroke hangs from a note's stem end like a flag: starting there and going out to the right.</summary>
    static bool FlagReaches(ScoreSheet sheet, ScoreItem note, Part flag)
    {
        double head = sheet.StepY(note.Staff, note.Step);
        double hx = 0.55 * S;
        bool up = note.Stem switch { StemDir.Up => true, StemDir.Down => false, _ => flag.Cy < head };
        double stemX = up ? note.X + hx : note.X - hx;
        double tipY = up ? head - 3.5 * S : head + 3.5 * S;
        if (flag.MinX < stemX - 0.6 * S || flag.MinX > stemX + 0.8 * S || flag.MaxX < stemX + 0.2 * S) return false;
        // It starts at the tip's end and goes back towards the head.
        double startY = up ? flag.MinY : flag.MaxY;
        return Math.Abs(startY - tipY) <= 1.4 * S && (up ? flag.Cy < head - 0.5 * S : flag.Cy > head + 0.5 * S);
    }

    static List<(double X, double Y)> Dedupe(IReadOnlyList<(double X, double Y)> points)
    {
        var list = new List<(double X, double Y)>(points.Count);
        foreach (var p in points)
            if (double.IsFinite(p.X) && double.IsFinite(p.Y) && (list.Count == 0 || Dist(list[^1], p) > 0.25)) list.Add(p);
        return list;
    }
}
