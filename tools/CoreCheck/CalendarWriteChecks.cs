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

        var deleted = await tools["calendar_delete_event"].RunAsync(Args($$"""{"id":"{{path}}"}"""), default);
        check("deleting removes it and says what it was", !server.Events.ContainsKey(path) && deleted.Contains("Dentist (moved by phone)") && server.Requests.Last(q => q.Method == "DELETE").Headers["If-Match"] == "\"v99\"");
        check("deleting it twice says it is gone", (await Fails(() => tools["calendar_delete_event"].RunAsync(Args($$"""{"id":"{{path}}"}"""), default)))?.Contains("no such event") == true);

        // Reading shows the ids that changing and deleting need.
        server.Ics = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:a\r\nDTSTART:20261111T080000Z\r\nDTEND:20261111T090000Z\r\nSUMMARY:Standup\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var agenda = await tools["calendar_events"].RunAsync(Args("""{"start":"2026-11-11","end":"2026-11-11"}"""), default);
        check("the agenda gives each event's id", agenda.Contains("Standup [Personal] {id: personal/a.ics}"));
    }
}
