using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Longhand.Services;

/// <summary>Checks for the model erasing the user's marks over its blocks: reading the request, which ink it takes, and undo.</summary>
static class EraseChecks
{
    const string Board = "<layout id=\"board\" title=\"Week\"><row><col id=\"todo\" drop title=\"To do\"><box id=\"c1\" edit>Call Anna</box></col></row></layout>";

    public static async Task RunAsync(Action<string, bool> check)
    {
        // ----- reading the request -----
        var tag = Blocks.Extract("Done. <erase over=\"board\"/> Bye.", finished: true);
        check("an erase tag is taken out of the words and read", tag.Text == "Done.  Bye." && tag.Erases is [{ Over: "board", X: 0, Y: 0, W: 1, H: 1 }]);
        var part = Blocks.Extract("Ok <erase over='map' x=\"0.5\" y=\"0.25\" w=\"0.5\" h=\"0.5\"></erase> now <erase over=\"b\" x=\"0.8\" w=\"0.9\"/>", finished: true);
        check("a region is read as fractions; what runs past the block is cut, what is missing is the rest",
            part.Erases is [{ Over: "map", X: 0.5, Y: 0.25, W: 0.5, H: 0.5 }, { Over: "b", X: 0.8, Y: 0, W: var w2, H: 1 }] && Math.Abs(w2 - 0.2) < 1e-9);
        check("a request with no block, or no area, is ignored", Blocks.Extract("<erase/> <erase over=\"\"/> <erase over=\"a\" w=\"0\"/> <erase over=\"a\" x=\"2\"/>", finished: true).Erases is { Count: 0 });
        check("only a few requests are kept", Blocks.Extract(string.Concat(Enumerable.Range(0, 12).Select(i => $"<erase over=\"b{i}\"/>")), finished: true).Erases!.Count == InkErase.MaxRequests);
        check("a tag that has begun but not ended is never shown while streaming", Blocks.Extract("Done <erase over=\"bo", finished: false).Text == "Done" && Blocks.Extract("Done <er", finished: false).Text == "Done" && Blocks.Extract("Done <e", finished: false).Text == "Done" && Blocks.Extract("a < b", finished: false).Text == "a < b");
        check("and a whole one is read as soon as it arrives", Blocks.Extract("Done <erase over=\"board\"/>", finished: false) is { Text: "Done", Erases: [{ Over: "board" }] });
        check("a reply's blocks and its erase tags are both found", Blocks.Extract("Hi " + Board + " <erase over=\"old\"/>", finished: true) is { Blocks.Count: 1, Erases: [{ Over: "old" }] });

        // ----- which ink -----
        var block = new HitRect(76, 300, 836, 600);
        var lower = InkErase.Region(block, new EraseRequest("b", 0, 0.5, 1, 0.5));
        check("a region is a part of the block, from its top left", lower == new HitRect(76, 450, 836, 600) && InkErase.Region(block, new EraseRequest("b", 0.5, 0, 0.5, 1)) == new HitRect(456, 300, 836, 600));
        check("ink inside goes; ink beside, above, or only passing through doesn't", InkErase.MostlyIn(new[] { (200.0, 350.0), (210.0, 360.0), (220.0, 370.0) }, block)
            && !InkErase.MostlyIn(new[] { (20.0, 350.0), (30.0, 360.0) }, block) && !InkErase.MostlyIn(new[] { (200.0, 100.0), (210.0, 110.0) }, block)
            && !InkErase.MostlyIn(new[] { (200.0, 250.0), (200.0, 350.0), (200.0, 450.0), (200.0, 650.0), (200.0, 700.0) }, block) // a line that runs through
            && !InkErase.MostlyIn(Array.Empty<(double, double)>(), block));
        check("a stroke a few pixels off the edge still counts (a block's edge is not exact)", InkErase.MostlyIn(new[] { (80.0, 303.0), (82.0, 297.0), (90.0, 296.0) }, block));

        // ----- on the page -----
        await using var api = new FakeCodex { AskForTool = false, FinalText = "HEARD: my board\n---\nHere it is.\n" + Board };
        var view = new FakeView();
        var data = new FakeData(view, api);
        var session = new PageSession(view, data);
        await session.StartAsync();
        string LastBody() => System.Text.RegularExpressions.Regex.Unescape(api.Requests.Last(r => r.Path.EndsWith("/responses")).Body);

        session.StrokesCollected(new[] { view.Draw(160, 200) });
        await session.AskAsync();
        check("the model is told how to erase, and what to erase", LastBody().Contains("<erase over=\\\"board\\\"/>".Replace("\\\"", "\"")) && LastBody().Contains("never my other writing") && LastBody().Contains("I can undo it"));
        var note = session.Notes.Single(n => !n.IsHint);
        double top = note.Y + note.Lines * PageLayout.LineHeight;
        var (_, h) = Blocks.Fit(note.Drawings.Single().Aspect, 760);

        var writing = view.DrawPoints(new (double, double)[] { (120, 160), (130, 180), (140, 200) });
        var circle = view.DrawPoints(Enumerable.Range(0, 20).Select(k => (200 + 40 * Math.Cos(k * 0.33), top + 60 + 40 * Math.Sin(k * 0.33))).ToList());
        var tick = view.DrawPoints(new (double, double)[] { (500, top + h - 60), (510, top + h - 40), (540, top + h - 80) });
        var below = view.DrawPoints(new (double, double)[] { (200, top + h + 200), (220, top + h + 210), (240, top + h + 220) });
        session.StrokesCollected(new[] { writing, circle, tick, below });
        api.FinalText = "HEARD: done\n---\nI erased the circle on the board.\n<erase over=\"board\" x=\"0\" y=\"0\" w=\"0.5\" h=\"0.5\"/>";
        view.Toasts.Clear();
        await session.AskAsync();
        var ids = view.Strokes().Select(s => s.Id).ToHashSet();
        var second = session.Notes.Last(n => !n.IsHint);
        check("only the ink mostly inside the region goes: the circle, not the writing, the tick or the ink below the block", !ids.Contains(circle) && ids.Contains(writing) && ids.Contains(tick) && ids.Contains(below));
        check("the words are kept without the tag, and the user is told it can be undone", second.Text == "I erased the circle on the board." && view.Toasts.Any(t => t.Contains("Erased 1 of your marks") && t.Contains("Undo")));
        session.Undo();
        check("one undo brings the erased ink back", view.Strokes().Select(s => s.Id).ToHashSet().Contains(circle));

        api.FinalText = "HEARD: done\n---\n<erase over=\"board\"/><erase over=\"nothing-here\"/>";
        view.Toasts.Clear();
        session.StrokesCollected(new[] { view.Draw(top + h + 300, top + h + 340) });
        await session.AskAsync();
        var left = view.Strokes().Select(s => s.Id).ToHashSet();
        check("the whole block takes the circle and the tick; an id that isn't on the page takes nothing", !left.Contains(circle) && !left.Contains(tick) && left.Contains(writing) && left.Contains(below));
        check("a reply that only erases still says what it did", session.Notes.Last(n => !n.IsHint).Text == "Erased the marks above.");
        session.Undo();
        var back = view.Strokes().Select(s => s.Id).ToHashSet();
        check("and one undo brings back everything it erased", back.Contains(circle) && back.Contains(tick));

        api.FinalText = "HEARD: done\n---\nI can't.\n<erase over=\"board\"/>";
        api.Incomplete = "max_output_tokens";
        session.StrokesCollected(new[] { view.Draw(top + h + 400, top + h + 440) });
        await session.AskAsync();
        api.Incomplete = null;
        check("an answer that was cut short erases nothing", view.Strokes().Select(s => s.Id).ToHashSet().IsSupersetOf(new[] { circle, tick }));
    }
}
