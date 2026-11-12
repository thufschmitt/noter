using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Longhand.Services;

/// <summary>Checks for the calendar, the tool loop and the drawings.</summary>
static class AgentChecks
{
    static readonly TimeZoneInfo Plus1 = TimeZoneInfo.CreateCustomTimeZone("test+1", TimeSpan.FromHours(1), "test+1", "test+1");

    const string Ics = """
        BEGIN:VCALENDAR
        VERSION:2.0
        BEGIN:VTIMEZONE
        TZID:Europe/Paris
        BEGIN:STANDARD
        DTSTART:19701025T030000
        END:STANDARD
        END:VTIMEZONE
        BEGIN:VEVENT
        UID:1
        DTSTART:20261111T080000Z
        DTEND:20261111T090000Z
        SUMMARY:Standup\, daily
        LOCATION:Room 2
        DESCRIPTION:Line one\nLine two that is
          folded here
        BEGIN:VALARM
        TRIGGER:-PT10M
        DESCRIPTION:Reminder
        END:VALARM
        END:VEVENT
        BEGIN:VEVENT
        UID:2
        DTSTART;VALUE=DATE:20261111
        DTEND;VALUE=DATE:20261113
        SUMMARY:Conference
        END:VEVENT
        BEGIN:VEVENT
        UID:3
        DTSTART:20261111T100000Z
        DURATION:PT90M
        SUMMARY:Dentist
        END:VEVENT
        BEGIN:VEVENT
        UID:4
        DTSTART:20261111T120000Z
        SUMMARY:Cancelled thing
        STATUS:CANCELLED
        END:VEVENT
        BEGIN:VEVENT
        UID:5
        DTSTART:20261111T133000
        SUMMARY:Floating lunch
        END:VEVENT
        END:VCALENDAR
        """;

    public static async Task RunAsync(Action<string, bool> check)
    {
        // ----- iCalendar -----
        var events = ICalendar.ParseEvents(Ics.Replace("\r\n", "\n").Replace("\n", "\r\n"), "Personal", Plus1);
        check("cancelled events are skipped", events.All(e => e.Title != "Cancelled thing") && events.Count == 4);
        var standup = events.First(e => e.Title.StartsWith("Standup"));
        check("escaped characters are read", standup.Title == "Standup, daily" && standup.Location == "Room 2");
        check("folded lines are joined and alarms ignored", standup.Description == "Line one\nLine two that isfolded here" || standup.Description == "Line one\nLine two that is folded here");
        check("UTC times are instants", standup.Start == new DateTimeOffset(2026, 11, 11, 8, 0, 0, TimeSpan.Zero) && standup.End == standup.Start.AddHours(1) && !standup.AllDay);
        var conference = events.First(e => e.Title == "Conference");
        check("all-day events are dates", conference.AllDay && conference.Start.Date == new DateTime(2026, 11, 11) && conference.End.Date == new DateTime(2026, 11, 13));
        check("a duration gives the end", events.First(e => e.Title == "Dentist").End == new DateTimeOffset(2026, 11, 11, 11, 30, 0, TimeSpan.Zero));
        check("floating times use the given zone", events.First(e => e.Title == "Floating lunch").Start == new DateTimeOffset(2026, 11, 11, 13, 30, 0, TimeSpan.FromHours(1)));

        // ----- the agenda text -----
        var from = new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.FromHours(1));
        var agenda = CalendarText.Agenda(events, from, from.AddDays(1), Plus1, null);
        check("agenda groups by day with all-day first", agenda.Contains("Wednesday 11 November 2026") && agenda.IndexOf("all day until Thu 12 Nov", StringComparison.Ordinal) is > 0 && agenda.IndexOf("all day until", StringComparison.Ordinal) < agenda.IndexOf("09:00–10:00", StringComparison.Ordinal));
        check("agenda shows local times, calendar and location", agenda.Contains("09:00–10:00 Standup, daily [Personal] @ Room 2") && agenda.Contains("11:00–12:30 Dentist"));
        check("agenda can be searched", !CalendarText.Agenda(events, from, from.AddDays(1), Plus1, "dentist").Contains("Standup") && CalendarText.Agenda(events, from, from.AddDays(1), Plus1, "dentist").Contains("Dentist"));
        check("an empty range says so", CalendarText.Agenda(new List<CalendarEvent>(), from, from.AddDays(1), Plus1, null).EndsWith("None found."));

