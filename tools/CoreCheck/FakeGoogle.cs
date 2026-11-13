using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Longhand.Services;

/// <summary>Enough of Google's sign-in and Calendar API for the Google Calendar checks: calendars, events with version tags, and a client secret on every token request.</summary>
sealed class FakeGoogle : FakeServer
{
    public const string Primary = "me@example.com", Team = "team@example.com", Holidays = "holidays#x@group.v.calendar.google.com";

    public Dictionary<string, string> LastTokenForm = new();
    public string? Revoked;
    public readonly Dictionary<string, List<JsonObject>> Events = new();
    public readonly List<(string Method, string Path, JsonNode? Body, string? IfMatch)> Writes = new();
    int _version = 1;

    public GoogleEndpoints Endpoints => new(BaseUrl + "/authorize", BaseUrl + "/token", BaseUrl + "/revoke", BaseUrl + "/calendar/v3");

    public FakeGoogle()
    {
        Events[Primary] = new()
        {
            Ev("e1", "Standup", "2026-11-11T09:00:00+01:00", "2026-11-11T09:30:00+01:00"),
            Day("e2", "Trip", "2026-11-20", "2026-11-22"),
            Ev("e3_20261111T100000Z", "Weekly sync", "2026-11-11T11:00:00+01:00", "2026-11-11T12:00:00+01:00", ("recurringEventId", "e3")),
        };
        // The repeating event itself, a week before the occurrence listed above (a real calendar gives it back by its own id).
        var master = Ev("e3", "Weekly sync", "2026-10-28T11:00:00+01:00", "2026-10-28T12:00:00+01:00");
        master["recurrence"] = new JsonArray("RRULE:FREQ=WEEKLY");
        Events[Primary].Add(master);
        Events[Team] = new() { Ev("e4", "Planning", "2026-11-11T14:00:00+01:00", "2026-11-11T15:00:00+01:00", ("location", "Room 2")) };
        Events[Holidays] = new() { Day("h1", "Armistice", "2026-11-11", "2026-11-12") };
    }

    JsonObject Ev(string id, string summary, string start, string end, params (string, string)[] more)
    {
        var o = new JsonObject { ["id"] = id, ["status"] = "confirmed", ["summary"] = summary, ["etag"] = "\"v" + _version++ + "\"", ["start"] = new JsonObject { ["dateTime"] = start }, ["end"] = new JsonObject { ["dateTime"] = end } };
        foreach (var (k, v) in more) o[k] = v;
        return o;
    }

    JsonObject Day(string id, string summary, string start, string end) =>
        new() { ["id"] = id, ["status"] = "confirmed", ["summary"] = summary, ["etag"] = "\"v" + _version++ + "\"", ["start"] = new JsonObject { ["date"] = start }, ["end"] = new JsonObject { ["date"] = end } };

    static JsonObject Cal(string id, string name, string role, bool primary, bool selected) =>
        new() { ["id"] = id, ["summary"] = name, ["accessRole"] = role, ["primary"] = primary, ["selected"] = selected };

    static void Merge(JsonObject into, JsonObject patch)
    {
        foreach (var (key, value) in patch.ToList())
        {
            if (value is null) into.Remove(key);
            else if (value is JsonObject sub && into[key] is JsonObject existing) { Merge(existing, sub); }
            else into[key] = value.DeepClone();
        }
    }

    static async Task Json(HttpListenerResponse response, int status, JsonNode body) => await Write(response, status, "application/json", body.ToJsonString());

