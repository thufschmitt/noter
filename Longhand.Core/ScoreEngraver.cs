using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Longhand.Services;

/// <summary>
/// Draws a <see cref="ScoreSheet"/> as clean notation, in SVG: the staves and their clefs, and each item by the usual engraving
/// rules, whatever the hand that wrote it did. Noteheads sit exactly on their line or space, a stem goes up on the right of a
/// head below the middle line and down on the left of one above it (unless it was written the other way), a chord shares one
/// stem, beamed notes share one beam with stems lengthened to meet it, ledger lines are added above and below the staff,
/// accidentals stand just left of their note and dots in the space to its right. Everything is in <c>currentColor</c>, the
/// staff lines fainter, so the screen gives it the page's ink colour; the Windows app builds it into native shapes with
/// <see cref="SvgScene"/>, the web page shows it as it is.
/// </summary>
public static class ScoreEngraver
{
    const double S = ScoreSheet.Space;
    const double HeadRx = 0.6 * S, HeadRy = 0.42 * S, StemWidth = 0.12 * S, StemLength = 3.5 * S, LineWidth = 0.08 * S;

    public static string Svg(ScoreSheet sheet)
    {
        double width = ScoreSheet.PageWidth, height = sheet.Height;
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(width)}\" height=\"{F(height)}\" viewBox=\"0 0 {F(width)} {F(height)}\" fill=\"currentColor\">");
        // The staves.
        sb.Append($"<g stroke=\"currentColor\" stroke-opacity=\"0.55\" stroke-width=\"{F(LineWidth * 1.2)}\" fill=\"none\">");
        for (int staff = 0; staff < sheet.StaffCount; staff++)
            for (int line = 0; line < 5; line++)
            {
                double y = sheet.StaffTop(staff) + line * S;
                sb.Append($"<line x1=\"{F(ScoreSheet.Left)}\" y1=\"{F(y)}\" x2=\"{F(width - ScoreSheet.Right)}\" y2=\"{F(y)}\"/>");
            }
        sb.Append("</g>");
        for (int staff = 0; staff < sheet.StaffCount; staff++)
            sb.Append(sheet.ClefOf(staff) == Clef.Bass ? BassClef(ScoreSheet.Left + 0.5 * S, sheet.StaffTop(staff) + S) : TrebleClef(ScoreSheet.Left + 1.3 * S, sheet.StaffTop(staff) + 3 * S));

        foreach (var staffItems in sheet.Items.GroupBy(i => i.Staff))
            Staff(sb, sheet, staffItems.ToList());
        foreach (var slur in sheet.Items.Where(i => i.Kind == ScoreKind.Slur))
            Slur(sb, sheet, slur);
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>A note column: one head or a chord's heads, sharing a stem.</summary>
    sealed class Column
    {
        public required List<ScoreItem> Notes;
        public double X;
        public bool Up;
        public bool Stemmed => Notes.Max(n => n.Duration) >= 2;
        public int Duration => Notes.Max(n => n.Duration);
        public int Top => Notes.Min(n => n.Step);
        public int Bottom => Notes.Max(n => n.Step);
        public string? Beam;
        public double StemX => Up ? X + HeadRx - StemWidth / 2 : X - HeadRx + StemWidth / 2;
        /// <summary>The head the stem starts from, and where it ends.</summary>
        public double StemFrom, StemTo;
    }

    static void Staff(StringBuilder sb, ScoreSheet sheet, List<ScoreItem> items)
    {
        int staff = items[0].Staff;
        double Y(int step) => sheet.StepY(staff, step);

        foreach (var bar in items.Where(i => i.Kind == ScoreKind.Bar))
            sb.Append(Rect(bar.X - 0.07 * S, Y(0), 0.14 * S, 4 * S));
        foreach (var rest in items.Where(i => i.Kind == ScoreKind.Rest))
            Rest(sb, sheet, rest);
        foreach (var acc in items.Where(i => i.Kind == ScoreKind.Accidental))
            sb.Append(AccidentalGlyph(acc.Accidental, acc.X, Y(acc.Step)));

        // Notes into columns: those within half a space across are one chord (stemmed with stemmed only).
        var columns = new List<Column>();
        foreach (var note in items.Where(i => i.Kind == ScoreKind.Note).OrderBy(i => i.X))
        {
            var col = columns.LastOrDefault(c => Math.Abs(c.Notes[0].X - note.X) <= 0.5 * S && (c.Notes[0].Duration >= 2) == (note.Duration >= 2));
            if (col is null) columns.Add(new Column { Notes = new() { note } });
            else col.Notes.Add(note);
        }
        foreach (var col in columns)
        {
            col.X = col.Notes.Average(n => n.X);
            col.Beam = col.Notes.Select(n => n.Beam).FirstOrDefault(b => b is not null);
            col.Up = Direction(col.Notes, col.Top, col.Bottom);
        }

        // Beamed columns agree on one direction, and their stems meet one straight beam.
        foreach (var group in columns.Where(c => c.Beam is not null && c.Stemmed).GroupBy(c => c.Beam))
        {
            var cols = group.OrderBy(c => c.X).ToList();
            if (cols.Count < 2) { cols.ForEach(c => c.Beam = null); continue; }
            bool up = Direction(cols.SelectMany(c => c.Notes).ToList(), cols.Min(c => c.Top), cols.Max(c => c.Bottom));
            foreach (var c in cols) c.Up = up;
            // Natural stem ends, then a beam through the first and last, no steeper than a space, pushed out so every stem is long enough.
            double Natural(Column c) => up ? Math.Min(Y(c.Top) - StemLength, Y(4)) : Math.Max(Y(c.Bottom) + StemLength, Y(4));
            double x0 = cols[0].StemX, x1 = cols[^1].StemX;
            double y0 = Natural(cols[0]), y1 = Natural(cols[^1]);
            double slope = Math.Clamp((y1 - y0) / Math.Max(1, x1 - x0), -S / Math.Max(1, x1 - x0), S / Math.Max(1, x1 - x0));
            double shift = 0;
            foreach (var c in cols)
            {
                // Every stem at least two and a half spaces long.
                double at = y0 + slope * (c.StemX - x0);
                double inner = up ? Y(c.Top) - 2.5 * S : Y(c.Bottom) + 2.5 * S;
                shift = up ? Math.Min(shift, inner - at) : Math.Max(shift, inner - at);
            }
            y0 += shift;
            foreach (var c in cols)
            {
                c.StemFrom = up ? Y(c.Bottom) : Y(c.Top);
                c.StemTo = y0 + slope * (c.StemX - x0);
            }
            // The beams: the first for every note; a second where two neighbours are both sixteenths, or a stub for a lone one.
            double thick = 0.5 * S, gap = 0.75 * S;
            double dir = up ? 1 : -1; // the second beam is towards the heads
            sb.Append(Beam(x0 - StemWidth / 2, y0, x1 + StemWidth / 2, y0 + slope * (x1 - x0), thick, up));
            for (int i = 0; i < cols.Count; i++)
            {
                if (cols[i].Duration < 16) continue;
                bool nextToo = i + 1 < cols.Count && cols[i + 1].Duration >= 16;
                bool prevToo = i > 0 && cols[i - 1].Duration >= 16;
                double a = cols[i].StemX, b;
                if (nextToo) b = cols[i + 1].StemX;
                else if (prevToo) continue;
                else b = i + 1 < cols.Count ? a + 1.2 * S : a - 1.2 * S;
                double ya = y0 + slope * (a - x0) + dir * gap, yb = y0 + slope * (b - x0) + dir * gap;
                sb.Append(Beam(Math.Min(a, b) - StemWidth / 2, a < b ? ya : yb, Math.Max(a, b) + StemWidth / 2, a < b ? yb : ya, thick, up));
            }
        }

        foreach (var col in columns)
        {
            bool beamed = col.Beam is not null && col.Stemmed;
            if (!beamed)
            {
                col.StemFrom = col.Up ? Y(col.Bottom) : Y(col.Top);
                double tip = col.Up ? Y(col.Top) - StemLength : Y(col.Bottom) + StemLength;
                // A stem reaches at least the middle line, from far above or below the staff.
                tip = col.Up ? Math.Min(tip, Y(4)) : Math.Max(tip, Y(4));
                col.StemTo = tip;
            }
            // Ledger lines, as far as the furthest head.
            double lw = 2 * HeadRx + 0.6 * S;
            for (int step = -2; step >= col.Top; step -= 2) sb.Append(Rect(col.X - lw / 2, Y(step) - LineWidth, lw, 2 * LineWidth));
            for (int step = 10; step <= col.Bottom; step += 2) sb.Append(Rect(col.X - lw / 2, Y(step) - LineWidth, lw, 2 * LineWidth));
            // Heads; a second (two steps a line apart) goes on the other side of the stem.
            var sorted = col.Up ? col.Notes.OrderByDescending(n => n.Step).ToList() : col.Notes.OrderBy(n => n.Step).ToList();
            int? last = null;
            bool lastShifted = false;
            var placed = new Dictionary<ScoreItem, double>();
            foreach (var note in sorted)
            {
                bool shift = last is { } l && Math.Abs(l - note.Step) == 1 && !lastShifted;
                double x = col.X + (shift ? (col.Up ? 2 * HeadRx - StemWidth : -2 * HeadRx + StemWidth) : 0);
                placed[note] = x;
                sb.Append(Head(x, Y(note.Step), note.Duration));
                last = note.Step;
                lastShifted = shift;
            }
            if (col.Stemmed) sb.Append(Rect(col.StemX - StemWidth / 2, Math.Min(col.StemFrom, col.StemTo), StemWidth, Math.Abs(col.StemTo - col.StemFrom)));
            if (col.Stemmed && !beamed && col.Duration >= 8)
                for (int f = 0; f < (col.Duration >= 16 ? 2 : 1); f++)
                    sb.Append(Flag(col.StemX + StemWidth / 2 - 0.02 * S, col.StemTo + (col.Up ? f * 0.8 * S : -f * 0.8 * S), col.Up));
            // Accidentals to the left, staggered when they would collide; dots in a space, to the right.
            double accX = placed.Values.Min() - HeadRx - 0.85 * S;
            (int Step, bool Out)? lastAcc = null;
            foreach (var note in col.Notes.OrderBy(n => n.Step).Where(n => n.Accidental != Accidental.None))
            {
                bool outward = lastAcc is { } la && note.Step - la.Step < 5 && !la.Out;
                sb.Append(AccidentalGlyph(note.Accidental, outward ? accX - 1.0 * S : accX, Y(note.Step)));
                lastAcc = (note.Step, outward);
            }
            foreach (var note in col.Notes.Where(n => n.Dotted))
            {
                int step = note.Step % 2 == 0 ? note.Step - 1 : note.Step;
                double right = placed.Values.Max() + HeadRx + 0.55 * S;
                sb.Append(Circle(right, Y(step), 0.2 * S));
            }
        }
    }

    /// <summary>Up or down: as written when it was, else away from the middle line, by the head furthest from it.</summary>
    static bool Direction(List<ScoreItem> notes, int top, int bottom)
    {
        var chosen = notes.Select(n => n.Stem).Where(s => s != StemDir.Auto).ToList();
        if (chosen.Count > 0) return chosen.Count(s => s == StemDir.Up) >= chosen.Count(s => s == StemDir.Down);
        return 4 - top <= bottom - 4;
    }

    // ---------- the glyphs ----------

    static string Head(double x, double y, int duration)
    {
        if (duration >= 4) return Ellipse(x, y, HeadRx, HeadRy, -20);
        if (duration == 2)
        {
            // An open head: the outer oval, with a narrower one turned more steeply cut out of it.
            return $"<path fill-rule=\"evenodd\" d=\"{EllipsePath(x, y, HeadRx, HeadRy, -20)} {EllipsePath(x, y, HeadRx * 0.78, HeadRy * 0.42, -32)}\"/>";
        }
        return $"<path fill-rule=\"evenodd\" d=\"{EllipsePath(x, y, HeadRx * 1.25, HeadRy * 1.05, 0)} {EllipsePath(x, y, HeadRx * 0.48, HeadRy * 0.72, 55)}\"/>";
    }

    static string Flag(double x, double tipY, bool up)
    {
        double d = up ? 1 : -1;
        return $"<path d=\"M{F(x)} {F(tipY)} C{F(x + 0.1 * S)} {F(tipY + d * 1.1 * S)} {F(x + 1.25 * S)} {F(tipY + d * 1.35 * S)} {F(x + 0.85 * S)} {F(tipY + d * 2.9 * S)} " +
               $"C{F(x + 1.0 * S)} {F(tipY + d * 1.7 * S)} {F(x + 0.35 * S)} {F(tipY + d * 1.35 * S)} {F(x)} {F(tipY + d * 1.05 * S)} Z\"/>";
    }

    static string Beam(double x0, double y0, double x1, double y1, double thick, bool up)
    {
        // The beam's outer edge is on the stem ends; it grows towards the heads.
        double t = up ? thick : -thick;
        return $"<path d=\"M{F(x0)} {F(y0)} L{F(x1)} {F(y1)} L{F(x1)} {F(y1 + t)} L{F(x0)} {F(y0 + t)} Z\"/>";
    }

    static void Rest(StringBuilder sb, ScoreSheet sheet, ScoreItem rest)
    {
        double Y(int step) => sheet.StepY(rest.Staff, step);
        double x = rest.X;
        switch (rest.Duration)
        {
            case 1: // hangs from the fourth line
                sb.Append(Rect(x - 0.6 * S, Y(2), 1.2 * S, 0.5 * S));
                break;
            case 2: // sits on the middle line
                sb.Append(Rect(x - 0.6 * S, Y(4) - 0.5 * S, 1.2 * S, 0.5 * S));
                break;
            case 4:
            {
                // Thin down to the right, a thick stroke back to the left, thin to the right again, and a hook to finish.
                double y = Y(4);
                string P(double dx, double dy) => $"{F(x + dx * S)} {F(y + dy * S)}";
                sb.Append($"<path fill=\"none\" stroke=\"currentColor\" stroke-width=\"{F(0.13 * S)}\" stroke-linecap=\"round\" stroke-linejoin=\"round\" " +
                          $"d=\"M{P(-0.3, -1.45)} L{P(0.32, -0.62)} M{P(-0.28, 0.08)} L{P(0.3, 0.78)} C{P(-0.15, 0.55)} {P(-0.45, 0.95)} {P(-0.05, 1.45)}\"/>");
                sb.Append($"<path d=\"M{P(0.42, -0.54)} L{P(-0.16, 0.16)} L{P(-0.38, -0.0)} L{P(0.2, -0.7)} Z\" stroke=\"currentColor\" stroke-width=\"{F(0.1 * S)}\" stroke-linejoin=\"round\"/>");
                break;
            }
            default:
            {
                double y = Y(3);
                string P(double dx, double dy) => $"{F(x + dx * S)} {F(y + dy * S)}";
                sb.Append(Circle(x - 0.3 * S, y, 0.24 * S));
                sb.Append($"<path d=\"M{P(-0.45, 0.05)} C{P(-0.15, 0.4)} {P(0.25, 0.3)} {P(0.5, -0.15)} L{P(0.62, -0.1)} L{P(0.0, 2.05)} L{P(-0.18, 2.05)} L{P(0.36, 0.35)} C{P(0.15, 0.5)} {P(-0.25, 0.45)} {P(-0.45, 0.05)} Z\"/>");
                if (rest.Duration >= 16)
                {
                    sb.Append(Circle(x - 0.55 * S, y + S, 0.24 * S));
                    sb.Append($"<path d=\"M{P(-0.7, 1.05)} C{P(-0.4, 1.4)} {P(0.0, 1.3)} {P(0.25, 0.85)} L{P(0.36, 0.9)} L{P(0.11, 1.35)} C{P(-0.1, 1.5)} {P(-0.5, 1.45)} {P(-0.7, 1.05)} Z\"/>");
                }
                break;
            }
        }
        if (rest.Dotted) sb.Append(Circle(x + 1.0 * S, Y(3), 0.2 * S));
    }

    static string AccidentalGlyph(Accidental kind, double x, double y)
    {
        string P(double dx, double dy) => $"{F(x + dx * S)} {F(y + dy * S)}";
        switch (kind)
        {
            case Accidental.Sharp:
            {
                var sb = new StringBuilder();
                sb.Append(Rect(x - 0.28 * S - 0.06 * S, y - 1.3 * S + 0.1 * S, 0.11 * S, 2.6 * S));
                sb.Append(Rect(x + 0.28 * S - 0.06 * S, y - 1.3 * S - 0.1 * S, 0.11 * S, 2.6 * S));
                foreach (double dy in new[] { -0.45, 0.45 })
                    sb.Append($"<path d=\"M{P(-0.6, dy + 0.18)} L{P(0.6, dy - 0.18)} L{P(0.6, dy - 0.18 + 0.32)} L{P(-0.6, dy + 0.18 + 0.32)} Z\"/>");
                return sb.ToString();
            }
            case Accidental.Flat:
                return Rect(x - 0.35 * S, y - 1.9 * S, 0.12 * S, 2.45 * S)
                    + $"<path fill-rule=\"evenodd\" d=\"M{P(-0.25, 0.55)} C{P(0.3, 0.2)} {P(0.75, -0.15)} {P(0.5, -0.45)} C{P(0.3, -0.7)} {P(-0.05, -0.45)} {P(-0.25, -0.15)} Z " +
                      $"M{P(-0.23, 0.3)} C{P(0.15, 0.05)} {P(0.45, -0.25)} {P(0.32, -0.38)} C{P(0.18, -0.5)} {P(-0.05, -0.3)} {P(-0.23, -0.05)} Z\"/>";
            case Accidental.Natural:
                return Rect(x - 0.3 * S, y - 1.35 * S, 0.11 * S, 1.85 * S) + Rect(x + 0.2 * S, y - 0.5 * S, 0.11 * S, 1.85 * S)
                    + $"<path d=\"M{P(-0.3, -0.3)} L{P(0.31, -0.5)} L{P(0.31, -0.2)} L{P(-0.3, 0.0)} Z M{P(-0.3, 0.5)} L{P(0.31, 0.3)} L{P(0.31, 0.6)} L{P(-0.3, 0.8)} Z\"/>";
            default:
                return "";
        }
    }

    static void Slur(StringBuilder sb, ScoreSheet sheet, ScoreItem slur)
    {
        if (sheet.Find(slur.From) is not { } a || sheet.Find(slur.To) is not { } b) return;
        double d = slur.Below ? 1 : -1;
        double ax = a.X + 0.2 * S, bx = b.X - 0.2 * S;
        if (ax > bx) (ax, bx) = (bx, ax);
        double ay = sheet.StepY(a.Staff, a.Step) + d * 0.8 * S, by = sheet.StepY(b.Staff, b.Step) + d * 0.8 * S;
        double bulge = Math.Clamp((bx - ax) * 0.22, 0.6 * S, 2 * S);
        double mx = (ax + bx) / 2, my = (ay + by) / 2 + d * bulge;
        double inner = my - d * 0.22 * S;
        sb.Append($"<path d=\"M{F(ax)} {F(ay)} Q{F(mx)} {F(my + d * 0.2 * S)} {F(bx)} {F(by)} Q{F(mx)} {F(inner)} {F(ax)} {F(ay)} Z\"/>");
    }

    /// <summary>A G clef, its curl round the second line (<paramref name="y"/>), drawn as one thick stroke and a ball.</summary>
    static string TrebleClef(double x, double y)
    {
        string P(double dx, double dy) => $"{F(x + dx * S)} {F(y + dy * S)}";
        return $"<path fill=\"none\" stroke=\"currentColor\" stroke-width=\"{F(0.17 * S)}\" stroke-linecap=\"round\" stroke-linejoin=\"round\" d=\"" +
               $"M{P(0.12, 0.38)} C{P(-0.45, 0.32)} {P(-0.5, -0.5)} {P(0.12, -0.62)} C{P(0.95, -0.7)} {P(1.15, 0.6)} {P(0.2, 0.98)} " +
               $"C{P(-0.85, 1.25)} {P(-1.35, -0.15)} {P(-0.55, -1.0)} C{P(0.05, -1.7)} {P(0.95, -2.55)} {P(0.8, -3.6)} " +
               $"C{P(0.72, -4.35)} {P(0.2, -4.55)} {P(0.0, -3.85)} C{P(-0.22, -3.0)} {P(0.28, 0.6)} {P(0.42, 1.95)} " +
               $"C{P(0.52, 2.65)} {P(-0.15, 2.95)} {P(-0.42, 2.45)}\"/>" + Circle(x - 0.28 * S, y + 2.3 * S, 0.3 * S);
    }

    /// <summary>An F clef, its dot on the fourth line (<paramref name="y"/>) and the two dots either side of it.</summary>
    static string BassClef(double x, double y)
    {
        string P(double dx, double dy) => $"{F(x + dx * S)} {F(y + dy * S)}";
        return Circle(x + 0.3 * S, y + 0.05 * S, 0.32 * S)
            + $"<path d=\"M{P(0.05, 0.0)} C{P(0.1, -1.15)} {P(1.95, -1.2)} {P(1.95, 0.15)} C{P(1.95, 1.4)} {P(1.0, 2.3)} {P(-0.15, 2.95)} " +
              $"C{P(0.75, 2.25)} {P(1.6, 1.35)} {P(1.6, 0.15)} C{P(1.6, -0.95)} {P(0.5, -0.8)} {P(0.45, -0.25)} Z\"/>"
            + Circle(x + 2.45 * S, y - 0.5 * S, 0.15 * S) + Circle(x + 2.45 * S, y + 0.5 * S, 0.15 * S);
    }

    // ---------- SVG ----------

    static string Rect(double x, double y, double w, double h) => $"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(w)}\" height=\"{F(h)}\"/>";

    static string Circle(double cx, double cy, double r) => $"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(r)}\"/>";

    static string Ellipse(double cx, double cy, double rx, double ry, double degrees) => $"<path d=\"{EllipsePath(cx, cy, rx, ry, degrees)}\"/>";

    /// <summary>A turned ellipse as two half arcs, so it can be one path with a hole cut out of it.</summary>
    static string EllipsePath(double cx, double cy, double rx, double ry, double degrees)
    {
        double r = degrees * Math.PI / 180, dx = rx * Math.Cos(r), dy = rx * Math.Sin(r);
        return $"M{F(cx - dx)} {F(cy - dy)} A{F(rx)} {F(ry)} {F(degrees)} 1 0 {F(cx + dx)} {F(cy + dy)} A{F(rx)} {F(ry)} {F(degrees)} 1 0 {F(cx - dx)} {F(cy - dy)} Z";
    }

    static string F(double v) => Math.Round(v, 2).ToString(CultureInfo.InvariantCulture);
}
