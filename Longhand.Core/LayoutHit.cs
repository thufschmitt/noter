using System;
using System.Collections.Generic;
using System.Linq;

namespace Longhand.Services;

/// <summary>A rectangle in whatever coordinates the caller works in (the page's).</summary>
public readonly record struct HitRect(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
    public double Area => Math.Max(0, Width) * Math.Max(0, Height);
    public double CenterX => (Left + Right) / 2;
    public double CenterY => (Top + Bottom) / 2;
    public bool Contains(double x, double y, double tolerance = 0) => x >= Left - tolerance && x <= Right + tolerance && y >= Top - tolerance && y <= Bottom + tolerance;
}

/// <summary>
/// What turns a pointer into a change of a layout, as plain arithmetic on rectangles: which container a drop lands in and at which
/// place, which time it lands on a timeline, which parts a lasso takes. The Windows page uses it over real controls; the web page
/// has the same rules in <c>app.js</c> (the numbers here are what its checks expect).
/// </summary>
public static class LayoutHit
{
    /// <summary>A time brought to the nearest step of <see cref="Layout.SnapMinutes"/>.</summary>
    public static int Snap(double minutes) => (int)Math.Round(minutes / Layout.SnapMinutes) * Layout.SnapMinutes;

    /// <summary>The time (in minutes since midnight, not snapped) at a height, on a grid that starts at <paramref name="gridTop"/> with <paramref name="fromMinutes"/> at its top.</summary>
    public static double MinutesAt(double y, double gridTop, int fromMinutes, double pxPerHour = Layout.PxPerHour) =>
        fromMinutes + (y - gridTop) / (pxPerHour / 60.0);

    /// <summary>Where an item would start if its top edge were at <paramref name="itemTop"/>: snapped, and kept inside the day for an item of this length.</summary>
    public static int StartFor(double itemTop, double gridTop, int fromMinutes, int lengthMinutes, double pxPerHour = Layout.PxPerHour)
    {
        int length = Math.Max(Layout.SnapMinutes, lengthMinutes);
        return Math.Max(0, Math.Min(Layout.DayMinutes - length, Snap(MinutesAt(itemTop, gridTop, fromMinutes, pxPerHour))));
    }

    /// <summary>Where an item that starts at <paramref name="start"/> would end if its bottom edge followed the pointer to <paramref name="y"/>: snapped, at least a step after the start, and not past the end of the day.</summary>
    public static int EndFor(double y, double gridTop, int fromMinutes, int start, double pxPerHour = Layout.PxPerHour) =>
        Math.Min(Layout.DayMinutes, Math.Max(start + Layout.SnapMinutes, Snap(MinutesAt(y, gridTop, fromMinutes, pxPerHour))));

    /// <summary>The index of the smallest rectangle that holds the point (the most specific container); -1 when none does.</summary>
    public static int Smallest(IReadOnlyList<HitRect> candidates, double x, double y, double tolerance = 0)
    {
        int best = -1;
        for (int i = 0; i < candidates.Count; i++)
            if (candidates[i].Contains(x, y, tolerance) && (best < 0 || candidates[i].Area < candidates[best].Area)) best = i;
        return best;
    }

    /// <summary>
    /// The place a part dropped at (<paramref name="x"/>, <paramref name="y"/>) takes among the parts of a container: before the first whose
    /// middle (left to right in a row, top to bottom in a column) is beyond the point, so 0 is first and the count is last.
    /// </summary>
    public static int IndexAmong(IReadOnlyList<HitRect> parts, bool row, double x, double y)
    {
        for (int i = 0; i < parts.Count; i++)
            if ((row ? x : y) < (row ? parts[i].CenterX : parts[i].CenterY)) return i;
        return parts.Count;
    }

    public static bool InsidePolygon(double x, double y, IReadOnlyList<(double X, double Y)> polygon)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var (xi, yi) = polygon[i];
            var (xj, yj) = polygon[j];
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }

    /// <summary>A part is taken by a lasso when most of it is inside the loop: of a grid of points over it, at least <paramref name="need"/> of <c>grid × grid</c>.</summary>
    public static bool MostlyInside(HitRect part, IReadOnlyList<(double X, double Y)> polygon, int grid = 5, int need = 13)
    {
        if (polygon.Count < 3) return false;
        int inside = 0;
        for (int i = 0; i < grid; i++)
            for (int j = 0; j < grid; j++)
                if (InsidePolygon(part.Left + part.Width * (i + 0.5) / grid, part.Top + part.Height * (j + 0.5) / grid, polygon)) inside++;
        return inside >= need;
    }

    /// <summary>True when every point of a stroke stays within <paramref name="size"/> of its first: a tap, not a line.</summary>
    public static bool IsTap(IReadOnlyList<(double X, double Y)> points, double size = 5) =>
        points.Count > 0 && points.All(p => Math.Abs(p.X - points[0].X) < size && Math.Abs(p.Y - points[0].Y) < size);

    // ----- where a timeline puts things -----

    /// <summary>The line (in pixels from the top of the grid) at a time.</summary>
    public static double TimelineY(int minutes, int fromMinutes, double pxPerHour = Layout.PxPerHour) => (minutes - fromMinutes) * pxPerHour / 60.0;

    /// <summary>
    /// Where an item of a timeline goes in a grid <paramref name="width"/> wide: at its start, as tall as it is long, in its lane of the lanes
    /// it shares the width with (a pixel in from the left and three short of the lane's width, so neighbours don't touch).
    /// </summary>
    public static HitRect TimelineSlot(int start, int end, int lane, int lanes, double width, int fromMinutes, double pxPerHour = Layout.PxPerHour)
    {
        double top = TimelineY(start, fromMinutes, pxPerHour);
        double height = Math.Max(14, (end - start) * pxPerHour / 60.0 - 2);
        double laneWidth = width / Math.Max(1, lanes);
        double left = laneWidth * lane + 1;
        return new HitRect(left, top, left + Math.Max(0, laneWidth - 3), top + height);
    }
}
