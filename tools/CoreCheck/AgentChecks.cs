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
        var events = ICalendar.ParseEvents(Ics.Replace("\n", "\r\n"), "Personal", Plus1);
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
            check("only event calendars are listed", calendars.Count == 1 && calendars[0].Name == "Personal");

            server.Ics = Ics.Replace("\n", "\r\n");
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

        // ----- drawings in replies -----
        var split = Svg.Extract("Here is a house.\n<svg viewBox=\"0 0 40 30\"><rect width=\"10\" height=\"10\"/></svg>\nIt has a door.", finished: true);
        check("a drawing is taken out of the words", split.Text == "Here is a house.\n\nIt has a door." && split.Drawings.Count == 1 && !split.Open);
        var fenced = Svg.Extract("Look:\n```svg\n<svg viewBox=\"0 0 4 3\"></svg>\n```\nDone", finished: true);
        check("code fences around a drawing are dropped", fenced.Text == "Look:\n\nDone" && fenced.Drawings.Count == 1);
        var streaming = Svg.Extract("Here:\n<svg viewBox=\"0 0 4 3\"><circle", finished: false);
        check("an unfinished drawing is held back", streaming.Open && streaming.Text == "Here:" && streaming.Drawings.Count == 0);
        check("a possible start of a drawing is held back while streaming", Svg.Extract("Here: <sv", finished: false).Text == "Here:" && Svg.Extract("a < b", finished: false).Text == "a < b");
        var nested = Svg.Extract("<svg viewBox=\"0 0 4 3\"><svg><g/></svg></svg>after", finished: true);
        check("nested svg elements stay in one drawing", nested.Drawings.Count == 1 && nested.Text == "after");
        var parsed = ReplyParser.Parse("HEARD: draw a cat\n---\nA cat:\n<svg viewBox=\"0 0 4 3\"><path d=\"M0 0\"/></svg>", finished: true);
        check("the parser keeps drawings out of the cleaned words", parsed.Body == "A cat:" && parsed.Drawings is { Count: 1 } && parsed.Heard == "draw a cat");

        check("a drawing gets the namespace it needs", Svg.Sanitize("<svg viewBox=\"0 0 4 3\"><g/></svg>")!.Contains("xmlns=\"http://www.w3.org/2000/svg\""));
        check("scripts, external images and doctypes are refused",
            Svg.Sanitize("<svg><script>alert(1)</script></svg>") is null && Svg.Sanitize("<svg><image href=\"http://x/y.png\"/></svg>") is null
            && Svg.Sanitize("<svg><use href=\"http://x/y.svg#a\"/></svg>") is null && Svg.Sanitize("<!DOCTYPE svg><svg/>") is null && Svg.Sanitize("<svg onload=\"x()\"/>") is null);
        check("malformed or huge drawings are refused", Svg.Sanitize("<svg><g></svg>") is null && Svg.Sanitize("<svg>" + new string('x', Svg.MaxBytes) + "</svg>") is null);
        check("local references are fine", Svg.Sanitize("<svg viewBox=\"0 0 4 3\"><defs><path id=\"a\"/></defs><use href=\"#a\"/></svg>") is not null);
        check("the shape comes from the viewBox", Math.Abs(Svg.Aspect("<svg viewBox=\"0 0 400 200\"/>") - 2) < 1e-9 && Math.Abs(Svg.Aspect("<svg width=\"300px\" height=\"100\"/>") - 3) < 1e-9 && Math.Abs(Svg.Aspect("<svg/>") - 4.0 / 3) < 1e-9);
        check("the title describes it", Svg.Title("<svg><title> A  red\nhouse </title></svg>") == "A red house" && Svg.Title("<svg/>") == "a drawing");
        var (w, h) = Svg.Fit(0.5, 560, 480);
        check("a tall drawing is limited in height, keeping its shape", Math.Abs(h - 480) < 1e-9 && Math.Abs(w - 240) < 1e-9 && Svg.Fit(2, 560, 480) == (560, 280));
    }

    static IReadOnlyDictionary<string, JsonElement> Args(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
}
