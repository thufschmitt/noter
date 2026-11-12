using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Longhand.Services;

/// <summary>Checks for editable layouts on the page: what an edit does, when it is told to the model, replacing a layout in place, undo and saving.</summary>
static class EditChecks
{
    static string Board(string todo, string doing, string id = "board") =>
        $"<layout id=\"{id}\" title=\"Week\"><row><col id=\"todo\" drop title=\"To do\">{todo}</col><col id=\"doing\" drop title=\"Doing\">{doing}</col></row></layout>";
    const string Anna = "<box id=\"c1\" edit>Call Anna</box>";
    const string Gym = "<box id=\"c2\" edit>Gym</box>";

    static List<(double X, double Y)> Star(double cx, double cy, double r)
    {
        var tips = Enumerable.Range(0, 5).Select(k => (X: cx + r * Math.Cos((-90 + 72 * k) * Math.PI / 180), Y: cy + r * Math.Sin((-90 + 72 * k) * Math.PI / 180))).ToArray();
        var corners = Enumerable.Range(0, 6).Select(i => tips[i * 2 % 5]).ToList();
        var points = new List<(double X, double Y)>();
        for (int i = 0; i < corners.Count - 1; i++)
            for (int k = 0; k < 10; k++)
                points.Add((corners[i].X + (corners[i + 1].X - corners[i].X) * k / 10, corners[i].Y + (corners[i + 1].Y - corners[i].Y) * k / 10));
        points.Add(corners[^1]);
        return points;
    }

    static async Task<bool> Until(Func<bool> condition, int ms = 4000)
    {
        for (int waited = 0; waited < ms && !condition(); waited += 20) await Task.Delay(20);
        return condition();
    }

