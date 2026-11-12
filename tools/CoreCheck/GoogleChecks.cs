using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Longhand.Services;

/// <summary>Checks for Google Calendar through Google's own API: the sign-in, the book that renews it, and the tools, against a fake Google.</summary>
static class GoogleChecks
{
    static readonly TimeZoneInfo Plus1 = TimeZoneInfo.CreateCustomTimeZone("test+1", TimeSpan.FromHours(1), "test+1", "test+1");
    static IReadOnlyDictionary<string, JsonElement> Args(string json) => JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    static async Task<string?> Fails(Func<Task> action)
    {
        try { await action(); return null; }
        catch (InvalidOperationException ex) { return ex.Message; }
    }

    public static async Task RunAsync(Action<string, bool> check)
    {
        await using var google = new FakeGoogle();
        var client = new OAuthClient("gcid", "gsecret");

        // ----- the sign-in -----
        var read = GoogleSignIn.Begin(client, "http://127.0.0.1:5080/oauth/callback", write: false, google.Endpoints);
        var q = McpOAuth.ParseQuery(read.AuthorizeUrl.Query);
        check("signing in goes to Google's page with PKCE, the client, offline access and the read-only scopes", read.AuthorizeUrl.AbsolutePath == "/authorize" && q["client_id"] == "gcid" && q["code_challenge_method"] == "S256" && q["access_type"] == "offline" && q["prompt"] == "consent"
            && q["scope"] == GoogleSignIn.ReadScopes && !q["scope"].Contains("calendar.events ") && q["redirect_uri"] == "http://127.0.0.1:5080/oauth/callback" && !q.ContainsKey("resource"));
        var write = McpOAuth.ParseQuery(GoogleSignIn.Begin(client, "x", write: true, google.Endpoints).AuthorizeUrl.Query);
        check("letting the model change events asks for the scope that does", write["scope"] == GoogleSignIn.WriteScopes && write["scope"].EndsWith("/auth/calendar.events") && write["scope"].Contains("calendarlist.readonly"));

        // ----- the book -----
        var settings = new MemorySettings();
        var secrets = new MemorySecrets();
        var book = new GoogleCalendarBook(settings, secrets);
        var oauth = new McpOAuth();
        check("nothing is connected to begin with", book.Client() is null && !book.Connected && !book.CanWrite);
        book.SaveClient(client, write: true);
        check("the client is kept: its id with the settings, its secret as a secret", book.Client() == client && book.CanWrite && secrets.Get("google-client-secret") == "gsecret" && !settings.All().Any(v => v.Value.Contains("gsecret")));
        var session = await oauth.FinishAsync(read, "?code=abc&state=" + Uri.EscapeDataString(read.State), default);
        check("the code is traded with the client's secret", session.AccessToken == "access-g1" && session.RefreshToken == "refresh-g1" && google.LastTokenForm["client_secret"] == "gsecret" && google.LastTokenForm["client_id"] == "gcid");
        book.SaveSession(session);
        check("a connected book gives the token, and keeps the tokens as secrets", book.Connected && await book.AccessTokenAsync(oauth, default) == "access-g1" && secrets.Get("google-access") == "access-g1" && secrets.Get("google-refresh") == "refresh-g1");
        book.SaveSession(session with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        check("a token about to run out is renewed, with the secret, and the renewal is kept", await book.AccessTokenAsync(oauth, default) == "access-g2" && google.LastTokenForm["grant_type"] == "refresh_token" && google.LastTokenForm["client_secret"] == "gsecret" && secrets.Get("google-access") == "access-g2" && secrets.Get("google-refresh") == "refresh-g1");
        book.SaveSession(session with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1), RefreshToken = "revoked" });
        var lapsed = await Fails(() => book.AccessTokenAsync(oauth, default));
        check("a sign-in that can't be renewed says to connect again", lapsed is not null && lapsed.Contains("Connect Google Calendar again"));
        book.SaveSession(session);
        book.SaveClient(client, write: false);
        check("allowing less (or another client) forgets the sign-in, which no longer fits", !book.Connected && book.Client() == client && !book.CanWrite);
        book.SaveSession(session);
        book.SignOut();
        check("signing out keeps the client", !book.Connected && book.Client() == client && book.TokenToRevoke() is null);
        book.SaveSession(session);
        check("a token to revoke is the refresh token", book.TokenToRevoke() == "refresh-g1");
        var tools = new GoogleCalendar(ct => book.AccessTokenAsync(oauth, ct), google.Endpoints);

