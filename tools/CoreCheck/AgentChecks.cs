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

        // ----- blocks in replies: layouts and SVG pictures -----
        var split = Blocks.Extract("Here is a house.\n<svg viewBox=\"0 0 40 30\"><rect width=\"10\" height=\"10\"/></svg>\nIt has a door.", finished: true);
        check("a picture is taken out of the words", split.Text == "Here is a house.\n\nIt has a door." && split.Blocks.Count == 1 && !split.Open);
        var both = Blocks.Extract("Plan:\n<layout id=\"b\"><box>x</box></layout>\nand a sketch:\n<svg viewBox=\"0 0 4 3\"><g/></svg>\nDone.", finished: true);
        check("a layout and a picture are two blocks, in order", both.Blocks.Count == 2 && both.Blocks[0].StartsWith("<layout") && both.Blocks[1].StartsWith("<svg") && both.Text == "Plan:\n\nand a sketch:\n\nDone.");
        var wrapped = Blocks.Extract("Your week:\n<figure aria-label=\"week\"><svg viewBox=\"0 0 4 3\"><g/></svg></figure>\nEnjoy.", finished: true);
        check("an old <figure> wrapper is dropped, the drawing in it stays", wrapped.Text == "Your week:\n\nEnjoy." && wrapped.Blocks.Count == 1 && wrapped.Blocks[0].StartsWith("<svg") && wrapped.Blocks[0].EndsWith("</svg>"));
        var fenced = Blocks.Extract("Look:\n```xml\n<layout><box>a</box></layout>\n```\nDone", finished: true);
        check("code fences around a block are dropped", fenced.Text == "Look:\n\nDone" && fenced.Blocks.Count == 1);
        var streaming = Blocks.Extract("Here:\n<layout id=\"b\"><col><box>Mo", finished: false);
        check("an unfinished block is held back", streaming.Open && streaming.Text == "Here:" && streaming.Blocks.Count == 0);
        check("a possible start of a block is held back while streaming", Blocks.Extract("Here: <sv", finished: false).Text == "Here:" && Blocks.Extract("Here: <lay", finished: false).Text == "Here:" && Blocks.Extract("a < b", finished: false).Text == "a < b");
        var nested = Blocks.Extract("<svg viewBox=\"0 0 4 3\"><svg><g/></svg></svg>after", finished: true);
        check("nested svgs stay in one block", nested.Blocks.Count == 1 && nested.Text == "after");
        var parsed = ReplyParser.Parse("HEARD: draw a cat\n---\nA cat:\n<svg viewBox=\"0 0 4 3\"><path d=\"M0 0\"/></svg>", finished: true);
        check("the parser keeps blocks out of the cleaned words", parsed.Body == "A cat:" && parsed.Drawings is { Count: 1 } && parsed.Heard == "draw a cat");

        var bare = Blocks.Sanitize("<svg viewBox=\"0 0 4 3\"><title>A cat</title><path d=\"M0 0\" stroke=\"currentColor\"/></svg>");
        check("a picture is kept as drawn, and can stand alone as an image", bare is not null && bare.StartsWith("<svg") && bare.Contains("viewBox=\"0 0 4 3\"") && bare.Contains("stroke=\"currentColor\"") && bare.Contains("xmlns=\"http://www.w3.org/2000/svg\""));
        var legacy = Blocks.Sanitize("<figure><svg viewBox=\"0 0 4 3\"><title>A cat</title><path d=\"M0 0\"/></svg></figure>");
        check("a picture saved in the old <figure> is unwrapped", legacy is not null && legacy.StartsWith("<svg") && !legacy.Contains("figure"));
        var unsafeBlock = Blocks.Sanitize("<svg viewBox=\"0 0 4 3\" onload=\"x()\"><script>alert(1)</script><image href=\"http://e/p.png\"/><use href=\"http://e/s.svg#a\"/>"
            + "<a href=\"http://e\"><text>link</text></a><text>words</text><foreignObject><div>html</div></foreignObject><style>@import url(http://e/s.css)</style>"
            + "<rect style=\"fill:red;background:url(http://e/q.png)\" onclick=\"x()\" data-edit width=\"2\" height=\"2\"/></svg>")!;
        check("scripts, handlers, images, links, html and styles are taken out of a picture", !unsafeBlock.Contains("script") && !unsafeBlock.Contains("onclick") && !unsafeBlock.Contains("onload") && !unsafeBlock.Contains("<image") && !unsafeBlock.Contains("href") && !unsafeBlock.Contains("<use") && !unsafeBlock.Contains("foreignObject") && !unsafeBlock.Contains("<div") && !unsafeBlock.Contains("<style"));
        check("nothing in it can be fetched, and a picture has nothing to edit", !unsafeBlock.Replace("http://www.w3.org/2000/svg", "").Contains("http") && !unsafeBlock.Contains("@import") && !unsafeBlock.Contains("data-edit") && !Blocks.IsEditable(unsafeBlock));
        check("what is safe stays: the shapes and their words", unsafeBlock.Contains("<rect") && unsafeBlock.Contains(">words<"));
        check("same-document references are fine", Blocks.Sanitize("<svg><defs><marker id=\"a\"/></defs><path d=\"M0 0\" marker-end=\"url(#a)\"/></svg>")!.Contains("url(#a)"));
        check("an empty, a huge or an unknown block is refused", Blocks.Sanitize("<svg><script>x</script></svg>") is null && Blocks.Sanitize("<svg>" + new string('x', Blocks.MaxBytes) + "</svg>") is null && Blocks.Sanitize("<div>x</div>") is null
            && Blocks.Sanitize("<figure><table><tr><td>a</td></tr></table></figure>") is null && Blocks.Sanitize("<layout></layout>") is null && Blocks.Sanitize("<layout>just words</layout>") is not null);

        // ----- the layout language -----
        var board = Blocks.Sanitize("<layout id=\"board\" title=\"Week\" onclick=\"x()\"><row>"
            + "<col id=\"todo\" drop title=\"To do\"><box id=\"c1\" edit>Call Anna</box><box edit>Gym &amp; run</box></col>"
            + "<col drop title=\"Doing\"/><script>alert(1)</script><svg><text>nope</text></svg></row><rule/><text>Notes <b>here</b></text></layout>")!;
        check("a layout is written out in its canonical form, with only the language's elements and attributes", board.StartsWith("<layout id=\"board\" title=\"Week\">") && board.Contains("<col id=\"todo\" title=\"To do\" drop>") && board.Contains("<box id=\"c1\" edit>Call Anna</box>") && board.Contains("<rule/>") && !board.Contains("onclick") && !board.Contains("<script") && !board.Contains("<svg") && !board.Contains("<b>"));
        check("words are kept, escaped, and what was inside an unknown element stays, unless it is a script or a picture", board.Contains("Gym &amp; run") && !board.Contains("alert(1)") && !board.Contains("nope") && board.Contains(">Notes here<"));
        check("every part the user can act on has an id, and so does its container", board.Contains("id=\"item-1\" edit>Gym &amp; run") && board.Contains("<col id=\"area-1\" title=\"Doing\" drop/>"));
        check("it survives being read and written again", Layout.Serialize(Layout.Parse(board)!) == board);
        check("it is editable, and has an id and a title", Blocks.IsEditable(board) && Blocks.FigureId(board) == "board" && Blocks.Title(board) == "Week" && Blocks.Text(board) == board && Blocks.IsLayout(board) && !Blocks.IsEditable(bare!));
        check("the whole layout is not a part to move, drop is only for containers, and bad ids are dropped", Layout.Sanitize("<layout id=\"a\" edit drop rot=\"30\"><text id=\"t\" drop>x</text><box id=\"1bad\" edit>y</box></layout>") is { } odd && !odd.StartsWith("<layout id=\"a\" edit") && !odd.Contains("rot=\"30\"") && odd.Contains("<text id=\"t\">x</text>") && odd.Contains("<box id=\"item-1\" edit>y</box>"));
        check("duplicate ids are not kept", Layout.Sanitize("<layout><box id=\"a\" edit>1</box><box id=\"a\" edit>2</box></layout>") is { } twin && twin.Contains("<box id=\"a\" edit>1</box>") && twin.Contains("<box id=\"item-1\" edit>2</box>"));
        check("a layout that is too big or too deep is refused", Layout.Sanitize("<layout>" + string.Concat(Enumerable.Repeat("<box>x</box>", Layout.MaxNodes + 1)) + "</layout>") is null
            && Layout.Sanitize("<layout>" + string.Concat(Enumerable.Repeat("<col>", Layout.MaxDepth + 1)) + "x" + string.Concat(Enumerable.Repeat("</col>", Layout.MaxDepth + 1)) + "</layout>") is null);
        var html = Blocks.Document(board);
        check("it is rendered by the browser from a document that lets nothing run or load, with every word escaped", html.Contains("default-src 'none'") && !html.Contains("script-src") && html.Contains(Blocks.InkToken) && html.Contains("class=\"n box edit\" id=\"c1\" data-edit>Call Anna</div>") && html.Contains(">Gym &amp; run</div>") && !html.Contains("<script"));
        check("a drop target and the title are in the document", html.Contains("class=\"n col drop\" id=\"todo\" data-drop><div class=\"title\">To do</div>"));
        var rendered = Blocks.Document(Blocks.Sanitize("<layout><box edit rot=\"15\">x</box></layout>")!);
        check("a turned part is turned in the document", rendered.Contains("data-rot=\"15\" style=\"transform:rotate(15deg)\""));

        // ----- colours -----
        var coloured = Blocks.Sanitize("<layout color=\"Red\" bg=\"grey\"><box id=\"a\" edit color=\"#ABC\" bg=\"#1e90ff\">x</box><text color=\"url(http://e/x)\">y</text><box color=\"red;background:url(//e)\" bg=\"expression(1)\">z</box><box color=\"#12\">w</box><rule color=\"green\"/></layout>")!;
        check("palette names and hex colours are kept, in lower case, and anything else is dropped", coloured.StartsWith("<layout color=\"red\" bg=\"gray\">") && coloured.Contains("<box id=\"a\" color=\"#abc\" bg=\"#1e90ff\" edit>x</box>") && coloured.Contains("<text>y</text>") && coloured.Contains("<box>z</box>") && coloured.Contains("<box>w</box>") && coloured.Contains("<rule color=\"green\"/>"));
        check("a coloured layout survives being read and written again", Layout.Serialize(Layout.Parse(coloured)!) == coloured);
        var tinted = Blocks.Document(coloured);
        check("a palette colour is a class, with a light and a dark value", tinted.Contains("class=\"n layout fg-red bg-gray\"") && tinted.Contains(".fg-red{color:#C62828}") && tinted.Contains("@media (prefers-color-scheme: dark){.fg-red{color:#FF8A80}") && tinted.Contains(".bg-blue{background:color-mix(in srgb,#1565C0 22%,transparent)}"));
        check("a hex colour is a style, and nothing else can get into one", tinted.Contains("style=\"color:#abc;background:color-mix(in srgb,#1e90ff 22%,transparent)\"") && !tinted.Contains("url(") && !tinted.Contains("expression") && !tinted.Contains("http://e"));
        check("a turned, coloured part keeps both", Blocks.Document(Blocks.Sanitize("<layout><box edit rot=\"15\" color=\"#f00\">x</box></layout>")!).Contains("data-rot=\"15\" style=\"color:#f00;transform:rotate(15deg)\""));
        check("colours don't get in the way of editing", Layout.ApplyEdits(coloured, new[] { new DrawingChange("a", "rotate", Degrees: 20) }) is { } turnedColour && turnedColour.Contains("<box id=\"a\" color=\"#abc\" bg=\"#1e90ff\" edit rot=\"20\">"));

        // ----- timelines: columns that place their children by time -----
        var day = Blocks.Sanitize("""
            <layout id="day"><row>
              <col id="thu" timeline="8-18" drop title="Thu 12">
                <box id="e1" edit start="9:00" end="9:30">Standup</box>
                <box id="e2" edit start="14:00" end="15:30" rot="20">Dentist</box>
                <box id="e3" start="14:30" end="15:00">Call</box>
                <box id="e4" edit>Someday</box>
              </col>
              <col id="fri" timeline="09:00-17:00" drop/>
            </row><col id="plain" drop title="Unscheduled"/></layout>
            """)!;
        check("a timeline column is written out in the canonical form, its range normalised, its items' times as HH:MM", day.Contains("<col id=\"thu\" title=\"Thu 12\" timeline=\"08:00-18:00\" drop>") && day.Contains("<box id=\"e1\" start=\"09:00\" end=\"09:30\" edit>Standup</box>") && day.Contains("<col id=\"fri\" timeline=\"09:00-17:00\" drop/>"));
        check("an item in a timeline can't be turned", day.Contains("<box id=\"e2\" start=\"14:00\" end=\"15:30\" edit>Dentist</box>") && !day.Contains("rot="));
        check("a bare timeline stays bare, and a range that isn't one is taken as bare", Layout.Sanitize("<layout><col id=\"a\" timeline><box>x</box></col></layout>")!.Contains("<col id=\"a\" timeline>") && Layout.Sanitize("<layout><col id=\"a\" timeline=\"18-8\"><box>x</box></col></layout>")!.Contains("<col id=\"a\" timeline>")
            && Layout.Sanitize("<layout><col id=\"a\" timeline=\"junk\"><box>x</box></col></layout>")!.Contains("<col id=\"a\" timeline>") && Layout.Sanitize("<layout><col id=\"a\" timeline=\"8-25\"><box>x</box></col></layout>")!.Contains("<col id=\"a\" timeline>"));
        var oddTimes = Layout.Sanitize("<layout><col id=\"plain\"><box start=\"10:00\" end=\"11:00\">x</box></col><box timeline=\"8-18\">y</box><col timeline><box id=\"a\" start=\"25:00\">bad</box><box start=\"10:00\" end=\"09:00\">backwards</box><box end=\"11:00\">no start</box></col></layout>")!;
        check("times only mean something in a timeline column, and only a column can be one", oddTimes.Contains("<col id=\"plain\">") && !oddTimes.Contains("start=\"10:00\" end=\"11:00\"") && oddTimes.Contains("<box>y</box>") && !oddTimes.Contains("timeline=\"08:00-18:00\""));
        check("a start that isn't a time, an end before the start, and an end with no start are dropped", oddTimes.Contains("<box id=\"a\">bad</box>") && oddTimes.Contains("<box start=\"10:00\">backwards</box>") && oddTimes.Contains("<box>no start</box>"));
        check("it survives being read and written again", Layout.Serialize(Layout.Parse(day)!) == day && Layout.Serialize(Layout.Parse(oddTimes)!) == oddTimes);
        check("times are read back as minutes, with an hour when there is no end, and none for what has no start", Layout.Parse(day) is { } dayTree && Layout.TimesOf(Layout.Find(dayTree, "e1")!) == (540, 570) && Layout.TimesOf(Layout.Find(dayTree, "e4")!) is null
            && Layout.Parse(oddTimes) is { } oddTimesTree && Layout.TimesOf(oddTimesTree.Descendants().First(d => d.Words() == "backwards")) == (600, 660));
        var dayRoot = Layout.Parse(day)!;
        check("the hours of timeline columns side by side are shared, the widest range", Layout.RangeOf(Layout.Find(dayRoot, "thu")!) == (480, 1080) && Layout.RangeOf(Layout.Find(dayRoot, "fri")!) == (480, 1080));
        var late = Layout.Parse(Layout.Sanitize("<layout><row><col id=\"a\" timeline=\"9-12\"><box start=\"11:30\" end=\"13:10\">x</box></col><col id=\"b\" timeline=\"9-12\"><box start=\"7:45\">y</box></col></row></layout>")!)!;
        check("an item outside the range widens it to whole hours", Layout.RangeOf(Layout.Find(late, "a")!) == (420, 840));
        check("with no range the items set it, and with nothing it is a working day", Layout.RangeOf(Layout.Parse(Layout.Sanitize("<layout><col id=\"a\" timeline><box start=\"9:20\" end=\"10:40\">x</box></col></layout>")!)!.Descendants().First(d => d.Id == "a")) == (540, 660)
            && Layout.RangeOf(Layout.Parse(Layout.Sanitize("<layout><col id=\"a\" timeline><box>x</box></col></layout>")!)!.Descendants().First(d => d.Id == "a")) == (480, 1080));
        var dayHtml = Blocks.Document(day);
        var dayBody = dayHtml[dayHtml.IndexOf("<body>")..];
        check("the hours are a grid as tall as the range, which says where it starts so a drop can be turned back into a time", dayBody.Contains("<div class=\"tl\" data-from=\"480\" data-to=\"1080\" data-ph=\"64\" style=\"height:640px\">"));
        check("an item is placed at its start, as tall as it is long", dayBody.Contains("data-start=\"540\" data-end=\"570\" style=\"top:64px;height:30px;left:calc(0% + 1px);width:calc(100% - 3px)\">Standup"));
        check("items that overlap share the width side by side, and the others keep all of it", dayBody.Contains("id=\"e2\" data-edit data-start=\"840\" data-end=\"930\" style=\"top:384px;height:94px;left:calc(0% + 1px);width:calc(50% - 3px)\">Dentist")
            && dayBody.Contains("id=\"e3\" data-start=\"870\" data-end=\"900\" style=\"top:416px;height:30px;left:calc(50% + 1px);width:calc(50% - 3px)\">Call"));
        check("what has no time comes before the hours", dayBody.IndexOf("Someday") is > 0 and var someday && someday < dayBody.IndexOf("<div class=\"tl\""));
        check("the first day shows the hours, once, and the others don't", dayBody.Split("class=\"tl-label\"").Length - 1 == 11 && dayBody.Contains("<div class=\"tl-label\" style=\"top:0px\">08:00</div>") && dayBody.Contains(">18:00</div>")
            && dayBody.Split("tl-axis").Length - 1 == 1 && dayBody.IndexOf("tl-axis") < dayBody.IndexOf("id=\"fri\""));
        check("the grid has its own style, and a colour on an item still shows", dayHtml.Contains(".tl { position: relative;") && Blocks.Document(Blocks.Sanitize("<layout><col timeline><box start=\"9:00\" color=\"red\">x</box></col></layout>")!).Contains("class=\"n box fg-red\""));

        string? Times(string? markup, string id) => markup is null ? null : Layout.TimesOf(Layout.Find(Layout.Parse(markup)!, id)!) is { } t ? Layout.Hhmm(t.Start) + "–" + Layout.Hhmm(t.End) : "-";
        string? ParentId(string? markup, string id) => markup is null ? null : Layout.Find(Layout.Parse(markup)!, id)!.Parent!.Id;
        var retimed = Layout.ApplyEdits(day, new[] { new DrawingChange("e1", "move", "thu", At: "10:15") });
        check("an item moved in its own column keeps its length, and its place among the others", Times(retimed, "e1") == "10:15–10:45" && Layout.Find(Layout.Parse(retimed!)!, "thu")!.Elements.Select(e => e.Id).SequenceEqual(new[] { "e1", "e2", "e3", "e4" }));
        var other = Layout.ApplyEdits(day, new[] { new DrawingChange("e2", "move", "fri", At: "11:00") });
        check("moved to another day's column it starts there, keeping its length", ParentId(other, "e2") == "fri" && Times(other, "e2") == "11:00–12:30");
        check("a start that would run past the end of the day is brought back inside it", Times(Layout.ApplyEdits(day, new[] { new DrawingChange("e1", "move", "fri", At: "23:50") }), "e1") == "23:30–24:00");
        check("an item with no time dropped into a timeline gets an hour", Times(Layout.ApplyEdits(day, new[] { new DrawingChange("e4", "move", "thu", At: "11:00") }), "e4") == "11:00–12:00");
        check("a move into a timeline needs a time that is one", Layout.ApplyEdits(day, new[] { new DrawingChange("e1", "move", "fri") }) is null && Layout.ApplyEdits(day, new[] { new DrawingChange("e1", "move", "fri", At: "25:00") }) is null && Layout.ApplyEdits(day, new[] { new DrawingChange("e1", "move", "fri", At: "soon") }) is null);
        var noDrop = Layout.Sanitize("<layout><row><col id=\"a\" timeline><box id=\"x\" edit start=\"9:00\">x</box></col><col id=\"b\" timeline/></row></layout>")!;
        check("a timeline column lets its own items change time without drop, but takes items from elsewhere only with it", Times(Layout.ApplyEdits(noDrop, new[] { new DrawingChange("x", "move", "a", At: "10:00") }), "x") == "10:00–11:00" && Layout.ApplyEdits(noDrop, new[] { new DrawingChange("x", "move", "b", At: "10:00") }) is null);
        var out2 = Layout.ApplyEdits(day, new[] { new DrawingChange("e1", "move", "plain") });
        check("moved out of a timeline it has no time any more", ParentId(out2, "e1") == "plain" && Times(out2, "e1") == "-" && !out2!.Contains("start=\"09:00\""));
        check("an end can be dragged, to at least a step after the start and not past the day", Times(Layout.ApplyEdits(day, new[] { new DrawingChange("e1", "resize", End: "11:00") }), "e1") == "09:00–11:00" && Times(Layout.ApplyEdits(day, new[] { new DrawingChange("e1", "resize", End: "09:05") }), "e1") == "09:00–09:15"
            && Times(Layout.ApplyEdits(day, new[] { new DrawingChange("e1", "resize", End: "24:00") }), "e1") == "09:00–24:00");
        check("only an item with a start can be resized, to a time that is one", Layout.ApplyEdits(day, new[] { new DrawingChange("e4", "resize", End: "11:00") }) is null && Layout.ApplyEdits(day, new[] { new DrawingChange("e1", "resize", End: "25:00") }) is null && Layout.ApplyEdits(day, new[] { new DrawingChange("e1", "resize") }) is null);
        check("turning an item of a timeline is refused, and so is a move to a column that isn't there", Layout.ApplyEdits(day, new[] { new DrawingChange("e1", "rotate", Degrees: 10) }) is null && Layout.ApplyEdits(day, new[] { new DrawingChange("e1", "move", "mon", At: "10:00") }) is null);

        var wasE1 = new Dictionary<string, Layout.EditEntry> { ["e1"] = Layout.Capture(day, "e1")! };
        check("what an item was is remembered, with its time", wasE1["e1"].Parent == "thu" && wasE1["e1"].Start == 540 && wasE1["e1"].End == 570 && Layout.Capture(day, "e4")!.Start is null);
        check("a new time is reported as rescheduled, with both", Layout.DescribeEdits(retimed!, wasE1) == "- e1 \"Standup\": rescheduled from 09:00–09:30 to 10:15–10:45");
        check("a new end is reported as resized", Layout.DescribeEdits(Layout.ApplyEdits(day, new[] { new DrawingChange("e1", "resize", End: "11:00") })!, wasE1) == "- e1 \"Standup\": resized from 09:00–09:30 to 09:00–11:00");
        check("another day is reported as a move, with the day and the times", Layout.DescribeEdits(Layout.ApplyEdits(day, new[] { new DrawingChange("e1", "move", "fri", At: "16:30") })!, wasE1) == "- e1 \"Standup\": moved from #thu 09:00–09:30 to #fri 16:30–17:00");
        check("moved away and back is not reported", Layout.DescribeEdits(Layout.ApplyEdits(retimed!, new[] { new DrawingChange("e1", "move", "thu", At: "09:00") })!, wasE1) is null);
        var wasE4 = new Dictionary<string, Layout.EditEntry> { ["e4"] = Layout.Capture(day, "e4")! };
        check("an item given a time is reported as scheduled", Layout.DescribeEdits(Layout.ApplyEdits(day, new[] { new DrawingChange("e4", "move", "thu", At: "11:00") })!, wasE4) == "- e4 \"Someday\": scheduled at 11:00–12:00 (it had no time)");
        check("moving out of a timeline and deleting say where the item was", Layout.DescribeEdits(out2!, wasE1) == "- e1 \"Standup\": moved from #thu 09:00–09:30 to #plain (position 1 of 1)"
            && Layout.DescribeEdits(Layout.ApplyEdits(day, new[] { new DrawingChange("e1", "delete") })!, new Dictionary<string, Layout.EditEntry> { ["e1"] = new() { Parent = "thu", Position = 1, Start = 540, End = 570, Label = "Standup", Deleted = true } }) == "- e1 \"Standup\": deleted (it was in #thu 09:00–09:30)");

        // ----- the user's changes -----
        var moved = Layout.ApplyEdits(board, new[] { new DrawingChange("c1", "move", "area-1") })!;
        check("a part can be moved into a container that accepts drops, at the end by default", moved.Contains("<col id=\"todo\" title=\"To do\" drop>\n") && moved.IndexOf("id=\"item-1\"") < moved.IndexOf("<col id=\"area-1\"") && moved.IndexOf("<col id=\"area-1\"") < moved.IndexOf("id=\"c1\" edit") && Layout.Find(Layout.Parse(moved)!, "c1")!.Parent!.Id == "area-1");
        var first = Layout.ApplyEdits(board, new[] { new DrawingChange("item-1", "move", "todo", 0) })!;
        check("or at a given place", first.IndexOf("id=\"item-1\"") < first.IndexOf("id=\"c1\""));
        var several = Layout.ApplyEdits(board, new[] { new DrawingChange("c1", "move", "area-1", 0), new DrawingChange("item-1", "move", "area-1", 1) })!;
        check("several parts keep their order", Layout.Find(Layout.Parse(several)!, "area-1")!.Elements.Select(e => e.Id).SequenceEqual(new[] { "c1", "item-1" }) && Layout.Find(Layout.Parse(several)!, "todo")!.Elements.Count() == 0);
        var turnedBoard = Layout.ApplyEdits(Layout.ApplyEdits(board, new[] { new DrawingChange("c1", "rotate", Degrees: 10) })!, new[] { new DrawingChange("c1", "rotate", Degrees: 5) })!;
        check("turning adds up", turnedBoard.Contains("<box id=\"c1\" edit rot=\"15\">") && Layout.ApplyEdits(turnedBoard, new[] { new DrawingChange("c1", "rotate", Degrees: -15) })!.Contains("<box id=\"c1\" edit>"));
        var gone = Layout.ApplyEdits(board, new[] { new DrawingChange("c1", "delete") })!;
        check("deleting removes the part and its words", !gone.Contains("Call Anna") && gone.Contains("Gym"));
        check("only editable parts can be changed, and only into containers that accept drops", Layout.ApplyEdits(board, new[] { new DrawingChange("todo", "delete") }) is null && Layout.ApplyEdits(board, new[] { new DrawingChange("board", "delete") }) is null
            && Layout.ApplyEdits(board, new[] { new DrawingChange("nope", "delete") }) is null && Layout.ApplyEdits(board, new[] { new DrawingChange("c1", "move", "board") }) is null && Layout.ApplyEdits(board, new[] { new DrawingChange("c1", "move", "c1") }) is null
            && Layout.ApplyEdits(board, new[] { new DrawingChange("c1", "move", "nowhere") }) is null && Layout.ApplyEdits(board, new[] { new DrawingChange("c1", "move", null) }) is null);
        var nestedBoard = Layout.Sanitize("<layout><box id=\"outer\" edit drop><box id=\"inner\" drop>x</box></box></layout>")!;
        check("a part can't be dropped into itself, and a change is all or nothing", Layout.ApplyEdits(nestedBoard, new[] { new DrawingChange("outer", "move", "inner") }) is null && Layout.ApplyEdits(nestedBoard, new[] { new DrawingChange("outer", "rotate", Degrees: 5), new DrawingChange("inner", "delete") }) is null && Layout.ApplyEdits(nestedBoard, new[] { new DrawingChange("outer", "explode") }) is null);

        var edits = new Dictionary<string, Layout.EditEntry> { ["c1"] = Layout.Capture(board, "c1")! };
        check("what a part was is remembered: where, and its words", edits["c1"].Parent == "todo" && edits["c1"].Position == 1 && edits["c1"].Label == "Call Anna" && Layout.Capture(board, "nope") is null && Layout.Capture(board, "board") is null);
        var told = Layout.DescribeEdits(moved, edits)!;
        check("a move is described with where it was and where it is", told == "- c1 \"Call Anna\": moved from #todo (position 1) to #area-1 (position 1 of 1)");
        check("a part moved away and back is not reported", Layout.DescribeEdits(board, edits) is null && !Layout.HasNetEdits(board, edits) && Layout.HasNetEdits(moved, edits));
        var reordered = Layout.ApplyEdits(board, new[] { new DrawingChange("c1", "move", "todo", 1) })!;
        check("a move within a container says so", Layout.DescribeEdits(reordered, edits) == "- c1 \"Call Anna\": moved within #todo from position 1 to position 2 (of 2)");
        check("a turn and a deletion are described", Layout.DescribeEdits(turnedBoard, edits) == "- c1 \"Call Anna\": turned 15° clockwise"
            && Layout.DescribeEdits(gone, new Dictionary<string, Layout.EditEntry> { ["c1"] = new() { Parent = "todo", Position = 1, Label = "Call Anna", Deleted = true } }) == "- c1 \"Call Anna\": deleted (it was in #todo (position 1))"
            && Layout.DescribeEdits(Layout.ApplyEdits(board, new[] { new DrawingChange("c1", "rotate", Degrees: -30) })!, edits)!.Contains("anticlockwise"));
        check("a part directly in the layout is said to be in it, by its id", Layout.Capture(Layout.Sanitize("<layout id=\"w\"><box id=\"a\" edit>x</box></layout>")!, "a")!.Parent == "w" && Layout.Capture(Layout.Sanitize("<layout><box id=\"a\" edit>x</box></layout>")!, "a")!.Parent == "top");

        var doc = Blocks.Document("<svg viewBox=\"0 0 4 3\"><g/></svg>");
        check("a picture's document lets nothing run or load, and has the ink to fill in", doc.Contains("default-src 'none'") && !doc.Contains("script-src") && doc.Contains(Blocks.InkToken) && doc.Contains("<svg viewBox") && Svg.Text(bare!) == "[drawing: A cat]" && Svg.Text("<svg id=\"x\"><title>T</title></svg>") == "[drawing: id \"x\", T]");
        var (w, h) = Blocks.Fit(0.5, 560, 480);
        check("a tall block is limited in height, keeping its shape", Math.Abs(h - 480) < 1e-9 && Math.Abs(w - 240) < 1e-9 && Blocks.Fit(2, 560, 480) == (560, 280) && Blocks.Fit(0.1, 600).Height == Blocks.MaxHeight);
    }

    static IReadOnlyDictionary<string, JsonElement> Args(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
}