    public static async Task RunAsync(Action<string, bool> check)
    {
        await using var api = new FakeCodex { AskForTool = false, FinalText = "HEARD: show my board\n---\nHere it is.\n" + Board(Anna + Gym, "") };
        var view = new FakeView();
        var data = new FakeData(view, api);
        var session = new PageSession(view, data);
        await session.StartAsync();
        int Asked() => api.Requests.Count(r => r.Path.EndsWith("/responses"));
        string LastBody() => System.Text.RegularExpressions.Regex.Unescape(api.Requests.Last(r => r.Path.EndsWith("/responses")).Body);

        // ----- the model writes a board -----
        var ink = view.Draw(160, 200);
        session.StrokesCollected(new[] { ink });
        await session.AskAsync();
        var note = session.Notes.Single(n => !n.IsHint);
        var board = note.Drawings.Single();
        check("the model's layout is on the page, editable, in its canonical form", board.Editable && Blocks.FigureId(board.Markup) == "board" && note.Text == "Here it is." && board.Markup.Contains("<col id=\"doing\" title=\"Doing\" drop/>") && board.Alt == "Week");
        check("the model was told the user can edit, and how", LastBody().Contains("Layouts can be interactive") && LastBody().Contains("edit on a part"));
        check("the history shows the layout as it is", session.History().Last().Reply.Contains("<box id=\"c1\" edit>Call Anna</box>"));
        check("nothing has been edited, so there is nothing more to ask", !session.HasEdits && !view.State.CanAsk);

        // ----- the user moves a card: local, and told only when asked -----
        int asked = Asked();
        view.Changed.Clear();
        bool ok = session.EditDrawing(note, board, new[] { new DrawingChange("c1", "move", "doing", 0) });
        check("the move is made in the layout, and the screen is sent it back", ok && Layout.Find(Layout.Parse(board.Markup)!, "c1")!.Parent!.Id == "doing" && view.Changed.Contains(board));
        check("it makes the page askable, and nothing is sent", session.HasEdits && view.State.CanAsk && Asked() == asked);

        // ----- an edit that isn't allowed leaves the layout alone -----
        view.Changed.Clear();
        string before = board.Markup;
        bool refused = session.EditDrawing(note, board, new[] { new DrawingChange("todo", "move", "doing") });
        check("an edit of something that isn't editable is refused, and the screen is told to show the layout as it is", !refused && board.Markup == before && view.Changed.Contains(board));
        check("a drop where nothing accepts it is refused too", !session.EditDrawing(note, board, new[] { new DrawingChange("c2", "move", "board") }) && board.Markup == before);

        // ----- asking with only an edit -----
        api.FinalText = "HEARD: [moved Call Anna to Doing]\n---\nI moved it to Doing.\n" + Board(Gym, Anna.Replace("Call Anna", "Call Anna (done)"));
        await session.AskAsync();
        var body = LastBody();
        check("Ask works with no ink: the edit is what is sent", Asked() == asked + 1 && !body.Contains("input_image") && body.Contains("I changed your layouts by hand since your last reply"));
        check("it lists the move by id, with where the part was and where it is", body.Contains("Layout \"board\":") && body.Contains("- c1 \"Call Anna\": moved from #todo (position 1) to #doing (position 1 of 1)"));
        check("and the layout in the history already has it", body.Contains("<col id=\"doing\" title=\"Doing\" drop>\n") && !body.Contains("Its source now"));
        check("the edit is told once", !session.HasEdits && board.Edits.Count == 0 && !view.State.CanAsk);
        var second = session.Notes.Where(n => !n.IsHint).OrderBy(n => n.Y).Last();
        check("a layout with the same id replaces the old one where it is", second != note && second.Drawings.Count == 0 && second.Text == "I moved it to Doing." && note.Drawings.Single() == board && board.Markup.Contains("Call Anna (done)") && view.Changed.Contains(board));
        check("the exchange is in the history, heard as the model said", session.History().Last().Heard == "[moved Call Anna to Doing]");

        // ----- undoing the replacement, then the edit -----
        session.Undo();
        check("Ctrl+Z brings back the layout as the user left it", board.Markup.Contains("Call Anna<") && !board.Markup.Contains("(done)") && Layout.Find(Layout.Parse(board.Markup)!, "c1")!.Parent!.Id == "doing" && board.Edits.Count == 0 && !session.HasEdits);
        session.Undo();
        check("and again takes the move back", Layout.Find(Layout.Parse(board.Markup)!, "c1")!.Parent!.Id == "todo" && !session.HasEdits && !view.State.CanAsk);

        // ----- moving away and back is nothing -----
        session.EditDrawing(note, board, new[] { new DrawingChange("c1", "move", "doing", 0) });
        session.EditDrawing(note, board, new[] { new DrawingChange("c1", "move", "todo", 0) });
        check("a part moved away and back changes nothing to tell", !session.HasEdits && !view.State.CanAsk);

        // ----- turning, deleting, and the star -----
        session.EditDrawing(note, board, new[] { new DrawingChange("c1", "rotate", Degrees: 15) });
        check("a turn is an edit too", session.HasEdits && board.Markup.Contains("rot=\"15\""));
        int asked2 = Asked();
        api.FinalText = "HEARD: [turned the card]\n---\nOK.";
        var star = view.DrawPoints(Star(300, 3000, 25));
        session.StrokesCollected(new[] { star });
        check("a star asks about edits alone", await Until(() => Asked() == asked2 + 1 && !session.Busy) && !view.Ink.Any(s => s.Id == star));
        check("and the turn was described", LastBody().Contains("c1 \"Call Anna\": turned 15° clockwise"));

        // ----- a failed answer gives the edits back -----
        session.EditDrawing(note, board, new[] { new DrawingChange("c1", "move", "doing", 0) });
        api.FailStatus = 401;
        await session.AskAsync();
        api.FailStatus = 0;
        check("when no answer comes, the edits are still waiting to be told", session.HasEdits && view.State.CanAsk && !view.State.Busy);

        // ----- deleting, and what is saved -----
        session.EditDrawing(note, board, new[] { new DrawingChange("c2", "delete") });
        check("deleting takes the part out of the layout", !board.Markup.Contains("Gym") && board.Edits["c2"].Deleted && board.Edits["c2"].Label == "Gym");
        await session.SaveAsync();
        var id = session.CurrentId;
        check("the edits are saved with the page, and the layout with them made", data.Pages[id].Notes.Any(n => n.Edits is { Count: 2 } e && e.Any(x => x.Id == "c2" && x.Deleted && x.Parent == "todo" && x.Position == 1) && e.Any(x => x.Id == "c1" && x.Parent == "todo")) && data.Pages[id].Notes.Any(n => n.Drawings is { } d && d.Any(s => !s.Contains("Gym"))));
        await session.NewConversationAsync();
        await session.OpenConversationAsync(id);
        var back = session.Notes.First(n => n.Drawings.Count > 0).Drawings[0];
        check("a reopened page still has them to tell", session.HasEdits && back.Edits["c2"].Deleted && back.Edits["c1"].Parent == "todo" && back.Edits["c1"].Position == 1);
        api.FinalText = "HEARD: [edited the board]\n---\nGone.";
        await session.AskAsync();
        check("and tells them", LastBody().Contains("- c2 \"Gym\": deleted (it was in #todo (position 1))") && LastBody().Contains("- c1 \"Call Anna\": moved from #todo (position 1) to #doing (position 1 of 1)") && !session.HasEdits);

        // ----- a picture can't be edited -----
        var pictureFirst = session.Notes.Count;
        api.FinalText = "HEARD: a house\n---\nA house.\n<svg viewBox=\"0 0 4 3\"><title>House</title><rect id=\"r\" data-edit width=\"2\" height=\"2\"/></svg>";
        var more = view.Draw(5000, 5040);
        session.StrokesCollected(new[] { more });
        await session.AskAsync();
        var picture = session.Notes.OrderBy(n => n.Y).Last().Drawings.Single();
        check("a picture is shown, and has nothing the user can edit", !picture.Editable && !picture.Markup.Contains("data-edit") && !session.EditDrawing(session.Notes.OrderBy(n => n.Y).Last(), picture, new[] { new DrawingChange("r", "delete") }));
        check("the history shows it as a stand-in", session.History().Last().Reply.Contains("[drawing: House]"));

        // ----- a device that can't edit says so -----
        view.CanEdit = false;
        var again = view.Draw(6000, 6040);
        session.StrokesCollected(new[] { again });
        await session.AskAsync();
        check("the model is told not to offer editing when the screen can't", LastBody().Contains("cannot select, move, turn or delete") && !LastBody().Contains("Layouts can be interactive"));
    }
}