        // ----- reading -----
        var now = new DateTimeOffset(2026, 11, 11, 8, 0, 0, TimeSpan.FromHours(1));
        var readOnly = GoogleCalendarTools.For(tools, canWrite: false, Plus1, () => now);
        check("with no permission to change, only reading is offered", readOnly.Select(t => t.Name).SequenceEqual(new[] { "google_calendar_events" }));
        var all = GoogleCalendarTools.For(tools, canWrite: true, Plus1, () => now);
        check("with it, adding, changing and deleting are offered too", all.Select(t => t.Name).SequenceEqual(new[] { "google_calendar_events", "google_calendar_add_event", "google_calendar_change_event", "google_calendar_delete_event" }));
        var events = all[0];
        var agenda = await events.RunAsync(Args("""{"start":"2026-11-11","end":"2026-11-11"}"""), default);
        check("the agenda has the day's events from the calendars switched on, with time, calendar and location", agenda.Contains("09:00–09:30 Standup [Personal]") && agenda.Contains("14:00–15:00 Planning [Team] @ Room 2") && agenda.Contains("11:00–12:00 Weekly sync [Personal]"));
        check("a calendar that isn't switched on isn't shown", !agenda.Contains("Armistice") && !agenda.Contains("Holidays"));
        check("each event has an id that names its calendar, and a repeating one says so", agenda.Contains("{id: me@example.com|e1}") && agenda.Contains("Weekly sync [Personal] (repeats) {id: me@example.com|e3_20261111T100000Z}"));
        var week = await events.RunAsync(Args("""{"start":"2026-11-19","end":"2026-11-23"}"""), default);
        check("an all-day event is shown with its days", week.Contains("all day until Sat 21 Nov Trip") || week.Contains("all day until") && week.Contains("Trip"));
        check("the agenda ends with a reminder to show the events on a timeline, and the tool says so too", agenda.EndsWith("Don't list them in words.)") && events.Definition.Description.Contains("put them on a timeline")
            && !(await events.RunAsync(Args("""{"start":"2030-01-01","end":"2030-01-01"}"""), default)).Contains("timeline"));
        check("the search narrows it", !(await events.RunAsync(Args("""{"start":"2026-11-11","end":"2026-11-11","search":"plan"}"""), default)).Contains("Standup"));
        check("every request carried the token, and the time range was asked for", google.Requests.Where(r => r.Path.Contains("/events")).All(r => r.Headers["Authorization"].StartsWith("Bearer access-g")) && google.Requests.Any(r => r.Query.Contains("singleEvents=true") && r.Query.Contains("timeMin=2026-11-11T00%3A00%3A00%2B01%3A00")));

        // ----- adding -----
        var add = all[1];
        var added = await add.RunAsync(Args("""{"title":"Dentist","start":"2026-11-13T15:00","location":"Rue 1","description":"Bring papers"}"""), default);
        var post = google.Writes.Last();
        check("an event is added to the main calendar, an hour long, in the user's zone", post.Method == "POST" && post.Path.EndsWith("/calendars/" + FakeGoogle.Primary + "/events") && (string?)post.Body!["summary"] == "Dentist" && (string?)post.Body["start"]!["dateTime"] == "2026-11-13T15:00:00+01:00" && (string?)post.Body["end"]!["dateTime"] == "2026-11-13T16:00:00+01:00" && (string?)post.Body["location"] == "Rue 1" && (string?)post.Body["description"] == "Bring papers");
        check("it says what was added, with an id to find it again", added.StartsWith("Added \"Dentist\"") && added.Contains("[Personal]") && added.Contains("{id: me@example.com|new4}"));
        await add.RunAsync(Args("""{"title":"Away","start":"2026-12-01","end":"2026-12-03","calendar":"team"}"""), default);
        var day = google.Writes.Last();
        check("a date makes an all-day event on the calendar asked for, the end day included", day.Path.EndsWith("/calendars/" + FakeGoogle.Team + "/events") && (string?)day.Body!["start"]!["date"] == "2026-12-01" && (string?)day.Body["end"]!["date"] == "2026-12-04");
        var unknown = await Fails(() => add.RunAsync(Args("""{"title":"x","start":"2026-12-01","calendar":"Holidays"}"""), default));
        check("a calendar that can't be added to is refused, naming the ones that can", unknown is not null && unknown.Contains("Personal, Team"));

