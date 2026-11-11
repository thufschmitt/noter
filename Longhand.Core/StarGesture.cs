using System;
using System.Collections.Generic;
using System.Linq;

namespace Longhand.Services;

/// <summary>
/// Recognises a five-point star drawn in one stroke, on the device, with no model and no network: the gesture that asks Claude.
/// It is the "$1" unistroke recogniser (Wobbrock, Wilson and Li, 2007) against built-in stars that start at every tip and go
/// both ways, so where the star starts, which way it goes, its tilt and its proportions don't matter. Two checks a star must
/// pass as well keep look-alikes out: the stroke ends where it began, and it turns sharply three to five times inside (a
/// pentagon, a circle or a zigzag doesn't, and a seven-point star turns too little or too often).
/// </summary>
public static class StarGesture
{
    const int Points = 64;
    const double Square = 250;
    static readonly double HalfDiagonal = 0.5 * Math.Sqrt(2 * Square * Square);
    static readonly double AngleRange = Deg(45), AnglePrecision = Deg(2), Phi = 0.5 * (-1 + Math.Sqrt(5));

    /// <summary>How alike a stroke must be to the best-matching star (1 is identical).</summary>
    public const double Threshold = 0.775;
    /// <summary>The smallest star taken, in page pixels on its longer side: smaller ones are dots and letters.</summary>
    public const double MinSize = 24;

    static readonly List<(double X, double Y)[]> Templates = BuildTemplates();

    /// <summary>How star-like the stroke is, from 0 to 1; 0 when it fails a check a star must pass.</summary>
    public static double Score(IReadOnlyList<(double X, double Y)> stroke)
    {
        if (stroke.Count < 10) return 0;
        double minX = stroke.Min(p => p.X), maxX = stroke.Max(p => p.X), minY = stroke.Min(p => p.Y), maxY = stroke.Max(p => p.Y);
        double w = maxX - minX, h = maxY - minY, size = Math.Max(w, h);
        if (size < MinSize || Math.Min(w, h) < size * 0.4) return 0; // a dot, or a line
        // Closed: a star ends at the tip it started from.
        if (Distance(stroke[0], stroke[^1]) > 0.3 * size) return 0;

        var points = Resample(stroke, Points);
        int corners = SharpTurns(points);
        if (corners is < 3 or > 5) return 0;

        var candidate = Normalize(points);
        double best = Templates.Min(t => DistanceAtBestAngle(candidate, t));
        return Math.Max(0, 1 - best / HalfDiagonal);
    }

    public static bool IsStar(IReadOnlyList<(double X, double Y)> stroke) => Score(stroke) >= Threshold;

    // ---------- the templates ----------

    /// <summary>A pentagram's path through its tips (every second one), from each tip, both ways round.</summary>
    static List<(double X, double Y)[]> BuildTemplates()
    {
        var tips = Enumerable.Range(0, 5).Select(k => (X: Math.Cos(Deg(-90 + 72 * k)) * 100, Y: Math.Sin(Deg(-90 + 72 * k)) * 100)).ToArray();
        var templates = new List<(double X, double Y)[]>();
        foreach (int step in new[] { 2, 3 }) // 2 one way round, 3 the other
            for (int start = 0; start < 5; start++)
            {
                var path = new List<(double X, double Y)>();
                for (int i = 0; i <= 5; i++) path.Add(tips[(start + i * step) % 5]);
                templates.Add(Normalize(Resample(path, Points)));
            }
        return templates;
    }

    // ---------- $1 ----------

    static (double X, double Y)[] Normalize((double X, double Y)[] points)
    {
        var rotated = RotateBy(points, -IndicativeAngle(points));
        var scaled = ScaleToSquare(rotated);
        return TranslateToOrigin(scaled);
    }

    static (double X, double Y)[] Resample(IReadOnlyList<(double X, double Y)> input, int n)
    {
        var pts = input.ToList();
        double interval = PathLength(pts) / (n - 1), d = 0;
        var result = new List<(double X, double Y)> { pts[0] };
        for (int i = 1; i < pts.Count; i++)
        {
            double step = Distance(pts[i - 1], pts[i]);
            if (d + step >= interval && step > 0)
            {
                double t = (interval - d) / step;
                var q = (pts[i - 1].X + t * (pts[i].X - pts[i - 1].X), pts[i - 1].Y + t * (pts[i].Y - pts[i - 1].Y));
                result.Add(q);
                pts.Insert(i, q);
                d = 0;
            }
            else d += step;
        }
        while (result.Count < n) result.Add(pts[^1]);
        return result.Take(n).ToArray();
    }

