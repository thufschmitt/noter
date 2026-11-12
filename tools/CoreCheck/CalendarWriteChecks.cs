using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Longhand.Services;

/// <summary>Checks for writing to the calendar: adding, changing and deleting events, safely, against a fake Nextcloud.</summary>
static class CalendarWriteChecks
{
    static readonly TimeZoneInfo Plus1 = TimeZoneInfo.CreateCustomTimeZone("test+1", TimeSpan.FromHours(1), "test+1", "test+1");

    static IReadOnlyDictionary<string, JsonElement> Args(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    static async Task<string?> Fails(Func<Task> action)
    {
        try { await action(); return null; }
        catch (InvalidOperationException ex) { return ex.Message; }
    }

    public static async Task RunAsync(Action<string, bool> check)
    {
        // ----- writing iCalendar -----
        var now = new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.FromHours(1));
        var made = ICalendar.NewEvent("u1@longhand", "Lunch, with Sam; then\\ coffee", new EventWhen(now.AddHours(3), now.AddHours(4), false), "Café", "Line 1\nLine 2", now);
        var back = ICalendar.ParseEvents(made, "Personal", Plus1).Single();
        check("a new event reads back as written, escapes and all", back.Title == "Lunch, with Sam; then\\ coffee" && back.Location == "Café" && back.Description == "Line 1\nLine 2" && back.Start == now.AddHours(3) && back.End == now.AddHours(4) && !back.AllDay);
        check("its lines end in CRLF and none is longer than 75 bytes", made.Contains("\r\n") && made.Split("\r\n").All(l => System.Text.Encoding.UTF8.GetByteCount(l) <= 75)
            && ICalendar.ParseEvents(ICalendar.NewEvent("u", new string('é', 120), new EventWhen(now, now.AddHours(1), false), null, null, now), "P", Plus1).Single().Title == new string('é', 120));
        var day = new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);
        var allDay = ICalendar.ParseEvents(ICalendar.NewEvent("u2", "Trip", new EventWhen(day, day.AddDays(2), true), null, null, now), "P", Plus1).Single();
        check("an all-day event keeps its dates", allDay.AllDay && allDay.Start == day && allDay.End == day.AddDays(2));

        const string withAlarm = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:x\r\nSEQUENCE:2\r\nDTSTART;TZID=Europe/Paris:20261111T100000\r\nDURATION:PT1H\r\nSUMMARY:Old\r\nLOCATION:Room 1\r\nCATEGORIES:Work\r\nBEGIN:VALARM\r\nACTION:DISPLAY\r\nDESCRIPTION:Soon\r\nTRIGGER:-PT10M\r\nEND:VALARM\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var changed = ICalendar.Change(withAlarm, new EventChange(Title: "New", When: new EventWhen(now.AddDays(1), now.AddDays(1).AddHours(2), false), Location: ""), now, out _)!;
        var reread = ICalendar.ParseEvents(changed, "P", Plus1).Single();
        check("a change sets what was given", reread.Title == "New" && reread.Start == now.AddDays(1) && reread.End == now.AddDays(1).AddHours(2) && reread.Location == "");
        check("and keeps the rest: alarm, categories, uid; the sequence goes up", changed.Contains("BEGIN:VALARM") && changed.Contains("DESCRIPTION:Soon") && changed.Contains("CATEGORIES:Work") && changed.Contains("UID:x") && changed.Contains("SEQUENCE:3") && !changed.Contains("DURATION") && !changed.Contains("TZID"));
        check("a repeating event isn't changed", ICalendar.Change(withAlarm.Replace("SUMMARY:Old", "RRULE:FREQ=WEEKLY\r\nSUMMARY:Old"), new EventChange(Title: "x"), now, out var why) is null && why!.Contains("repeating"));

