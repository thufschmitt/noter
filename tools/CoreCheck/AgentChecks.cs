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

            // ----- the whole tool loop through the Claude service, against a fake API -----
            await using var api = new FakeAnthropic { FinalText = "HEARD: what is on tomorrow\n---\nYou have a standup at 9.\n<svg viewBox=\"0 0 100 50\"><title>A clock</title><circle cx=\"25\" cy=\"25\" r=\"20\"/></svg>" };
            var statuses = new List<string>();
            var progress = new List<ReplyParser.Parsed>();
            var service = new ClaudeService("sk-ant-placeholder", model: "claude-haiku-5-5", baseUrl: api.BaseUrl, agentTools: new IAgentTool[] { tool });
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
            var result = await service.AskAsync(png, Array.Empty<Turn>(), progress.Add, default, statuses.Add);

            var calls = api.Requests.Where(q => q.Path.EndsWith("/messages")).ToList();
            check("Claude was called twice: once for the tool call, once with its result", calls.Count == 2);
            check("the tool was offered to Claude", calls[0].Body.Contains("\"calendar_events\"") && calls[0].Body.Contains("web_search"));
            check("the second call carries the tool result and the thinking block", calls[1].Body.Contains("\"tool_result\"") && calls[1].Body.Contains("Standup, daily") && calls[1].Body.Contains("sig-abc"));
            check("the page was told a tool was running", statuses.SequenceEqual(new[] { "checking your calendar…" }));
            check("the reply is the last round's text, with its drawing", result.Outcome == ReplyOutcome.Done && result.Heard == "what is on tomorrow" && result.Body == "You have a standup at 9." && result.Drawings.Count == 1);
            check("the streamed reply never showed drawing markup", progress.All(p => !p.Body.Contains('<')) && progress.Any(p => p.Drawings is { Count: 1 }));
        }

        // ----- blocks in replies: HTML figures, drawings among them -----
        var split = Html.Extract("Here is a house.\n<svg viewBox=\"0 0 40 30\"><rect width=\"10\" height=\"10\"/></svg>\nIt has a door.", finished: true);
        check("a bare drawing is taken out of the words", split.Text == "Here is a house.\n\nIt has a door." && split.Blocks.Count == 1 && !split.Open);
        var table = Html.Extract("Your week:\n<figure aria-label=\"week\"><table><tr><td>Mon</td></tr></table><svg><g/></svg></figure>\nEnjoy.", finished: true);
        check("a figure is one block, whatever it holds", table.Text == "Your week:\n\nEnjoy." && table.Blocks.Count == 1 && table.Blocks[0].EndsWith("</figure>"));
        var fenced = Html.Extract("Look:\n```html\n<figure><p>x</p></figure>\n```\nDone", finished: true);
        check("code fences around a block are dropped", fenced.Text == "Look:\n\nDone" && fenced.Blocks.Count == 1);
        var streaming = Html.Extract("Here:\n<figure><table><tr><td>Mo", finished: false);
        check("an unfinished block is held back", streaming.Open && streaming.Text == "Here:" && streaming.Blocks.Count == 0);
        check("a possible start of a block is held back while streaming", Html.Extract("Here: <fig", finished: false).Text == "Here:" && Html.Extract("Here: <sv", finished: false).Text == "Here:" && Html.Extract("a < b", finished: false).Text == "a < b");
        var nested = Html.Extract("<figure><figure><p>x</p></figure></figure>after", finished: true);
        check("nested figures stay in one block", nested.Blocks.Count == 1 && nested.Text == "after");
        var parsed = ReplyParser.Parse("HEARD: draw a cat\n---\nA cat:\n<svg viewBox=\"0 0 4 3\"><path d=\"M0 0\"/></svg>", finished: true);
        check("the parser keeps blocks out of the cleaned words", parsed.Body == "A cat:" && parsed.Drawings is { Count: 1 } && parsed.Heard == "draw a cat");

        var bare = Html.Sanitize("<svg viewBox=\"0 0 4 3\"><title>A cat</title><path d=\"M0 0\" stroke=\"currentColor\"/></svg>");
        check("a bare drawing (as saved before) becomes a figure, kept as drawn", bare is not null && bare.StartsWith("<figure>") && bare.Contains("viewBox=\"0 0 4 3\"") && bare.Contains("stroke=\"currentColor\""));
        var unsafeBlock = Html.Sanitize("<figure><table><tr><td onclick=\"x()\" style=\"color:red;background:url(http://e/x.png)\">a</td></tr></table>"
            + "<script>alert(1)</script><img src=\"http://e/i.png\"><a href=\"http://e\">link</a><form><input></form><iframe src=\"http://e\"></iframe>"
            + "<style>@import url(http://e/s.css); td { background: url(//e/p.png) }</style><div style=\"background:url(//e/q.png)\">q</div>"
            + "<svg><image href=\"http://e/p.png\"/><use href=\"http://e/s.svg#a\"/></svg></figure>")!;
        check("scripts, handlers, images, links, forms and frames are taken out", !unsafeBlock.Contains("script") && !unsafeBlock.Contains("onclick") && !unsafeBlock.Contains("<img") && !unsafeBlock.Contains("href") && !unsafeBlock.Contains("<input") && !unsafeBlock.Contains("iframe") && !unsafeBlock.Contains("<image") && !unsafeBlock.Contains("<use"));
        check("nothing in it can be fetched", !unsafeBlock.Contains("http") && !unsafeBlock.Contains("//e/") && !unsafeBlock.Contains("@import"));
        check("what is safe stays: the table, its words and its colour", unsafeBlock.Contains("<td") && unsafeBlock.Contains(">a</td>") && unsafeBlock.Contains("color") && unsafeBlock.Contains(">link<") && unsafeBlock.Contains(">q<"));
        check("same-document references are fine", Html.Sanitize("<figure><svg><defs><marker id=\"a\"/></defs><path d=\"M0 0\" marker-end=\"url(#a)\"/></svg></figure>")!.Contains("url(#a)"));
        check("an empty, a huge or a non-figure block is refused", Html.Sanitize("<figure><script>x</script></figure>") is null && Html.Sanitize("<figure>" + new string('x', Html.MaxBytes) + "</figure>") is null && Html.Sanitize("<div>x</div>") is null);

        check("the title comes from the label, then the caption, then the drawing's title", Html.Title("<figure aria-label=\" Your  week \"><figcaption>c</figcaption></figure>") == "Your week"
            && Html.Title("<figure><figcaption>Prices</figcaption><table></table></figure>") == "Prices" && Html.Title("<figure><svg><title>A red house</title></svg></figure>") == "A red house"
            && Html.Title("<figure><table><tr><td>1</td></tr></table></figure>") == "a table");
        var asText = Html.Text("<figure><h3>Plan</h3><table><tr><th>Day</th><th>Task</th></tr><tr><td>Mon</td><td><b>gym</b> &amp; run</td></tr></table><ul><li>eggs</li><li>milk</li></ul><style>td{color:red}</style><svg><title>Map</title><text>x</text></svg></figure>");
        check("the text version reads like the block", asText == "Plan\n\nDay | Task\nMon | gym & run\n\n– eggs\n– milk\n\n[drawing: Map]");
        var doc = Html.Document("<figure><p>hi</p></figure>");
        check("the document lets nothing run or load, and has the ink to fill in", doc.Contains("default-src 'none'") && !doc.Contains("script-src") && doc.Contains(Html.InkToken) && doc.Contains("<p>hi</p>"));
        var (w, h) = Html.Fit(0.5, 560, 480);
        check("a tall block is limited in height, keeping its shape", Math.Abs(h - 480) < 1e-9 && Math.Abs(w - 240) < 1e-9 && Html.Fit(2, 560, 480) == (560, 280) && Html.Fit(0.1, 600).Height == Html.MaxHeight);
    }

    static IReadOnlyDictionary<string, JsonElement> Args(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
}
