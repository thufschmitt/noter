using System;
using System.Collections.Generic;
using System.Linq;

namespace Longhand.Services;

/// <summary>Where a day's timeline is on the page: the height of <see cref="From"/>, how tall a minute is, and the grid's sides.</summary>
public sealed record TimelineGeometry(double Top, double PxPerMinute, int From, int To, double Left, double Right)
{
    public double Bottom => Top + (To - From) * PxPerMinute;
    public double MinutesAt(double y) => From + (y - Top) / PxPerMinute;
}

/// <summary>A stroke of the user's ink, by id, through these points (page coordinates).</summary>
public sealed record InkPath(uint Id, IReadOnlyList<(double X, double Y)> Points);

/// <summary>An event the user drew: when (minutes since midnight), the strokes that marked the time, and those that are its title.</summary>
public sealed record DrawnEvent(int Start, int End, IReadOnlyList<uint> Shape, IReadOnlyList<uint> Words);

/// <summary>
/// Reads new events from ink drawn over a day's timeline, with no model: plain geometry. A closed shape (a box, an oval) marks an
/// event from its top to its bottom, and the writing inside it is the title. A line or bracket down the hours does the same, with the
/// writing to its right. Writing on its own is an event of an hour from where it starts. Times snap to 15 minutes.
/// </summary>
public static class InkEvents
{
    /// <summary>The smallest box that marks an event, in pixels: wider than a word's letters, and at least a quarter of an hour tall.</summary>
    const double BoxMinWidth = 60, BoxMinHeight = 14;
    /// <summary>A line or bracket is at least this tall (over half an hour), and much taller than it is wide.</summary>
    const double LineMinHeight = 40, LineMaxSlant = 0.4;
    /// <summary>How close a shape's two ends come for it to count as closed, as a share of its larger side.</summary>
    const double Closure = 0.35;
    /// <summary>How far to the right of a line its title can be written.</summary>
    const double TitleReach = 420;

    readonly record struct Box(double Left, double Top, double Right, double Bottom)
    {
        public double Width => Right - Left;
        public double Height => Bottom - Top;
        public double CX => (Left + Right) / 2;
        public double CY => (Top + Bottom) / 2;
        public bool Holds(double x, double y, double t) => x >= Left - t && x <= Right + t && y >= Top - t && y <= Bottom + t;
    }

    static Box BoxOf(IReadOnlyList<(double X, double Y)> p) => new(p.Min(q => q.X), p.Min(q => q.Y), p.Max(q => q.X), p.Max(q => q.Y));

    enum Kind { Box, Line, Word }

    static Kind KindOf(InkPath s, Box b)
    {
        var p = s.Points;
        if (p.Count < 3) return Kind.Word;
        double gap = Math.Sqrt(Math.Pow(p[0].X - p[^1].X, 2) + Math.Pow(p[0].Y - p[^1].Y, 2));
        bool closed = gap <= Closure * Math.Max(b.Width, b.Height);
        if (closed && b.Width >= BoxMinWidth && b.Height >= BoxMinHeight) return Kind.Box;
        if (!closed && b.Height >= LineMinHeight && b.Width <= LineMaxSlant * b.Height) return Kind.Line;
        return Kind.Word;
    }

    public static List<DrawnEvent> Find(IReadOnlyList<InkPath> strokes, TimelineGeometry grid)
    {
        // Only ink over the timeline counts; anything else is left alone.
        var all = strokes.Where(s => s.Points.Count > 0).Select(s => (Stroke: s, Box: BoxOf(s.Points))).ToList();
        var over = all.Where(a => a.Box.CY >= grid.Top - 8 && a.Box.CY <= grid.Bottom + 8 && a.Box.CX >= grid.Left - 40 && a.Box.CX <= grid.Right + 8).ToList();
        var shapes = new List<(Kind Kind, InkPath Stroke, Box Box, List<uint> Words)>();
        var words = new List<(InkPath Stroke, Box Box)>();
        foreach (var (s, b) in over)
        {
            var kind = KindOf(s, b);
            if (kind == Kind.Word) words.Add((s, b));
            else shapes.Add((kind, s, b, new List<uint>()));
        }

        // Each piece of writing goes with the smallest box around it, or else the nearest line to its left.
        var loose = new List<(InkPath Stroke, Box Box)>();
        foreach (var w in words)
        {
            var inBox = shapes.Where(x => x.Kind == Kind.Box && x.Box.Holds(w.Box.CX, w.Box.CY, 6)).OrderBy(x => x.Box.Width * x.Box.Height).FirstOrDefault();
            if (inBox.Words is not null) { inBox.Words.Add(w.Stroke.Id); continue; }
            var byLine = shapes.Where(x => x.Kind == Kind.Line && w.Box.CY >= x.Box.Top - 16 && w.Box.CY <= x.Box.Bottom + 16 && w.Box.CX > x.Box.Right - 4 && w.Box.CX < x.Box.Right + TitleReach)
                .OrderBy(x => w.Box.CX - x.Box.Right).FirstOrDefault();
            if (byLine.Words is not null) { byLine.Words.Add(w.Stroke.Id); continue; }
            loose.Add(w);
        }

        var found = new List<DrawnEvent>();
        foreach (var (_, s, b, ws) in shapes)
        {
            int start = Clamp(LayoutHit.Snap(grid.MinutesAt(b.Top)));
            int end = Math.Min(Layout.DayMinutes, Math.Max(start + Layout.SnapMinutes, Clamp(LayoutHit.Snap(grid.MinutesAt(b.Bottom)))));
            found.Add(new DrawnEvent(Math.Min(start, Layout.DayMinutes - Layout.SnapMinutes), end, new[] { s.Id }, ws));
        }

        // Writing on its own: one event for each line of it, from where the line starts, an hour long.
        foreach (var line in Lines(loose))
        {
            int start = Math.Min(Layout.DayMinutes - Layout.SnapMinutes, Clamp(LayoutHit.Snap(grid.MinutesAt(line.Min(w => w.Box.Top)))));
            found.Add(new DrawnEvent(start, Math.Min(Layout.DayMinutes, start + Layout.DefaultMinutes), Array.Empty<uint>(), line.Select(w => w.Stroke.Id).ToList()));
        }
        return found.OrderBy(e => e.Start).ThenBy(e => e.End).ToList();
    }

    static int Clamp(int minutes) => Math.Clamp(minutes, 0, Layout.DayMinutes);

    /// <summary>Pieces of writing grouped into lines: those whose heights overlap (or nearly), top to bottom.</summary>
    static List<List<(InkPath Stroke, Box Box)>> Lines(List<(InkPath Stroke, Box Box)> words)
    {
        var lines = new List<(double Top, double Bottom, List<(InkPath, Box)> Words)>();
        foreach (var w in words.OrderBy(w => w.Box.CY))
        {
            int i = lines.FindIndex(l => w.Box.CY >= l.Top - 10 && w.Box.CY <= l.Bottom + 10);
            if (i < 0) lines.Add((w.Box.Top, w.Box.Bottom, new() { w }));
            else
            {
                var l = lines[i];
                l.Words.Add(w);
                lines[i] = (Math.Min(l.Top, w.Box.Top), Math.Max(l.Bottom, w.Box.Bottom), l.Words);
            }
        }
        return lines.Select(l => l.Words).ToList();
    }
}
