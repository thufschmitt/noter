using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Longhand.Services;

/// <summary>Checks for editable drawings on the page: what an edit does, when it is told to the model, replacing a drawing in place, undo and saving.</summary>
static class EditChecks
{
    const string Cards = "<g id=\"card-1\" data-edit><rect x=\"{X}\" y=\"20\" width=\"100\" height=\"40\"/><text x=\"{X}\" y=\"45\">Call Anna</text></g>";
    static string Board(int cardX, string rootId = "board") =>
        $"<svg id=\"{rootId}\" viewBox=\"0 0 400 200\"><title>Board</title><rect id=\"col-todo\" width=\"200\" height=\"200\" fill=\"none\" stroke=\"currentColor\"/>"
        + "<rect id=\"col-doing\" x=\"200\" width=\"200\" height=\"200\" fill=\"none\" stroke=\"currentColor\"/>" + Cards.Replace("{X}", cardX.ToString()) + "</svg>";

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
        await using var api = new FakeCodex { AskForTool = false, FinalText = "HEARD: show my board\n---\nHere it is.\n" + Board(20) };
        var view = new FakeView();
        var where = new Dictionary<string, Box> { ["col-todo"] = new(0, 0, 200, 200), ["col-doing"] = new(200, 0, 200, 200), ["card-1"] = new(20, 20, 100, 40) };
        view.Boxes = _ => new Dictionary<string, Box>(where);
        var data = new FakeData(view, api);
        var session = new PageSession(view, data);
        await session.StartAsync();
        int Asked() => api.Requests.Count(r => r.Path.EndsWith("/responses"));
        string LastBody() => System.Text.RegularExpressions.Regex.Unescape(api.Requests.Last(r => r.Path.EndsWith("/responses")).Body);

        // ----- the model draws a board -----
        var ink = view.Draw(160, 200);
        session.StrokesCollected(new[] { ink });
        await session.AskAsync();
        var note = session.Notes.Single(n => !n.IsHint);
        var board = note.Drawings.Single();
        check("the model's drawing is on the page, editable, and its parts were measured", board.Editable && Svg.FigureId(board.Markup) == "board" && board.Boxes.Count == 3 && note.Text == "Here it is.");
        check("the model was told the user can edit, and how", LastBody().Contains("Your drawings can be interactive") && LastBody().Contains("data-edit"));
        check("the history shows the drawing by id", session.History().Last().Reply.Contains("[drawing: id \"board\", Board, editable]"));
        check("nothing has been edited, so there is nothing more to ask", !session.HasEdits && !view.State.CanAsk);

        // ----- the user moves the card: local, and told only when asked -----
        int asked = Asked();
        where["card-1"] = new Box(230, 20, 100, 40);
        bool ok = session.EditDrawing(note, board, new[] { new DrawingChange("card-1", "move", "translate(210 0)") }, where);
        check("the edit is written into the drawing", ok && board.Markup.Contains("transform=\"translate(210 0)\"") && board.Boxes["card-1"].X == 230);
        check("it makes the page askable, and nothing is sent", session.HasEdits && view.State.CanAsk && Asked() == asked);

        // ----- an edit that isn't allowed leaves the drawing alone -----
        view.Changed.Clear();
        string before = board.Markup;
        bool refused = session.EditDrawing(note, board, new[] { new DrawingChange("col-todo", "move", "translate(5 5)") }, where);
        check("an edit of something that isn't editable is refused, and the screen is told to show the drawing as it is", !refused && board.Markup == before && view.Changed.Contains(board));

        // ----- asking with only an edit -----
        api.FinalText = "HEARD: [moved Call Anna to Doing]\n---\nI moved it to Doing.\n" + Board(230);
        await session.AskAsync();
        var body = LastBody();
        check("Ask works with no ink: the edit is what is sent", Asked() == asked + 1 && !body.Contains("input_image") && body.Contains("I changed your drawings by hand since your last reply"));
        check("it lists the move, with where it landed", body.Contains("Drawing \"board\":") && body.Contains("card-1: moved by (+210, +0), from x 20, y 20 to x 230, y 20") && body.Contains("its centre is now inside #col-doing"));
        check("and carries the drawing's source as it is now", body.Contains("Its source now:") && body.Contains("translate(210 0)"));
        check("the edit is told once", !session.HasEdits && board.Edits.Count == 0 && !view.State.CanAsk);
        var second = session.Notes.Where(n => !n.IsHint).OrderBy(n => n.Y).Last();
        check("a drawing with the same id replaces the old one where it is", second != note && second.Drawings.Count == 0 && second.Text == "I moved it to Doing." && note.Drawings.Single() == board && board.Markup.Contains("x=\"230\"") && !board.Markup.Contains("translate(210 0)") && view.Changed.Contains(board));
        check("the exchange is in the history, heard as the model said", session.History().Last().Heard == "[moved Call Anna to Doing]");