        // ----- one occurrence of a repeating event -----
        const string weekly = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:w\r\nSEQUENCE:1\r\nDTSTART:20261111T080000Z\r\nDTEND:20261111T090000Z\r\nRRULE:FREQ=WEEKLY\r\nSUMMARY:Gym\r\nLOCATION:Pool\r\nBEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER:-PT10M\r\nEND:VALARM\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var third = new CalendarEvent("P", "Gym", new DateTimeOffset(2026, 11, 18, 8, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero), false, "Pool", "", Repeats: true);
        int Count(string text, string what) => System.Text.RegularExpressions.Regex.Matches(text, what).Count;
        check("a repeating calendar says so, a plain one doesn't", ICalendar.Repeats(weekly) && !ICalendar.Repeats(withAlarm));
        var movedOcc = ICalendar.ChangeOccurrence(weekly, third, new EventChange(When: new EventWhen(third.Start.AddHours(2), third.End.AddHours(2), false)), now, Plus1, out var problem)!;
        var parsed = ICalendar.ParseEvents(movedOcc, "P", Plus1);
        check("changing one occurrence adds an exception for it, and the event keeps repeating as it was", problem is null && parsed.Count == 2 && Count(movedOcc, "RRULE:FREQ=WEEKLY") == 1 && movedOcc.Contains("RECURRENCE-ID:20261118T080000Z")
            && parsed[0].Start == new DateTimeOffset(2026, 11, 11, 8, 0, 0, TimeSpan.Zero) && parsed[0].Title == "Gym" && Count(movedOcc, "UID:w") == 2);
        check("the exception is at its new time, with the rest of the event kept (place, alarm), and no rule of its own", parsed[1].Start == third.Start.AddHours(2) && parsed[1].End == third.End.AddHours(2) && parsed[1].Location == "Pool" && Count(movedOcc, "BEGIN:VALARM") == 2 && Count(movedOcc, "RRULE") == 1);
        var renamed = ICalendar.ChangeOccurrence(weekly, third, new EventChange(Title: "Swim"), now, Plus1, out _)!;
        var renamedEvents = ICalendar.ParseEvents(renamed, "P", Plus1);
        check("a change that isn't of the time leaves the occurrence where it was", renamedEvents[1].Title == "Swim" && renamedEvents[1].Start == third.Start && renamedEvents[0].Title == "Gym");
        var again = ICalendar.ChangeOccurrence(movedOcc, third with { Start = third.Start.AddHours(2), End = third.End.AddHours(2) }, new EventChange(Location: "Gym 2"), now, Plus1, out _)!;
        var againEvents = ICalendar.ParseEvents(again, "P", Plus1);
        check("an occurrence changed before is changed again, as the calendar shows it now, not added twice", againEvents.Count == 2 && againEvents[1].Location == "Gym 2" && againEvents[1].Start == third.Start.AddHours(2) && Count(again, "RECURRENCE-ID") == 1);
        var cancelled = ICalendar.DeleteOccurrence(movedOcc, third with { Start = third.Start.AddHours(2) }, now, Plus1, out _)!;
        check("deleting an occurrence leaves it out of the rule, and drops its exception", ICalendar.ParseEvents(cancelled, "P", Plus1).Count == 1 && cancelled.Contains("EXDATE:20261118T080000Z") && !cancelled.Contains("RECURRENCE-ID") && cancelled.Contains("RRULE:FREQ=WEEKLY"));
        var seriesRenamed = ICalendar.ChangeSeries(movedOcc, new EventChange(Title: "Pool time"), now, out _)!;
        var seriesEvents = ICalendar.ParseEvents(seriesRenamed, "P", Plus1);
        check("changing every occurrence changes the event itself, not the ones changed on their own", seriesEvents[0].Title == "Pool time" && seriesEvents[1].Title == "Gym" && Count(seriesRenamed, "RECURRENCE-ID") == 1);
        check("the time of every occurrence isn't changed that way, and a plain event isn't a repeating one", ICalendar.ChangeSeries(weekly, new EventChange(When: new EventWhen(now, now.AddHours(1), false)), now, out var whyNot) is null && whyNot!.Contains("time")
            && ICalendar.ChangeOccurrence(withAlarm, third, new EventChange(Title: "x"), now, Plus1, out var notRepeating) is null && notRepeating!.Contains("isn't a repeating")
            && ICalendar.DeleteOccurrence(withAlarm, third, now, Plus1, out _) is null);
        var wholeDay = new CalendarEvent("P", "Trip", new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero), true, "", "", Repeats: true);
        var dayEvents = ICalendar.ChangeOccurrence("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:t\r\nDTSTART;VALUE=DATE:20261111\r\nDTEND;VALUE=DATE:20261112\r\nRRULE:FREQ=WEEKLY\r\nSUMMARY:Trip\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n", wholeDay, new EventChange(Title: "Trip!"), now, Plus1, out _)!;
        check("an all-day occurrence is named by its date", dayEvents.Contains("RECURRENCE-ID;VALUE=DATE:20261118") && dayEvents.Contains("DTSTART;VALUE=DATE:20261118"));

        // ----- the tools, against a fake Nextcloud -----
        await using var server = new FakeNextcloud();
        var account = new NextcloudAccount(server.BaseUrl, FakeNextcloud.User, FakeNextcloud.Password);
        var tools = CalendarTools.For(new Nextcloud(), account, Plus1, () => now).ToDictionary(t => t.Name);
        check("reading, adding, changing and deleting are offered", tools.Keys.OrderBy(k => k).SequenceEqual(new[] { "calendar_add_event", "calendar_change_event", "calendar_delete_event", "calendar_events" }));

        var added = await tools["calendar_add_event"].RunAsync(Args("""{"title":"Dentist","start":"2026-11-12T15:00","location":"Main St"}"""), default);
        var (path, stored) = server.Events.Single();
        var ev = ICalendar.ParseEvents(stored.Ics, "Personal", Plus1).Single();
        check("an event is added to the default calendar, an hour long, in the user's time", path.StartsWith("personal/") && ev.Title == "Dentist" && ev.Start == new DateTimeOffset(2026, 11, 12, 15, 0, 0, TimeSpan.FromHours(1)) && ev.End == ev.Start.AddHours(1) && ev.Location == "Main St");
        check("adding never overwrites, and says what was added with its id", server.Requests.Single(q => q.Method == "PUT").Headers["If-None-Match"] == "*" && added.Contains("Thu 12 Nov 2026 15:00–16:00") && added.Contains("{id: " + path + "}"));
        await tools["calendar_add_event"].RunAsync(Args("""{"title":"Holiday","start":"2026-12-21","end":"2026-12-24"}"""), default);
        var holiday = server.Events.Values.Select(e => ICalendar.ParseEvents(e.Ics, "P", Plus1).Single()).Single(e => e.Title == "Holiday");
        check("a date alone makes an all-day event, through the last day given", holiday.AllDay && holiday.Start.Date == new DateTime(2026, 12, 21) && holiday.End.Date == new DateTime(2026, 12, 25));
        check("a read-only calendar can't be added to, and the reason names the ones that can", (await Fails(() => tools["calendar_add_event"].RunAsync(Args("""{"title":"x","start":"2026-11-12T10:00","calendar":"Team"}"""), default)))?.Contains("Personal") == true);
        check("a timed event needs a time", (await Fails(() => tools["calendar_add_event"].RunAsync(Args("""{"title":"x","start":"2026-11-12","all_day":false}"""), default))) is not null);

        var moved = await tools["calendar_change_event"].RunAsync(Args($$"""{"id":"{{path}}","start":"2026-11-13T09:30"}"""), default);
        ev = ICalendar.ParseEvents(server.Events[path].Ics, "Personal", Plus1).Single();
        check("moving an event keeps its length and the rest of it", ev.Start == new DateTimeOffset(2026, 11, 13, 9, 30, 0, TimeSpan.FromHours(1)) && ev.End == ev.Start.AddHours(1) && ev.Title == "Dentist" && ev.Location == "Main St" && moved.Contains("Fri 13 Nov 2026 09:30–10:30"));
        check("a change only goes over the version that was read", server.Requests.Last(q => q.Method == "PUT").Headers.TryGetValue("If-Match", out var ifMatch) && ifMatch.StartsWith("\"v"));

        // Someone edits it in between: the change is refused, nothing is lost.
        var stale = server.Events[path];
        server.Events[path] = (stale.Ics.Replace("SUMMARY:Dentist", "SUMMARY:Dentist (moved by phone)"), "\"v99\"");
        check("an event changed since it was read is left alone", (await Fails(() => new Nextcloud().PutEventAsync(account, path, "x", stale.ETag, default)))?.Contains("changed") == true && server.Events[path].ETag == "\"v99\"");

        check("an id that climbs out of the calendars is refused", (await Fails(() => tools["calendar_delete_event"].RunAsync(Args("""{"id":"../../../etc/x.ics"}"""), default))) is not null
            && (await Fails(() => tools["calendar_change_event"].RunAsync(Args("""{"id":"personal/../../x.ics","title":"y"}"""), default))) is not null && !Nextcloud.IsEventId("personal/x/y.ics"));
        server.Events["personal/weekly.ics"] = ("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:w\r\nDTSTART:20261111T080000Z\r\nDTEND:20261111T090000Z\r\nRRULE:FREQ=WEEKLY\r\nSUMMARY:Gym\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n", "\"w1\"");
        check("a repeating event is neither changed nor deleted", (await Fails(() => tools["calendar_delete_event"].RunAsync(Args("""{"id":"personal/weekly.ics"}"""), default)))?.Contains("repeating") == true && server.Events.ContainsKey("personal/weekly.ics"));
        server.Events["team_shared_by_bob/standup.ics"] = ("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:s\r\nDTSTART:20261111T080000Z\r\nDTEND:20261111T090000Z\r\nSUMMARY:Standup\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n", "\"s1\"");
        check("a calendar shared read-only is left alone", (await Fails(() => tools["calendar_delete_event"].RunAsync(Args("""{"id":"team_shared_by_bob/standup.ics"}"""), default)))?.Contains("read-only") == true && server.Events.ContainsKey("team_shared_by_bob/standup.ics"));
        var chosen = CalendarTools.For(new Nextcloud(), account, Plus1, () => now, new[] { "personal" }).ToDictionary(t => t.Name);
        check("an event in a calendar that wasn't chosen is neither changed nor deleted", (await Fails(() => chosen["calendar_delete_event"].RunAsync(Args("""{"id":"team_shared_by_bob/standup.ics"}"""), default)))?.Contains("hasn't chosen") == true
            && (await Fails(() => chosen["calendar_change_event"].RunAsync(Args("""{"id":"team_shared_by_bob/standup.ics","title":"x"}"""), default)))?.Contains("hasn't chosen") == true && server.Events.ContainsKey("team_shared_by_bob/standup.ics"));
        var other = CalendarTools.For(new Nextcloud(), account, Plus1, () => now, new[] { "team_shared_by_bob" }).ToDictionary(t => t.Name);
        check("an event isn't added to a calendar that wasn't chosen, even the default one", (await Fails(() => other["calendar_add_event"].RunAsync(Args("""{"title":"x","start":"2026-11-12T10:00"}"""), default)))?.Contains("no calendar that Longhand may add") == true);

        var deleted = await tools["calendar_delete_event"].RunAsync(Args($$"""{"id":"{{path}}"}"""), default);
        check("deleting removes it and says what it was", !server.Events.ContainsKey(path) && deleted.Contains("Dentist (moved by phone)") && server.Requests.Last(q => q.Method == "DELETE").Headers["If-Match"] == "\"v99\"");
        check("deleting it twice says it is gone", (await Fails(() => tools["calendar_delete_event"].RunAsync(Args($$"""{"id":"{{path}}"}"""), default)))?.Contains("no such event") == true);

        // Reading shows the ids that changing and deleting need.
        server.Ics = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:a\r\nDTSTART:20261111T080000Z\r\nDTEND:20261111T090000Z\r\nSUMMARY:Standup\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var agenda = await tools["calendar_events"].RunAsync(Args("""{"start":"2026-11-11","end":"2026-11-11"}"""), default);
        check("the agenda gives each event's id", agenda.Contains("Standup [Personal] {id: personal/a.ics}"));
    }
}
