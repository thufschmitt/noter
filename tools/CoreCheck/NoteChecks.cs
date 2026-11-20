using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Longhand.Services;

/// <summary>Checks for the lasso taking whole notes (the model's replies and typed notes): where a note is, moving it, erasing it and undo.</summary>
static class NoteChecks
{
    public static async Task RunAsync(Action<string, bool> check)
    {
        // ----- where a note is -----
        const double L = PageLayout.LineHeight, X = PageLayout.MarginX;
        check("a note of words is as wide as its longest line, down the lines it takes", LayoutHit.NoteBox(200, 2, 300, Array.Empty<HitRect>()) == new HitRect(X, 200, X + 300, 200 + 2 * L));
        check("its blocks are part of it", LayoutHit.NoteBox(200, 1, 100, new[] { new HitRect(X, 240, X + 500, 400) }) == new HitRect(X, 200, X + 500, 400));
        check("a note that is only a block is the block", LayoutHit.NoteBox(200, 0, 0, new[] { new HitRect(X, 200, X + 400, 320) }) == new HitRect(X, 200, X + 400, 320));
        var loop = new List<(double X, double Y)> { (X - 20, 180), (X + 340, 180), (X + 340, 300), (X - 20, 300) };
        check("a loop round a note's words takes it, one over half of it doesn't", LayoutHit.MostlyInside(LayoutHit.NoteBox(200, 2, 300, Array.Empty<HitRect>()), loop)
            && !LayoutHit.MostlyInside(LayoutHit.NoteBox(200, 2, 700, Array.Empty<HitRect>()), loop));
        var clipping = new List<(double X, double Y)> { (X - 20, 220), (X + 800, 220), (X + 800, 500), (X - 20, 500) };
        check("a loop that clips the bottom of a line of words, round a layout under it, doesn't take the words",
            !LayoutHit.MostlyInside(LayoutHit.NoteBox(200, 1, 300, Array.Empty<HitRect>()), clipping, need: LayoutHit.WordsNeed)
            && LayoutHit.MostlyInside(LayoutHit.NoteBox(200, 1, 300, Array.Empty<HitRect>()), loop, need: LayoutHit.WordsNeed));
        check("a drag moves by whole lines", LayoutHit.NoteShift(55, 400) == L && LayoutHit.NoteShift(-61, 400) == -2 * L && LayoutHit.NoteShift(15, 400) == 0);
        check("never above the first rule", LayoutHit.NoteShift(-1000, PageLayout.Top + 2 * L) == -2 * L && LayoutHit.NoteShift(-1000, PageLayout.Top) == 0);

        // ----- on the page -----
        await using var api = new FakeCodex { AskForTool = false };
        var view = new FakeView();
        var data = new FakeData(view, api);
        var session = new PageSession(view, data);
        await session.StartAsync();
        check("the hint can't be taken", !session.CanTakeNote(session.Notes.Single()));

        session.StrokesCollected(new[] { view.Draw(160, 200) });
        await session.AskAsync();
        var reply = session.Notes.Single(n => !n.IsHint);
        await session.TypeAsync("and the day after?");
        var typed = session.Notes.Single(n => n.IsTyped);
        var second = session.Notes.Where(n => !n.IsHint && !n.IsTyped).OrderBy(n => n.Y).Last();
        check("a reply and a typed note can be taken", session.CanTakeNote(reply) && session.CanTakeNote(typed));

        double wasY = typed.Y, replyY = reply.Y, secondY = second.Y;
        check("moving a typed note takes it down by whole lines, and nothing else moves", session.MoveNotes(new[] { typed }, 3 * L + 5) && typed.Y == wasY + 3 * L && reply.Y == replyY && second.Y == secondY);
        session.Undo();
        check("undo puts it back", typed.Y == wasY);
        check("a drag shorter than half a line moves nothing", !session.MoveNotes(new[] { typed }, L / 3) && typed.Y == wasY);
        check("several move together, kept below the first rule", session.MoveNotes(new[] { reply, second }, -10000) && reply.Y == PageLayout.Top && second.Y == secondY - (replyY - PageLayout.Top));
        check("the model reads the page as it now is, top to bottom", session.History().Count == 2);
        session.Undo();
        check("one undo puts them both back", reply.Y == replyY && second.Y == secondY);

        check("erasing a reply takes it off the page and out of what the model reads", session.EraseNotes(new[] { reply }) && !session.Notes.Contains(reply) && !view.Visible.Contains(reply)
            && session.History().Count == 1);
        await Task.Delay(1300);
        check("and out of what is saved", data.Pages.Values.Single().Notes.All(n => n.Text != reply.Text || n.Y != replyY));
        session.Undo();
        await Task.Delay(50);
        check("undo puts the reply back where it was, read again", session.Notes.Contains(reply) && view.Visible.Contains(reply) && reply.Y == replyY && session.History().Count == 2);
        check("erasing several at once is one undo", session.EraseNotes(new[] { typed, second }) && session.Notes.Count(n => !n.IsHint) == 1);
        session.Undo();
        await Task.Delay(50);
        check("and they all come back", session.Notes.Contains(typed) && session.Notes.Contains(second));

        // ----- while an answer is being written -----
        api.ReplyDelayMs = 400;
        session.StrokesCollected(new[] { view.Draw(second.Bottom + 40, second.Bottom + 80) });
        var asking = session.AskAsync();
        await Task.Delay(100);
        view.Toasts.Clear();
        check("nothing is moved or erased while an answer is coming", !session.MoveNotes(new[] { reply }, 2 * L) && reply.Y == replyY && !session.EraseNotes(new[] { reply }) && session.Notes.Contains(reply)
            && view.Toasts.Count == 2);
        var writing = session.Notes.Single(n => !n.Done);
        check("and the answer being written can't be taken", !session.CanTakeNote(writing));
        await asking;
        api.ReplyDelayMs = 0;
    }
}