        // ----- Nextcloud sign-in and calendars, against a fake server -----
        check("server addresses are tidied", Nextcloud.NormalizeServer("cloud.example.com/")!.ToString() == "https://cloud.example.com/" && Nextcloud.NormalizeServer("https://c.example/index.php")!.AbsolutePath == "/");
        check("plain http is refused except on this PC", Nextcloud.NormalizeServer("http://cloud.example.com") is null && Nextcloud.NormalizeServer("http://127.0.0.1:8080") is not null && Nextcloud.NormalizeServer("   ") is null);

        await using (var server = new FakeNextcloud())
        {
            var nc = new Nextcloud();
            var start = await nc.BeginLoginAsync(server.BaseUrl, default);
            check("login flow gives the page to open", start.LoginUrl.AbsolutePath == "/login/v2/flow/tok123" && start.PollToken == "tok123");
            var account = await nc.WaitForLoginAsync(start, TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(5), default);
            check("polling waits until approved, then returns the app password", account.User == FakeNextcloud.User && account.Password == FakeNextcloud.Password && account.Server == server.BaseUrl);

            var calendars = await nc.ListCalendarsAsync(account, default);
            check("only event calendars are listed, with which ones may be written", calendars.Select(c => c.Name).SequenceEqual(new[] { "Personal", "Team" }) && calendars[0].Writable && !calendars[1].Writable);

            server.Ics = Ics.Replace("\r\n", "\n").Replace("\n", "\r\n");
            var tool = new CalendarTool(nc, account, Plus1, () => from);
            var text = await tool.RunAsync(Args("""{"start":"2026-11-11","end":"2026-11-11"}"""), default);
            check("the tool returns the day's agenda", text.Contains("Standup, daily") && text.Contains("Conference") && text.Contains("(test+1)"));
            var report = server.Requests.Last(q => q.Method == "REPORT");
            check("the query asks the server to expand repeats for exactly that day", report.Body.Contains("expand start=\"20261110T230000Z\" end=\"20261111T230000Z\""));
            check("every request carried the app password", server.Requests.Where(q => q.Method is "PROPFIND" or "REPORT").All(q => q.Headers["Authorization"].StartsWith("Basic ")));

            var failed = false;
            try { await tool.RunAsync(Args("""{"start":"someday"}"""), default); } catch (InvalidOperationException) { failed = true; }
            check("a bad date is reported to Claude", failed);
            failed = false;
            try { await nc.ListCalendarsAsync(account with { Password = "wrong" }, default); } catch (InvalidOperationException ex) { failed = ex.Message.Contains("again"); }
            check("a rejected login says to connect again", failed);

            await nc.RevokeAsync(account, default);
            check("disconnecting asks the server to forget the app password", server.Requests.Any(q => q.Method == "DELETE" && q.Headers.ContainsKey("OCS-APIRequest")));

            // ----- the whole tool loop through the service, against a fake of the Codex backend -----
            await using var api = new FakeCodex { FinalText = "HEARD: what is on tomorrow\n---\nYou have a standup at 9.\n<svg viewBox=\"0 0 100 50\"><title>A clock</title><circle cx=\"25\" cy=\"25\" r=\"20\"/></svg>" };
            var statuses = new List<string>();
            var progress = new List<ReplyParser.Parsed>();
            var service = new CodexService(api.SignIn, model: "gpt-6.1-sol", agentTools: new IAgentTool[] { tool }, baseUrl: api.BaseUrl);
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
            var result = await service.AskAsync(png, Array.Empty<Turn>(), progress.Add, default, statuses.Add);

            var calls = api.Requests.Where(q => q.Path.EndsWith("/responses")).ToList();
            check("the model was called twice: once for the tool call, once with its result", calls.Count == 2);
            check("the tool was offered, with web search", calls[0].Body.Contains("\"calendar_events\"") && calls[0].Body.Contains("\"web_search\""));
            check("the second call carries the call, its result and the encrypted reasoning", calls[1].Body.Contains("\"function_call_output\"") && calls[1].Body.Contains("\"call_1\"") && calls[1].Body.Contains("Standup, daily") && calls[1].Body.Contains("enc-reasoning-1"));
            check("the page was told a tool was running", statuses.SequenceEqual(new[] { "checking your calendar…" }));
            check("the reply is the last round's text, with its drawing", result.Outcome == ReplyOutcome.Done && result.Heard == "what is on tomorrow" && result.Body == "You have a standup at 9." && result.Drawings.Count == 1);
            check("the streamed reply never showed drawing markup", progress.All(p => !p.Body.Contains('<')) && progress.Any(p => p.Drawings is { Count: 1 }));
        }

