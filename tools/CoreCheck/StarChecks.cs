using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Longhand.Services;

/// <summary>Checks for the star that asks Claude: recognising it on the device, and what the page does with it.</summary>
static class StarChecks
{
    /// <summary>A five-point star drawn in one stroke through the given tips order, around (cx, cy), as a pen would: many points.</summary>
    static List<(double X, double Y)> Star(double cx, double cy, double r, int start = 0, int step = 2, double tilt = 0)
    {
        var tips = Enumerable.Range(0, 5).Select(k => (X: cx + r * Math.Cos((-90 + 72 * k + tilt) * Math.PI / 180), Y: cy + r * Math.Sin((-90 + 72 * k + tilt) * Math.PI / 180))).ToArray();
        return Through(Enumerable.Range(0, 6).Select(i => tips[(start + i * step) % 5]).ToList());
    }

    static List<(double X, double Y)> Through(List<(double X, double Y)> corners)
    {
        var points = new List<(double X, double Y)>();
        for (int i = 0; i < corners.Count - 1; i++)
            for (int k = 0; k < 10; k++)
                points.Add((corners[i].X + (corners[i + 1].X - corners[i].X) * k / 10, corners[i].Y + (corners[i + 1].Y - corners[i].Y) * k / 10));
        points.Add(corners[^1]);
        return points;
    }

    static List<(double X, double Y)> Polygon(double cx, double cy, double r, int sides) =>
        Through(Enumerable.Range(0, sides + 1).Select(i => (cx + r * Math.Cos(2 * Math.PI * i / sides), cy + r * Math.Sin(2 * Math.PI * i / sides))).ToList());

    public static async Task RunAsync(Action<string, bool> check)
    {
        // ----- recognising it -----
        check("a five-point star is recognised from any tip, either way round, tilted", new[] { 0, 1, 2, 3, 4 }.All(s => StarGesture.IsStar(Star(300, 300, 30, s, 2, tilt: s * 11)) && StarGesture.IsStar(Star(300, 300, 30, s, 3, tilt: -s * 9))));
        check("squashed, it is still a star", StarGesture.IsStar(Star(300, 300, 30).Select(p => (p.X, 300 + (p.Y - 300) * 0.7)).ToList()));
        check("circles, pentagons, triangles and squares aren't", !StarGesture.IsStar(Polygon(300, 300, 30, 40)) && !StarGesture.IsStar(Polygon(300, 300, 30, 5)) && !StarGesture.IsStar(Polygon(300, 300, 30, 3)) && !StarGesture.IsStar(Polygon(300, 300, 30, 4)));
        check("nor is a star left open, or one too small to be anything but a letter", !StarGesture.IsStar(Star(300, 300, 30).Take(45).ToList()) && !StarGesture.IsStar(Star(300, 300, 8)));
        check("nor a seven-point star", !StarGesture.IsStar(Through(Enumerable.Range(0, 8).Select(i => (300 + 30 * Math.Cos(2 * Math.PI * (i * 3 % 7) / 7), 300 + 30 * Math.Sin(2 * Math.PI * (i * 3 % 7) / 7))).ToList())));

        // ----- on the page -----
        await using var api = new FakeCodex { AskForTool = false };
        var view = new FakeView();
        var data = new FakeData(view, api);
        var session = new PageSession(view, data);
        await session.StartAsync();
        int Asked() => api.Requests.Count(r => r.Path.EndsWith("/responses"));
        static async Task<bool> Until(Func<bool> condition, int ms = 4000)
        {
            for (int waited = 0; waited < ms && !condition(); waited += 20) await Task.Delay(20);
            return condition();
        }

        var lone = view.DrawPoints(Star(400, 180, 25));
        session.StrokesCollected(new[] { lone });
        await Task.Delay(200);
        check("a star with nothing written to ask about is just ink", Asked() == 0 && view.Ink.Any(s => s.Id == lone) && session.PendingCount == 1);

        var word = view.Draw(250, 290);
        session.StrokesCollected(new[] { word });
        var touching = view.DrawPoints(Star(140, 270, 25));
        session.StrokesCollected(new[] { touching });
        await Task.Delay(200);
        check("a star drawn on the writing is part of it, not a request", Asked() == 0 && view.Ink.Any(s => s.Id == touching) && session.PendingCount == 3);

        var star = view.DrawPoints(Star(420, 270, 25));
        session.StrokesCollected(new[] { star });
        check("a star apart from the writing asks Claude about it", await Until(() => session.Notes.Any(n => !n.IsHint && n.Done) && !session.Busy) && Asked() == 1);
        check("and comes off the page, while the writing it asked about is answered", !view.Ink.Any(s => s.Id == star) && session.PendingCount == 0 && view.LastIds is { } sent && sent.Contains(word) && !sent.Contains(star));
        session.Undo();
        check("Ctrl+Z puts the star back", view.Ink.Any(s => s.Id == star));

        var reply = session.Notes.First(n => !n.IsHint && n.Done);
        var more = view.Draw(reply.Bottom + 40, reply.Bottom + 80);
        session.StrokesCollected(new[] { more });
        var onReply = view.DrawPoints(Star(500, reply.Y + 20, 18));
        session.StrokesCollected(new[] { onReply });
        await Task.Delay(200);
        check("a star on one of Claude's replies is ink (it marks the reply)", Asked() == 1 && view.Ink.Any(s => s.Id == onReply));
    }
}