        // ----- undoing the replacement, then the edit -----
        where["card-1"] = new Box(20, 20, 100, 40); // where the screen would find it once both are undone
        session.Undo();
        check("Ctrl+Z brings back the drawing as the user left it", board.Markup.Contains("translate(210 0)") && board.Edits.Count == 0 && !session.HasEdits);
        session.Undo();
        await Task.Delay(100); // the screen measures the drawing again after an undo
        check("and again takes the move back", !board.Markup.Contains("transform=") && !session.HasEdits && !view.State.CanAsk && board.Boxes["card-1"].X == 20);

        // ----- moving away and back is nothing -----
        where["card-1"] = new Box(230, 20, 100, 40);
        session.EditDrawing(note, board, new[] { new DrawingChange("card-1", "move", "translate(210 0)") }, where);
        where["card-1"] = new Box(20, 20, 100, 40);
        session.EditDrawing(note, board, new[] { new DrawingChange("card-1", "move", "translate(0 0)") }, where);
        check("a part moved away and back changes nothing to tell", !session.HasEdits && !view.State.CanAsk);

        // ----- turning, deleting, and the star -----
        where["card-1"] = new Box(10, 10, 120, 60);
        session.EditDrawing(note, board, new[] { new DrawingChange("card-1", "rotate", "matrix(0.9659 0.2588 -0.2588 0.9659 5 -3)", 15) }, where);
        check("a turn is an edit too", session.HasEdits && board.Edits["card-1"].Degrees == 15);
        int asked2 = Asked();
        api.FinalText = "HEARD: [turned the card]\n---\nOK.";
        var star = view.DrawPoints(Star(300, 3000, 25));
        session.StrokesCollected(new[] { star });
        check("a star asks about edits alone", await Until(() => Asked() == asked2 + 1 && !session.Busy) && !view.Ink.Any(s => s.Id == star));
        check("and the turn was described", LastBody().Contains("card-1: turned 15° clockwise"));

        // ----- the history stops carrying the source -----
        check("later turns show the drawing by id and title only", !session.History().Any(t => t.Reply.Contains("<svg")));

        // ----- a failed answer gives the edits back -----
        where["card-1"] = new Box(230, 20, 100, 40);
        session.EditDrawing(note, board, new[] { new DrawingChange("card-1", "move", "translate(220 10)") }, where);
        api.FailStatus = 401;
        await session.AskAsync();
        api.FailStatus = 0;
        check("when no answer comes, the edits are still waiting to be told", session.HasEdits && view.State.CanAsk && !view.State.Busy);

        // ----- deleting, and what is saved -----
        session.EditDrawing(note, board, new[] { new DrawingChange("card-1", "delete") }, new Dictionary<string, Box> { ["col-todo"] = new(0, 0, 200, 200), ["col-doing"] = new(200, 0, 200, 200) });
        check("deleting takes the part out of the drawing", !board.Markup.Contains("Call Anna") && board.Edits["card-1"].Deleted && Svg.EditableIds(board.Markup).Count == 0);
        await session.SaveAsync();
        var id = session.CurrentId;
        check("the edits are saved with the page", data.Pages[id].Notes.Any(n => n.Edits is { Count: 1 } e && e[0].Id == "card-1" && e[0].Deleted) && data.Pages[id].Notes.Any(n => n.Drawings is { } d && d.Any(s => !s.Contains("Call Anna"))));
        await session.NewConversationAsync();
        await session.OpenConversationAsync(id);
        var back = session.Notes.First(n => n.Drawings.Count > 0).Drawings[0];
        check("a reopened page still has them to tell", session.HasEdits && back.Edits["card-1"].Deleted && back.Edits["card-1"].First is { } was && was.X == 10);
        api.FinalText = "HEARD: [deleted the card]\n---\nGone.";
        await session.AskAsync();
        check("and tells them", LastBody().Contains("card-1: deleted (it was at x 10, y 10, 120×60)") && !session.HasEdits);

        // ----- a device that can't edit says so -----
        view.CanEdit = false;
        var more = view.Draw(4000, 4040);
        session.StrokesCollected(new[] { more });
        await session.AskAsync();
        check("the model is told not to offer editing when the screen can't", LastBody().Contains("cannot move, turn or delete") && !LastBody().Contains("Your drawings can be interactive"));
    }
}