    /// <summary>The tips: places where the stroke doubles back by more than 110 degrees (a star's turn by about 144), counted once each.</summary>
    static int SharpTurns((double X, double Y)[] p)
    {
        const int k = 3;
        int count = 0;
        bool inTurn = false;
        for (int i = k; i < p.Length - k; i++)
        {
            var a = (X: p[i].X - p[i - k].X, Y: p[i].Y - p[i - k].Y);
            var b = (X: p[i + k].X - p[i].X, Y: p[i + k].Y - p[i].Y);
            double la = Math.Sqrt(a.X * a.X + a.Y * a.Y), lb = Math.Sqrt(b.X * b.X + b.Y * b.Y);
            if (la == 0 || lb == 0) continue;
            double turn = Math.Acos(Math.Clamp((a.X * b.X + a.Y * b.Y) / (la * lb), -1, 1));
            bool sharp = turn > Deg(110);
            if (sharp && !inTurn) count++;
            inTurn = sharp;
        }
        return count;
    }

    static double IndicativeAngle((double X, double Y)[] p)
    {
        var c = Centroid(p);
        return Math.Atan2(c.Y - p[0].Y, c.X - p[0].X);
    }

    static (double X, double Y)[] RotateBy((double X, double Y)[] p, double angle)
    {
        var c = Centroid(p);
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        return p.Select(q => ((q.X - c.X) * cos - (q.Y - c.Y) * sin + c.X, (q.X - c.X) * sin + (q.Y - c.Y) * cos + c.Y)).ToArray();
    }

    static (double X, double Y)[] ScaleToSquare((double X, double Y)[] p)
    {
        double minX = p.Min(q => q.X), maxX = p.Max(q => q.X), minY = p.Min(q => q.Y), maxY = p.Max(q => q.Y);
        double w = Math.Max(maxX - minX, 1e-9), h = Math.Max(maxY - minY, 1e-9);
        return p.Select(q => (q.X * (Square / w), q.Y * (Square / h))).ToArray();
    }

    static (double X, double Y)[] TranslateToOrigin((double X, double Y)[] p)
    {
        var c = Centroid(p);
        return p.Select(q => (q.X - c.X, q.Y - c.Y)).ToArray();
    }

    static double DistanceAtBestAngle((double X, double Y)[] p, (double X, double Y)[] t)
    {
        double a = -AngleRange, b = AngleRange;
        double x1 = Phi * a + (1 - Phi) * b, f1 = DistanceAtAngle(p, t, x1);
        double x2 = (1 - Phi) * a + Phi * b, f2 = DistanceAtAngle(p, t, x2);
        while (Math.Abs(b - a) > AnglePrecision)
        {
            if (f1 < f2)
            {
                b = x2; x2 = x1; f2 = f1;
                x1 = Phi * a + (1 - Phi) * b; f1 = DistanceAtAngle(p, t, x1);
            }
            else
            {
                a = x1; x1 = x2; f1 = f2;
                x2 = (1 - Phi) * a + Phi * b; f2 = DistanceAtAngle(p, t, x2);
            }
        }
        return Math.Min(f1, f2);
    }

    static double DistanceAtAngle((double X, double Y)[] p, (double X, double Y)[] t, double angle)
    {
        var r = RotateBy(p, angle);
        double d = 0;
        for (int i = 0; i < r.Length; i++) d += Distance(r[i], t[i]);
        return d / r.Length;
    }

    static (double X, double Y) Centroid((double X, double Y)[] p) => (p.Average(q => q.X), p.Average(q => q.Y));

    static double PathLength(IReadOnlyList<(double X, double Y)> p)
    {
        double d = 0;
        for (int i = 1; i < p.Count; i++) d += Distance(p[i - 1], p[i]);
        return d;
    }

    static double Distance((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    static double Deg(double degrees) => degrees * Math.PI / 180;
}
