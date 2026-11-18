using System;
using System.Collections.Generic;

namespace Longhand.Services;

/// <summary>
/// Whether the eraser, moving from one point to the next, touches a stroke: the stroke erasing that Windows Ink did for us, as plain
/// geometry so any platform's raw pen input can use it. A stroke is taken whole once the eraser's path comes within its reach.
/// </summary>
public static class EraserHit
{
    /// <summary>How close (page pixels) the eraser's path must come to a stroke's line to take it.</summary>
    public const double Reach = 5;

    /// <summary>Whether the segment a→b comes within <paramref name="reach"/> of the polyline through <paramref name="stroke"/>.</summary>
    public static bool Touches(IReadOnlyList<(double X, double Y)> stroke, (double X, double Y) a, (double X, double Y) b, double reach = Reach)
    {
        if (stroke.Count == 0) return false;
        if (stroke.Count == 1) return SegmentPoint(a, b, stroke[0]) <= reach;
        for (int i = 1; i < stroke.Count; i++)
            if (SegmentSegment(a, b, stroke[i - 1], stroke[i]) <= reach) return true;
        return false;
    }

    /// <summary>The distance from p to the segment a→b.</summary>
    static double SegmentPoint((double X, double Y) a, (double X, double Y) b, (double X, double Y) p)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
        double t = len2 == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        double x = a.X + t * dx - p.X, y = a.Y + t * dy - p.Y;
        return Math.Sqrt(x * x + y * y);
    }

    /// <summary>The distance between the segments a→b and c→d (0 when they cross).</summary>
    static double SegmentSegment((double X, double Y) a, (double X, double Y) b, (double X, double Y) c, (double X, double Y) d)
    {
        if (Crosses(a, b, c, d)) return 0;
        return Math.Min(Math.Min(SegmentPoint(a, b, c), SegmentPoint(a, b, d)), Math.Min(SegmentPoint(c, d, a), SegmentPoint(c, d, b)));
    }

    static bool Crosses((double X, double Y) a, (double X, double Y) b, (double X, double Y) c, (double X, double Y) d)
    {
        static double Cross((double X, double Y) o, (double X, double Y) p, (double X, double Y) q) => (p.X - o.X) * (q.Y - o.Y) - (p.Y - o.Y) * (q.X - o.X);
        double d1 = Cross(c, d, a), d2 = Cross(c, d, b), d3 = Cross(a, b, c), d4 = Cross(a, b, d);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }
}