        // ----- changing -----
        var change = all[2];
        var changed = await change.RunAsync(Args("""{"id":"me@example.com|e1","title":"Standup (moved)","start":"2026-11-11T09:15","location":""}"""), default);
        var patch = google.Writes.Last();
        check("a change is sent as a patch of only what changed, against the version that was read", patch.Method == "PATCH" && patch.IfMatch is not null && (string?)patch.Body!["summary"] == "Standup (moved)" && (string?)patch.Body["start"]!["dateTime"] == "2026-11-11T09:15:00+01:00" && (string?)patch.Body["end"]!["dateTime"] == "2026-11-11T09:45:00+01:00" && patch.Body.AsObject().ContainsKey("location") && patch.Body["location"] is null && !patch.Body.AsObject().ContainsKey("description"));
        check("moving the start keeps the length, and it reads back as it is now", changed.Contains("It is now \"Standup (moved)\"") && changed.Contains("09:15–09:45"));
        var after = await events.RunAsync(Args("""{"start":"2026-11-11","end":"2026-11-11"}"""), default);
        check("and the calendar shows it", after.Contains("09:15–09:45 Standup (moved) [Personal]"));
        check("a repeating event isn't changed or deleted", (await Fails(() => change.RunAsync(Args("""{"id":"me@example.com|e3_20261111T100000Z","title":"x"}"""), default)))?.Contains("repeating") == true
            && (await Fails(() => all[3].RunAsync(Args("""{"id":"me@example.com|e3_20261111T100000Z"}"""), default)))?.Contains("repeating") == true);
        check("an id that isn't one is refused", (await Fails(() => change.RunAsync(Args("""{"id":"e1","title":"x"}"""), default)))?.Contains("id must be") == true && (await Fails(() => change.RunAsync(Args("""{"id":"../x|e1","title":"x"}"""), default)))?.Contains("id must be") == true);
        check("nothing to change is refused, and an event that isn't there is said not to be", (await Fails(() => change.RunAsync(Args("""{"id":"me@example.com|e1"}"""), default)))?.Contains("Nothing to change") == true
            && (await Fails(() => change.RunAsync(Args("""{"id":"me@example.com|zzz","title":"x"}"""), default)))?.Contains("doesn't exist") == true);
        // The event was changed elsewhere between reading it and changing it.
        var staleRead = await tools.GetEventAsync("team@example.com|e4", default);
        google.Events[FakeGoogle.Team][0]["etag"] = "\"elsewhere\"";
        var stale = await Fails(async () =>
        {
            var ctx = new GoogleCalendarContext(tools, Plus1, () => now);
            await ctx.Google.PatchAsync(staleRead.CalendarId, staleRead.EventId, new JsonObject { ["summary"] = "x" }, staleRead.ETag, default);
        });
        check("an event changed elsewhere in the meantime is left alone", stale is not null && stale.Contains("changed elsewhere"));

        // ----- deleting -----
        var deleted = await all[3].RunAsync(Args("""{"id":"me@example.com|e2"}"""), default);
        var del = google.Writes.Last();
        check("a delete is made against the version that was read, and says what went", del.Method == "DELETE" && del.IfMatch is not null && deleted.StartsWith("Deleted \"Trip\"") && !google.Events[FakeGoogle.Primary].Any(e => (string?)e["id"] == "e2"));