        // ----- drawings in replies: SVG only -----
        var split = Svg.Extract("Here is a house.\n<svg viewBox=\"0 0 40 30\"><rect width=\"10\" height=\"10\"/></svg>\nIt has a door.", finished: true);
        check("a drawing is taken out of the words", split.Text == "Here is a house.\n\nIt has a door." && split.Blocks.Count == 1 && !split.Open);
        var wrapped = Svg.Extract("Your week:\n<figure aria-label=\"week\"><svg viewBox=\"0 0 4 3\"><g/></svg></figure>\nEnjoy.", finished: true);
        check("an old <figure> wrapper is dropped, the drawing in it stays", wrapped.Text == "Your week:\n\nEnjoy." && wrapped.Blocks.Count == 1 && wrapped.Blocks[0].StartsWith("<svg") && wrapped.Blocks[0].EndsWith("</svg>"));
        var fenced = Svg.Extract("Look:\n```svg\n<svg viewBox=\"0 0 4 3\"><g/></svg>\n```\nDone", finished: true);
        check("code fences around a drawing are dropped", fenced.Text == "Look:\n\nDone" && fenced.Blocks.Count == 1);
        var streaming = Svg.Extract("Here:\n<svg viewBox=\"0 0 4 3\"><text>Mo", finished: false);
        check("an unfinished drawing is held back", streaming.Open && streaming.Text == "Here:" && streaming.Blocks.Count == 0);
        check("a possible start of a drawing is held back while streaming", Svg.Extract("Here: <sv", finished: false).Text == "Here:" && Svg.Extract("Here: <fig", finished: false).Text == "Here:" && Svg.Extract("a < b", finished: false).Text == "a < b");
        var nested = Svg.Extract("<svg viewBox=\"0 0 4 3\"><svg><g/></svg></svg>after", finished: true);
        check("nested svgs stay in one drawing", nested.Blocks.Count == 1 && nested.Text == "after");
        var parsed = ReplyParser.Parse("HEARD: draw a cat\n---\nA cat:\n<svg viewBox=\"0 0 4 3\"><path d=\"M0 0\"/></svg>", finished: true);
        check("the parser keeps drawings out of the cleaned words", parsed.Body == "A cat:" && parsed.Drawings is { Count: 1 } && parsed.Heard == "draw a cat");