    protected override async Task Handle(Request r, HttpListenerResponse response)
    {
        if (r.Method == "POST" && r.Path == "/token")
        {
            var form = r.Body.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
            LastTokenForm = form;
            if (form.GetValueOrDefault("client_id") != "gcid" || form.GetValueOrDefault("client_secret") != "gsecret") { await Json(response, 401, new JsonObject { ["error"] = "invalid_client" }); return; }
            if (form["grant_type"] == "authorization_code") await Json(response, 200, new JsonObject { ["access_token"] = "access-g1", ["refresh_token"] = "refresh-g1", ["expires_in"] = 3599 });
            else if (form["grant_type"] == "refresh_token" && form["refresh_token"] == "refresh-g1") await Json(response, 200, new JsonObject { ["access_token"] = "access-g2", ["expires_in"] = 3599 });
            else await Json(response, 400, new JsonObject { ["error"] = "invalid_grant" });
            return;
        }
        if (r.Method == "POST" && r.Path == "/revoke")
        {
            Revoked = r.Body;
            await Write(response, 200, "application/json", "{}");
            return;
        }
        if (!r.Headers.TryGetValue("Authorization", out var auth) || auth is not ("Bearer access-g1" or "Bearer access-g2"))
        {
            await Json(response, 401, new JsonObject { ["error"] = new JsonObject { ["message"] = "Invalid Credentials", ["code"] = 401 } });
            return;
        }
        var path = Uri.UnescapeDataString(r.Path);
        const string root = "/calendar/v3";
        if (r.Method == "GET" && path == root + "/users/me/calendarList")
        {
            await Json(response, 200, new JsonObject
            {
                ["items"] = new JsonArray(Cal(Primary, "Personal", "owner", true, true), Cal(Team, "Team", "writer", false, true), Cal(Holidays, "Holidays", "reader", false, false)),
            });
            return;
        }
        var parts = path[(root.Length + 1)..].Split('/');
        if (parts.Length < 3 || parts[0] != "calendars" || parts[2] != "events" || !Events.TryGetValue(parts[1], out var list)) { await Json(response, 404, new JsonObject { ["error"] = new JsonObject { ["message"] = "Not Found" } }); return; }
        var calendar = parts[1];
        if (parts.Length == 3 && r.Method == "GET")
        {
            var q = System.Web.HttpUtility.ParseQueryString(r.Query);
            var from = DateTimeOffset.Parse(q["timeMin"]!, System.Globalization.CultureInfo.InvariantCulture);
            var to = DateTimeOffset.Parse(q["timeMax"]!, System.Globalization.CultureInfo.InvariantCulture);
            DateTimeOffset At(JsonNode? w) => (string?)w?["dateTime"] is { } t ? DateTimeOffset.Parse(t, System.Globalization.CultureInfo.InvariantCulture) : new DateTimeOffset(DateTime.Parse((string)w!["date"]!, System.Globalization.CultureInfo.InvariantCulture), TimeSpan.Zero);
            await Json(response, 200, new JsonObject { ["items"] = new JsonArray(list.Where(e => At(e["start"]) < to && At(e["end"]) > from).Select(e => (JsonNode)e.DeepClone()).ToArray()) });
            return;
        }
        if (parts.Length == 3 && r.Method == "POST")
        {
            var body = JsonNode.Parse(r.Body)!.AsObject();
            Writes.Add((r.Method, path, body, null));
            var made = new JsonObject { ["id"] = "new" + (list.Count + 1), ["status"] = "confirmed", ["etag"] = "\"v" + _version++ + "\"" };
            Merge(made, body);
            list.Add(made);
            await Json(response, 200, made);
            return;
        }
        var existingEvent = parts.Length == 4 ? list.FirstOrDefault(e => (string?)e["id"] == parts[3]) : null;
        if (existingEvent is null) { await Json(response, 404, new JsonObject { ["error"] = new JsonObject { ["message"] = "Not Found" } }); return; }
        if (r.Method == "GET") { await Json(response, 200, existingEvent); return; }
        r.Headers.TryGetValue("If-Match", out var ifMatch);
        Writes.Add((r.Method, path, string.IsNullOrEmpty(r.Body) ? null : JsonNode.Parse(r.Body), ifMatch));
        if (ifMatch is not null && ifMatch != (string?)existingEvent["etag"]) { await Json(response, 412, new JsonObject { ["error"] = new JsonObject { ["message"] = "Precondition Failed" } }); return; }
        if (r.Method == "PATCH")
        {
            Merge(existingEvent, JsonNode.Parse(r.Body)!.AsObject());
            existingEvent["etag"] = "\"v" + _version++ + "\"";
            await Json(response, 200, existingEvent);
        }
        else if (r.Method == "DELETE")
        {
            list.Remove(existingEvent);
            response.StatusCode = 204;
        }
    }
}