        // ----- choosing the calendars -----
        check("no calendars are chosen to begin with", book.Calendars() is null);
        var listed = await tools.ListCalendarsAsync(default);
        check("with no choice the calendars in use are the ones switched on in Google Calendar; with one, exactly the chosen",
            listed.Where(c => GoogleCalendar.IsUsed(c, null)).Select(c => c.Name).SequenceEqual(new[] { "Personal", "Team" })
            && listed.Where(c => GoogleCalendar.IsUsed(c, new[] { FakeGoogle.Holidays })).Select(c => c.Name).SequenceEqual(new[] { "Holidays" }));
        book.SaveCalendars(new[] { FakeGoogle.Team, FakeGoogle.Holidays, FakeGoogle.Team });
        check("a choice is kept with the settings, once each, and read back by another book", new GoogleCalendarBook(settings, secrets).Calendars()!.OrderBy(c => c).SequenceEqual(new[] { FakeGoogle.Holidays, FakeGoogle.Team }.OrderBy(c => c)));
        var picked = GoogleCalendarTools.For(tools, canWrite: true, Plus1, () => now, book.Calendars());
        var pickedAgenda = await picked[0].RunAsync(Args("""{"start":"2026-11-11","end":"2026-11-11"}"""), default);
        check("only the chosen calendars are read, even one Google has switched off", pickedAgenda.Contains("Planning [Team]") && pickedAgenda.Contains("Armistice") && !pickedAgenda.Contains("[Personal]"));
        await picked[1].RunAsync(Args("""{"title":"Chosen","start":"2026-11-14T10:00"}"""), default);
        check("an event is added to a chosen calendar, never to the main one when that wasn't chosen", google.Writes.Last().Path.EndsWith("/calendars/" + FakeGoogle.Team + "/events"));
        var notChosen = await Fails(() => picked[1].RunAsync(Args("""{"title":"x","start":"2026-11-14T10:00","calendar":"Personal"}"""), default));
        check("naming a calendar that wasn't chosen is refused, naming the ones that were", notChosen is not null && notChosen.EndsWith("The calendars are: Team."));
        var writes = google.Writes.Count;
        check("an event in a calendar that wasn't chosen is neither changed nor deleted", (await Fails(() => picked[2].RunAsync(Args("""{"id":"me@example.com|e1","title":"x"}"""), default)))?.Contains("hasn't chosen") == true
            && (await Fails(() => picked[3].RunAsync(Args("""{"id":"me@example.com|e1"}"""), default)))?.Contains("hasn't chosen") == true && google.Writes.Count == writes);
        var switchedOff = GoogleCalendarTools.For(tools, canWrite: true, Plus1, () => now, Array.Empty<string>());
        check("with every calendar switched off the model is told so, for reading and for adding", (await Fails(() => switchedOff[0].RunAsync(Args("""{"start":"2026-11-11"}"""), default)))?.Contains("hasn't chosen any Google calendar") == true
            && (await Fails(() => switchedOff[1].RunAsync(Args("""{"title":"x","start":"2026-11-14T10:00"}"""), default)))?.Contains("hasn't chosen any Google calendar") == true);
        book.SignOut();
        check("signing out forgets the choice, as another account has other calendars", book.Calendars() is null);

        // ----- errors -----
        var refused = await Fails(() => new GoogleCalendar(_ => Task.FromResult("nope"), google.Endpoints).ListCalendarsAsync(default));
        check("a token Google doesn't accept says why, and to connect again", refused is not null && refused.Contains("Invalid Credentials") && refused.Contains("Connect Google Calendar again"));
        check("a revoked token is sent to Google", await Revoked(google));
    }

    static async Task<bool> Revoked(FakeGoogle google)
    {
        await GoogleCalendar.RevokeAsync("tok-1", google.Endpoints);
        return google.Revoked == "token=tok-1";
    }
}