        var bare = Svg.Sanitize("<svg viewBox=\"0 0 4 3\"><title>A cat</title><path d=\"M0 0\" stroke=\"currentColor\"/></svg>");
        check("a drawing is kept as drawn, and can stand alone as an image", bare is not null && bare.StartsWith("<svg") && bare.Contains("viewBox=\"0 0 4 3\"") && bare.Contains("stroke=\"currentColor\"") && bare.Contains("xmlns=\"http://www.w3.org/2000/svg\""));
        var legacy = Svg.Sanitize("<figure><svg viewBox=\"0 0 4 3\"><title>A cat</title><path d=\"M0 0\"/></svg></figure>");
        check("a drawing saved in the old <figure> is unwrapped", legacy is not null && legacy.StartsWith("<svg") && !legacy.Contains("figure"));
        var unsafeBlock = Svg.Sanitize("<svg viewBox=\"0 0 4 3\" onload=\"x()\"><script>alert(1)</script><image href=\"http://e/p.png\"/><use href=\"http://e/s.svg#a\"/>"
            + "<a href=\"http://e\"><text>link</text></a><text>words</text><foreignObject><div>html</div></foreignObject><style>@import url(http://e/s.css)</style>"
            + "<rect style=\"fill:red;background:url(http://e/q.png)\" onclick=\"x()\" width=\"2\" height=\"2\"/></svg>")!;
        check("scripts, handlers, images, links, html and styles are taken out", !unsafeBlock.Contains("script") && !unsafeBlock.Contains("onclick") && !unsafeBlock.Contains("onload") && !unsafeBlock.Contains("<image") && !unsafeBlock.Contains("href") && !unsafeBlock.Contains("<use") && !unsafeBlock.Contains("foreignObject") && !unsafeBlock.Contains("<div") && !unsafeBlock.Contains("<style"));
        check("nothing in it can be fetched", !unsafeBlock.Replace("http://www.w3.org/2000/svg", "").Contains("http") && !unsafeBlock.Contains("@import"));
        check("what is safe stays: the shapes and their words", unsafeBlock.Contains("<rect") && unsafeBlock.Contains(">words<"));
        check("same-document references are fine", Svg.Sanitize("<svg><defs><marker id=\"a\"/></defs><path d=\"M0 0\" marker-end=\"url(#a)\"/></svg>")!.Contains("url(#a)"));
        check("an empty, a huge or a non-svg block is refused", Svg.Sanitize("<svg><script>x</script></svg>") is null && Svg.Sanitize("<svg>" + new string('x', Svg.MaxBytes) + "</svg>") is null && Svg.Sanitize("<div>x</div>") is null && Svg.Sanitize("<figure><table><tr><td>a</td></tr></table></figure>") is null);

        // ----- parts the user can edit -----
        var board = Svg.Sanitize("<svg id=\"board\" data-edit viewBox=\"0 0 400 200\"><title>Kanban</title>"
            + "<rect id=\"col-todo\" x=\"0\" y=\"0\" width=\"200\" height=\"200\" fill=\"none\" stroke=\"currentColor\"/><rect id=\"col-doing\" x=\"200\" y=\"0\" width=\"200\" height=\"200\" fill=\"none\" stroke=\"currentColor\"/>"
            + "<g id=\"card-1\" data-edit><rect id=\"card-1-bg\" x=\"20\" y=\"20\" width=\"100\" height=\"40\"/><text x=\"30\" y=\"45\">Call Anna</text></g>"
            + "<g data-edit><rect x=\"20\" y=\"80\" width=\"100\" height=\"40\"/></g><g data-edit onclick=\"x()\"><circle cx=\"50\" cy=\"150\" r=\"10\"/></g></svg>")!;
        check("data-edit survives, the root is not itself editable, and editable parts without an id get one", board.Contains("data-edit=\"\"") && Svg.IsEditable(board) && Svg.EditableIds(board).SequenceEqual(new[] { "card-1", "item-1", "item-2" }) && !board.Contains("onclick") && !board.StartsWith("<svg id=\"board\" data-edit"));
        check("the root id names the drawing", Svg.FigureId(board) == "board" && Svg.FigureId(bare!) is null && !Svg.IsEditable(bare!));
        check("the title and the history text come from the drawing", Svg.Title(board) == "Kanban" && Svg.Text(board) == "[drawing: id \"board\", Kanban, editable]" && Svg.Text(bare!) == "[drawing: A cat]" && Svg.Title("<svg aria-label=\" A  map \"><title>T</title></svg>") == "A map");

        var moved = Svg.ApplyEdits(board, new[] { new DrawingChange("card-1", "move", "translate(210 0)") })!;
        check("a move is written into the drawing as a transform", moved.Contains("id=\"card-1\"") && moved.Contains("transform=\"translate(210 0)\"") && Svg.IsEditable(moved));
        var turned = Svg.ApplyEdits(moved, new[] { new DrawingChange("card-1", "rotate", "matrix(0.9659 0.2588 -0.2588 0.9659 200 -50)", 15) })!;
        check("a later turn replaces the transform", turned.Contains("matrix(0.9659 0.2588 -0.2588 0.9659 200 -50)") && !turned.Contains("translate(210 0)"));
        var deleted = Svg.ApplyEdits(board, new[] { new DrawingChange("card-1", "delete"), new DrawingChange("item-1", "delete") })!;
        check("deleting removes the part and what is in it", !deleted.Contains("Call Anna") && !deleted.Contains("card-1") && deleted.Contains("col-todo") && Svg.EditableIds(deleted).SequenceEqual(new[] { "item-2" }));
        check("only editable parts can be changed", Svg.ApplyEdits(board, new[] { new DrawingChange("col-todo", "move", "translate(1 1)") }) is null
            && Svg.ApplyEdits(board, new[] { new DrawingChange("board", "delete") }) is null && Svg.ApplyEdits(board, new[] { new DrawingChange("nope", "delete") }) is null
            && Svg.ApplyEdits(board, new[] { new DrawingChange("card-1-bg", "move", "translate(1 1)") }) is null);
        check("a transform is only numbers, and a change is all or nothing", Svg.ApplyEdits(board, new[] { new DrawingChange("card-1", "move", "translate(1 1); evil()") }) is null
            && Svg.ApplyEdits(board, new[] { new DrawingChange("card-1", "move", "url(http://e)") }) is null && Svg.ApplyEdits(board, new[] { new DrawingChange("card-1", "move", null) }) is null
            && Svg.ApplyEdits(board, new[] { new DrawingChange("card-1", "move", "translate(5 5)"), new DrawingChange("col-todo", "delete") }) is null && Svg.ApplyEdits(board, new[] { new DrawingChange("card-1", "explode") }) is null);

        var boxes = new Dictionary<string, Box>
        {
            ["col-todo"] = new(0, 0, 200, 200), ["col-doing"] = new(200, 0, 200, 200),
            ["card-1"] = new(230, 20, 100, 40), ["card-1-bg"] = new(230, 20, 100, 40), ["item-2"] = new(40, 140, 20, 20),
        };
        var edits = new Dictionary<string, Svg.EditEntry> { ["card-1"] = new() { First = new Box(20, 20, 100, 40) } };
        var said = Svg.DescribeEdits(board, edits, boxes)!;
        check("a move is described with how far it went and which named region it is now in", said.Contains("card-1: moved by (+210, +0), from x 20, y 20 to x 230, y 20") && said.Contains("its centre is now inside #col-doing") && !said.Contains("#col-todo") && !said.Contains("card-1-bg"));
        var spinning = new Dictionary<string, Svg.EditEntry> { ["item-2"] = new() { First = new Box(40, 140, 20, 20), Degrees = 30 }, ["card-1"] = new() { First = new Box(20, 20, 100, 40), Deleted = true } };
        var spun = Svg.DescribeEdits(board, spinning, boxes)!;
        check("a turn and a deletion are described", spun.Contains("item-2: turned 30° clockwise") && spun.Contains("card-1: deleted (it was at x 20, y 20, 100×40)"));
        check("a part moved away and back is not reported", Svg.DescribeEdits(board, new Dictionary<string, Svg.EditEntry> { ["card-1"] = new() { First = new Box(230, 20, 100, 40) } }, boxes) is null);
        check("turning back counts as nothing too, and overlaps are named", new Svg.EditEntry { First = new Box(0, 0, 1, 1), Degrees = 360 }.IsNet(new Box(0, 0, 1, 1)) == false
            && Svg.DescribeEdits(board, new Dictionary<string, Svg.EditEntry> { ["item-2"] = new() { First = new Box(0, 0, 20, 20) } }, new Dictionary<string, Box> { ["item-2"] = new(185, 10, 20, 20), ["col-todo"] = new(0, 0, 200, 200), ["col-doing"] = new(200, 0, 200, 200) })!.Contains("its centre is now inside #col-todo; it overlaps #col-doing"));

        var doc = Svg.Document("<svg viewBox=\"0 0 4 3\"><g/></svg>");
        check("the document lets nothing run or load, and has the ink to fill in", doc.Contains("default-src 'none'") && !doc.Contains("script-src") && doc.Contains(Svg.InkToken) && doc.Contains("<svg viewBox"));
        var (w, h) = Svg.Fit(0.5, 560, 480);
        check("a tall drawing is limited in height, keeping its shape", Math.Abs(h - 480) < 1e-9 && Math.Abs(w - 240) < 1e-9 && Svg.Fit(2, 560, 480) == (560, 280) && Svg.Fit(0.1, 600).Height == Svg.MaxHeight);
    }

    static IReadOnlyDictionary<string, JsonElement> Args(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
}
