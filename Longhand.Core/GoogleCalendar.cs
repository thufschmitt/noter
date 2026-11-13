using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Longhand.Services;

/// <summary>Where Google's sign-in and Calendar API are. The checks point these at a fake.</summary>
public sealed record GoogleEndpoints(string AuthorizeUrl, string TokenUrl, string RevokeUrl, string ApiBase)
{
    public static readonly GoogleEndpoints Default = new(
        "https://accounts.google.com/o/oauth2/v2/auth",
        "https://oauth2.googleapis.com/token",
        "https://oauth2.googleapis.com/revoke",
        "https://www.googleapis.com/calendar/v3");
}

/// <summary>
/// The sign-in to Google Calendar, with an OAuth client the user created in their own Google Cloud project (Google doesn't let apps
/// register themselves). Reading asks for the calendar list and events read-only; letting the model change events asks for the
/// scope that does. Tokens are traded and renewed by <see cref="McpOAuth"/>, which only needs the addresses and the client.
/// </summary>
public static class GoogleSignIn
{
    public const string ReadScopes = "https://www.googleapis.com/auth/calendar.calendarlist.readonly https://www.googleapis.com/auth/calendar.events.readonly";
    public const string WriteScopes = "https://www.googleapis.com/auth/calendar.calendarlist.readonly https://www.googleapis.com/auth/calendar.events";

    public static string Scopes(bool write) => write ? WriteScopes : ReadScopes;

    /// <summary>The sign-in page to send the user to (PKCE, and offline access so a refresh token comes back).</summary>
    public static SignInRequest Begin(OAuthClient client, string redirectUri, bool write, GoogleEndpoints? endpoints = null)
    {
        var ep = endpoints ?? GoogleEndpoints.Default;
        var verifier = McpOAuth.RandomUrlSafe(48);
        var challenge = McpOAuth.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = McpOAuth.RandomUrlSafe(24);
        var query = new (string Key, string Value)[]
        {
            ("response_type", "code"),
            ("client_id", client.ClientId),
            ("redirect_uri", redirectUri),
            ("scope", Scopes(write)),
            ("code_challenge", challenge),
            ("code_challenge_method", "S256"),
            ("state", state),
            ("access_type", "offline"),
            ("prompt", "consent"),
        };
        var url = new UriBuilder(ep.AuthorizeUrl) { Query = string.Join("&", query.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))) };
        return new SignInRequest(url.Uri, state, verifier, ep.ApiBase, client.ClientId, ep.TokenUrl, null, redirectUri, client.ClientSecret);
    }
}

/// <summary>
/// The user's Google Calendar sign-in: the OAuth client they made (its id with the settings, its secret and the tokens as secrets), whether
/// the model may change events, and a token that is renewed when it runs low.
/// </summary>
public sealed class GoogleCalendarBook
{
    const string ClientIdKey = "google-client-id", WriteKey = "google-write", TokenEndpointKey = "google-token-endpoint", ExpiresKey = "google-expires";
    const string SecretName = "google-client-secret", AccessName = "google-access", RefreshName = "google-refresh";
    const string CalendarsKey = "google-calendars";

    readonly ISettings _settings;
    readonly ISecrets _secrets;

    public GoogleCalendarBook(ISettings settings, ISecrets secrets)
    {
        _settings = settings;
        _secrets = secrets;
    }

    /// <summary>The client the user set up, or null.</summary>
    public OAuthClient? Client() =>
        _settings.Get(ClientIdKey) is { Length: > 0 } id ? new OAuthClient(id, Secret(SecretName)) : null;

    /// <summary>Whether the model may add, change and delete events (it was asked for when signing in).</summary>
    public bool CanWrite => _settings.Get(WriteKey) == "1";

    public bool Connected => Secret(AccessName) is not null;

    /// <summary>The calendars (by Google's id) the user chose for Longhand to use; null when no choice was made, meaning the ones switched on in Google Calendar.</summary>
    public IReadOnlyCollection<string>? Calendars() => CalendarChoice.Read(_settings, CalendarsKey);

    /// <summary>Keeps the choice of calendars; null goes back to the ones switched on in Google Calendar.</summary>
    public void SaveCalendars(IEnumerable<string>? ids) => CalendarChoice.Write(_settings, CalendarsKey, ids);

    /// <summary>
    /// Keeps the client and whether to allow changes. A sign-in made with another client, or without the scope for changes, no
    /// longer fits, so it is forgotten and the user connects again.
    /// </summary>
    public void SaveClient(OAuthClient client, bool write)
    {
        if (Client() != client || CanWrite != write) SignOut();
        _settings.Set(ClientIdKey, client.ClientId);
        _settings.Set(WriteKey, write ? "1" : "0");
        if (string.IsNullOrEmpty(client.ClientSecret)) _secrets.Remove(SecretName); else _secrets.Set(SecretName, client.ClientSecret);
    }

    public void SaveSession(OAuthSession session)
    {
        _settings.Set(TokenEndpointKey, session.TokenEndpoint);
        _settings.Set(ExpiresKey, session.ExpiresAt.ToString("O", CultureInfo.InvariantCulture));
        _secrets.Set(AccessName, session.AccessToken);
        if (session.RefreshToken is not null) _secrets.Set(RefreshName, session.RefreshToken);
    }

    /// <summary>The access token to send, renewed first when it is about to run out. Throws when it can't be renewed: the user connects again.</summary>
    public async Task<string> AccessTokenAsync(McpOAuth oauth, CancellationToken ct)
    {
        if (Secret(AccessName) is not { } access || Client() is not { } client || _settings.Get(TokenEndpointKey) is not { } endpoint)
            throw new InvalidOperationException("Google Calendar isn't connected. Connect it in the connected apps.");
        var expires = DateTimeOffset.TryParse(_settings.Get(ExpiresKey), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var e) ? e : DateTimeOffset.MinValue;
        var session = new OAuthSession(GoogleEndpoints.Default.ApiBase, client.ClientId, endpoint, null, access, Secret(RefreshName), expires, client.ClientSecret);
        if (!session.NeedsRefresh) return access;
        try
        {
            session = await oauth.RefreshAsync(session, ct);
            SaveSession(session);
            return session.AccessToken;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Google wouldn't renew the sign-in (" + ex.Message + "). Connect Google Calendar again in the connected apps.");
        }
    }

    /// <summary>The refresh token if there is one, else the access token: what to ask Google to revoke.</summary>
    public string? TokenToRevoke() => Secret(RefreshName) ?? Secret(AccessName);

    /// <summary>Forgets the sign-in, keeping the client, so connecting again is one tap.</summary>
    public void SignOut()
    {
        _settings.Remove(CalendarsKey); // another Google account has other calendars
        _secrets.Remove(AccessName);
        _secrets.Remove(RefreshName);
        _settings.Remove(TokenEndpointKey);
        _settings.Remove(ExpiresKey);
    }

    /// <summary>Forgets everything, the client too.</summary>
    public void Forget()
    {
        SignOut();
        _settings.Remove(ClientIdKey);
        _settings.Remove(WriteKey);
        _secrets.Remove(SecretName);
    }

    string? Secret(string name) => _secrets.Get(name) is { Length: > 0 } s ? s : null;
}

/// <summary>Google Calendar's own API (REST), called with the user's sign-in. An event is named <c>calendar|event</c>.</summary>
public sealed class GoogleCalendar
{
    public sealed record CalendarInfo(string Id, string Name, bool Writable, bool Primary, bool Selected);

    /// <summary>Whether a calendar is one to use: one of the <paramref name="only"/> the user chose, or, with no choice made, one switched on in Google Calendar (or the main one).</summary>
    public static bool IsUsed(CalendarInfo calendar, IReadOnlyCollection<string>? only) => only is null ? calendar.Selected || calendar.Primary : only.Contains(calendar.Id);

    /// <summary>An event as Google has it, with the version tag a change has to be made against.</summary>
    public sealed record Found(string CalendarId, string EventId, JsonObject Json, string? ETag, CalendarEvent Event);

    const int MaxCalendars = 25;
    static readonly Regex EventIdRule = new(@"^[A-Za-z0-9_\-]{1,1024}$", RegexOptions.Compiled);
    static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    readonly Func<CancellationToken, Task<string>> _token;
    readonly string _api;
    readonly HttpClient _http;

    /// <param name="token">Gives the access token to send, renewed when it needs to be.</param>
    public GoogleCalendar(Func<CancellationToken, Task<string>> token, GoogleEndpoints? endpoints = null, HttpClient? http = null)
    {
        _token = token;
        _api = (endpoints ?? GoogleEndpoints.Default).ApiBase.TrimEnd('/');
        _http = http ?? SharedHttp;
    }

    // ----- ids -----

    public static string MakeId(string calendarId, string eventId) => calendarId + "|" + eventId;

    /// <summary>Splits an id the model was given back into calendar and event; false when it isn't one.</summary>
    public static bool TryParseId(string? id, out string calendarId, out string eventId)
    {
        calendarId = eventId = "";
        if (id is null) return false;
        int bar = id.LastIndexOf('|');
        if (bar <= 0 || bar == id.Length - 1) return false;
        calendarId = id[..bar];
        eventId = id[(bar + 1)..];
        return calendarId.Length <= 300 && !calendarId.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '|' or '/' or '\\') && EventIdRule.IsMatch(eventId);
    }

    // ----- the API -----

    async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, string? etag, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, _api + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _token(ct));
        request.Headers.Accept.ParseAdd("application/json");
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try { return JsonNode.Parse(text); } catch (JsonException) { throw new InvalidOperationException("Google Calendar sent something that isn't JSON."); }
        }
        var why = ErrorMessage(text);
        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new InvalidOperationException("Google refused the sign-in" + (why.Length > 0 ? $" ({why})" : "") + ". Connect Google Calendar again in the connected apps."),
            HttpStatusCode.Forbidden => new InvalidOperationException("Google Calendar said no" + (why.Length > 0 ? $": {why}" : ".")),
            HttpStatusCode.NotFound or HttpStatusCode.Gone => new InvalidOperationException("That calendar or event doesn't exist (any more)."),
            HttpStatusCode.PreconditionFailed => new InvalidOperationException("That event was changed elsewhere since it was read. Read the calendar again and then retry."),
            HttpStatusCode.TooManyRequests => new InvalidOperationException("Google Calendar is limiting requests right now. Try again in a moment."),
            _ => new InvalidOperationException($"Google Calendar answered {(int)response.StatusCode}" + (why.Length > 0 ? $": {why}" : ".")),
        };
    }

    static string ErrorMessage(string body)
    {
        try
        {
            var message = (string?)JsonNode.Parse(body)?["error"]?["message"];
            if (!string.IsNullOrWhiteSpace(message)) body = message;
        }
        catch (JsonException) { }
        body = Regex.Replace(Regex.Replace(body, "<[^>]+>", " "), @"\s+", " ").Trim();
        return body.Length > 300 ? body[..300] + "…" : body;
    }

    static string Path(string calendarId, string? eventId = null) =>
        "/calendars/" + Uri.EscapeDataString(calendarId) + "/events" + (eventId is null ? "" : "/" + Uri.EscapeDataString(eventId));

    /// <summary>The calendars the user has (the ones they have switched on first).</summary>
    public async Task<List<CalendarInfo>> ListCalendarsAsync(CancellationToken ct)
    {
        var calendars = new List<CalendarInfo>();
        string? page = null;
        for (int i = 0; i < 4; i++)
        {
            var json = await SendAsync(HttpMethod.Get, "/users/me/calendarList?maxResults=250" + (page is null ? "" : "&pageToken=" + Uri.EscapeDataString(page)), null, null, ct);
            foreach (var item in json?["items"]?.AsArray() ?? new JsonArray())
            {
                var id = (string?)item?["id"];
                if (string.IsNullOrEmpty(id) || item?["deleted"]?.GetValue<bool>() == true) continue;
                var role = (string?)item["accessRole"] ?? "reader";
                var name = (string?)item["summaryOverride"] ?? (string?)item["summary"] ?? id;
                calendars.Add(new CalendarInfo(id, name, role is "owner" or "writer", item["primary"]?.GetValue<bool>() == true, item["selected"]?.GetValue<bool>() == true));
            }
            page = (string?)json?["nextPageToken"];
            if (string.IsNullOrEmpty(page)) break;
        }
        return calendars.OrderByDescending(c => c.Primary).ThenByDescending(c => c.Selected).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// The events between two moments across the calendars to use (<see cref="IsUsed"/>); repeating ones come as the single occurrences in the range.
    /// </summary>
    public async Task<List<CalendarEvent>> EventsAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken ct, IReadOnlyCollection<string>? only = null)
    {
        var calendars = (await ListCalendarsAsync(ct)).Where(c => IsUsed(c, only)).Take(MaxCalendars).ToList();
        var lists = await Task.WhenAll(calendars.Select(async calendar =>
        {
            var events = new List<CalendarEvent>();
            string? page = null;
            for (int i = 0; i < 4; i++)
            {
                var query = "?singleEvents=true&orderBy=startTime&maxResults=250"
                    + "&timeMin=" + Uri.EscapeDataString(start.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture))
                    + "&timeMax=" + Uri.EscapeDataString(end.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture))
                    + (page is null ? "" : "&pageToken=" + Uri.EscapeDataString(page));
                var json = await SendAsync(HttpMethod.Get, Path(calendar.Id) + query, null, null, ct);
                foreach (var item in json?["items"]?.AsArray() ?? new JsonArray())
                    if (item is JsonObject o && ToEvent(calendar.Name, calendar.Id, o) is { } e) events.Add(e);
                page = (string?)json?["nextPageToken"];
                if (string.IsNullOrEmpty(page)) break;
            }
            return events;
        }));
        return lists.SelectMany(l => l).ToList();
    }

    /// <summary>The event behind an id as it is now, with its version tag; null when it was deleted.</summary>
    public async Task<Found> GetEventAsync(string id, CancellationToken ct)
    {
        if (!TryParseId(id, out var calendarId, out var eventId)) throw new InvalidOperationException("id must be an event's id, as reading the calendar shows it ({id: …}).");
        var json = await SendAsync(HttpMethod.Get, Path(calendarId, eventId), null, null, ct) as JsonObject ?? throw new InvalidOperationException("That calendar or event doesn't exist (any more).");
        var name = (await ListCalendarsAsync(ct)).FirstOrDefault(c => c.Id == calendarId)?.Name ?? calendarId;
        var e = ToEvent(name, calendarId, json, keepCancelled: true) ?? throw new InvalidOperationException("That isn't an event Longhand can read.");
        if (json["status"]?.GetValue<string>() == "cancelled") throw new InvalidOperationException("That event was deleted.");
        return new Found(calendarId, eventId, json, (string?)json["etag"], e);
    }

    /// <summary>Adds an event; returns the id it was given.</summary>
    public async Task<string> InsertAsync(string calendarId, JsonObject body, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Post, Path(calendarId), body, null, ct);
        return MakeId(calendarId, (string?)json?["id"] ?? throw new InvalidOperationException("Google Calendar didn't say what the new event is called."));
    }

    public Task PatchAsync(string calendarId, string eventId, JsonObject body, string? etag, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, Path(calendarId, eventId), body, etag, ct);

    public Task DeleteAsync(string calendarId, string eventId, string? etag, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, Path(calendarId, eventId), null, etag, ct);

    /// <summary>Revokes a token with Google (the sign-in is gone on their side too).</summary>
    public static async Task RevokeAsync(string token, GoogleEndpoints? endpoints = null, HttpClient? http = null, CancellationToken ct = default)
    {
        using var response = await (http ?? SharedHttp).PostAsync((endpoints ?? GoogleEndpoints.Default).RevokeUrl,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }), ct);
    }

    // ----- reading an event -----

    /// <summary>An event as Longhand shows it; all-day events are dates (midnight UTC, end exclusive), as events from Nextcloud are. Null for a cancelled one.</summary>
    public static CalendarEvent? ToEvent(string calendar, string calendarId, JsonObject item, bool keepCancelled = false)
    {
        if (!keepCancelled && (string?)item["status"] == "cancelled") return null;
        if (!TryWhen(item["start"] as JsonObject, out var start, out var allDay) || !TryWhen(item["end"] as JsonObject, out var end, out _)) return null;
        if (end < start) end = start;
        var id = (string?)item["id"] ?? "";
        var title = ((string?)item["summary"])?.Trim() is { Length: > 0 } t ? t : "(no title)";
        bool repeats = item["recurringEventId"] is not null || item["recurrence"] is not null;
        return new CalendarEvent(calendar, title, start, end, allDay, ((string?)item["location"])?.Trim() ?? "", ((string?)item["description"])?.Trim() ?? "",
            id.Length > 0 ? MakeId(calendarId, id) : "", repeats);
    }

    static bool TryWhen(JsonObject? when, out DateTimeOffset time, out bool allDay)
    {
        time = default;
        allDay = false;
        if (when is null) return false;
        if ((string?)when["date"] is { } date && DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            time = new DateTimeOffset(day, TimeSpan.Zero);
            allDay = true;
            return true;
        }
        return (string?)when["dateTime"] is { } stamp && DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    }

    /// <summary>The start and end of an event, as Google wants them: dates for an all-day event (end exclusive), moments with their offset otherwise.</summary>
    public static (JsonObject Start, JsonObject End) WhenJson(EventWhen when)
    {
        if (when.AllDay)
            return (new JsonObject { ["date"] = when.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["dateTime"] = null },
                new JsonObject { ["date"] = when.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["dateTime"] = null });
        return (new JsonObject { ["dateTime"] = when.Start.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture), ["date"] = null },
            new JsonObject { ["dateTime"] = when.End.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture), ["date"] = null });
    }
}

/// <summary>What the Google Calendar tools share: the API, and reading an event back before it is changed.</summary>
public sealed class GoogleCalendarContext : CalendarClock
{
    public GoogleCalendar Google { get; }
    /// <summary>The calendars (by Google's id) the user chose to use; null when they haven't chosen, meaning the ones switched on in Google Calendar.</summary>
    public IReadOnlyCollection<string>? Only { get; }

    public GoogleCalendarContext(GoogleCalendar google, TimeZoneInfo zone, Func<DateTimeOffset> now, IReadOnlyCollection<string>? only = null) : base(zone, now)
    {
        Google = google;
        Only = only;
    }

    /// <summary>Refuses to go on when the user switched every calendar off.</summary>
    internal void RequireCalendars()
    {
        if (Only is { Count: 0 }) throw CalendarChoice.NoneChosen("Google");
    }

    /// <summary>
    /// The event as it is now. Google gives each occurrence of a repeating event an id of its own, and changing or deleting one of
    /// those affects only that occurrence, which is the default. With <paramref name="all"/> it is the repeating event itself, which
    /// is every occurrence that wasn't changed on its own.
    /// </summary>
    internal async Task<GoogleCalendar.Found> ReadEventAsync(string? id, bool all, CancellationToken ct)
    {
        if (!GoogleCalendar.TryParseId(id, out var calendarId, out _)) throw new InvalidOperationException("id must be an event's id, as reading the calendar shows it ({id: …}).");
        if (Only is not null && !Only.Contains(calendarId)) throw CalendarChoice.NotChosen();
        var found = await Google.GetEventAsync(id!, ct);
        if (!found.Event.Repeats) return found;
        var series = (string?)found.Json["recurringEventId"];
        if (all) return series is null ? found : await Google.GetEventAsync(GoogleCalendar.MakeId(calendarId, series), ct);
        if (series is null) throw new InvalidOperationException("That id is the repeating event itself, not one of its occurrences. Give the id of the occurrence, as reading the calendar shows it, or scope \"all\" for every occurrence.");
        return found;
    }
}

public static class GoogleCalendarTools
{
    /// <summary>Reading, and, when the user allowed it, adding, changing and deleting events.</summary>
    /// <param name="calendars">The calendars (by Google's id) the user chose for the tools to use; null for the ones switched on in Google Calendar.</param>
    public static IAgentTool[] For(GoogleCalendar google, bool canWrite, TimeZoneInfo? zone = null, Func<DateTimeOffset>? now = null, IReadOnlyCollection<string>? calendars = null)
    {
        var context = new GoogleCalendarContext(google, zone ?? TimeZoneInfo.Local, now ?? (() => DateTimeOffset.Now), calendars);
        var read = new GoogleCalendarEventsTool(context);
        return canWrite
            ? new IAgentTool[] { read, new GoogleCalendarAddTool(context), new GoogleCalendarChangeTool(context), new GoogleCalendarDeleteTool(context) }
            : new IAgentTool[] { read };
    }
}

/// <summary>Lets the model read the user's Google Calendar.</summary>
public sealed class GoogleCalendarEventsTool : IAgentTool
{
    public const string ToolName = "google_calendar_events";
    const int MaxDays = 93;
    readonly GoogleCalendarContext _c;

    public GoogleCalendarEventsTool(GoogleCalendarContext context) => _c = context;

    public string Name => ToolName;
    public string Status => "checking your Google Calendar…";

    public ToolSpec Definition => ToolSpec.Object(
        ToolName,
        "Reads the user's Google Calendar. Use it for questions about their schedule, availability, upcoming or past events, or " +
        "when something is on, and to find an event before changing or deleting it. Returns the events in the range across the " +
        "calendars they have chosen for it, grouped by day, each with its time, title, calendar, location, whether it repeats, and " +
        "its {id: …}. Times are in the user's own time zone. To show the events to the user, put them on a timeline in a layout " +
        "(see your instructions), not in a list of words. Ask for a short range (a day or a week) unless the question needs " +
        "more; the longest range is about three months.",
        new Dictionary<string, JsonElement>
        {
            ["start"] = CalendarContext.Json("""{"type":"string","description":"First day or moment of the range: a date like 2026-11-10 or a date-time like 2026-11-10T14:00, in the user's local time."}"""),
            ["end"] = CalendarContext.Json("""{"type":"string","description":"Last day or end of the range. A date alone (2026-11-12) includes that whole day; a date-time is the exclusive end. Defaults to a week after start."}"""),
            ["search"] = CalendarContext.Json("""{"type":"string","description":"Optional: only events whose title, location or description contain this text."}"""),
        },
        "start");

    public async Task<string> RunAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        if (!_c.TryTime(input, "start", out var start, out _)) throw new InvalidOperationException("start must be " + CalendarContext.TimeHelp + ".");
        DateTimeOffset end;
        if (input.ContainsKey("end"))
        {
            if (!_c.TryTime(input, "end", out end, out var dateOnly)) throw new InvalidOperationException("end must be " + CalendarContext.TimeHelp + ".");
            if (dateOnly) end = end.AddDays(1);
        }
        else end = start.AddDays(7);
        if (end <= start) throw new InvalidOperationException("end must be after start.");
        if (end - start > TimeSpan.FromDays(MaxDays)) end = start.AddDays(MaxDays);

        _c.RequireCalendars();
        var events = await _c.Google.EventsAsync(start, end, ct, _c.Only);
        return CalendarText.WithTimelineHint(CalendarText.Agenda(events, start, end, _c.Zone, CalendarContext.Text(input, "search")));
    }
}

/// <summary>Lets the model add an event to the user's Google Calendar.</summary>
public sealed class GoogleCalendarAddTool : IAgentTool
{
    public const string ToolName = "google_calendar_add_event";
    readonly GoogleCalendarContext _c;

    public GoogleCalendarAddTool(GoogleCalendarContext context) => _c = context;

    public string Name => ToolName;
    public string Status => "adding it to your Google Calendar…";

    public ToolSpec Definition => ToolSpec.Object(
        ToolName,
        "Adds an event to the user's Google Calendar. Use it only when the user asked for something to be put in their " +
        "calendar. Times are in the user's own time zone. A timed event lasts an hour unless an end is given; a date alone " +
        "makes an all-day event. Returns the event as added, with its id.",
        new Dictionary<string, JsonElement>
        {
            ["title"] = CalendarContext.Json("""{"type":"string","description":"What the event is, short, as it should appear in the calendar."}"""),
            ["start"] = CalendarContext.Json("""{"type":"string","description":"When it starts: a date-time like 2026-11-10T14:00, or a date like 2026-11-10 for an all-day event, in the user's local time."}"""),
            ["end"] = CalendarContext.Json("""{"type":"string","description":"Optional: when it ends (a date-time), or the last day of an all-day event (a date)."}"""),
            ["all_day"] = CalendarContext.Json("""{"type":"boolean","description":"Optional: true for an all-day event. A start given as a date alone already means all day."}"""),
            ["location"] = CalendarContext.Json("""{"type":"string","description":"Optional: where it is."}"""),
            ["description"] = CalendarContext.Json("""{"type":"string","description":"Optional: notes for the event."}"""),
            ["calendar"] = CalendarContext.Json("""{"type":"string","description":"Optional: the name of the calendar to add it to. Defaults to the user's own main calendar."}"""),
        },
        "title", "start");

    public async Task<string> RunAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        var title = CalendarContext.Text(input, "title");
        if (string.IsNullOrEmpty(title)) throw new InvalidOperationException("title is needed.");
        if (!input.ContainsKey("start")) throw new InvalidOperationException("start is needed.");
        var when = _c.When(input, old: null);

        _c.RequireCalendars();
        var calendars = (await _c.Google.ListCalendarsAsync(ct)).Where(c => c.Writable && (_c.Only is null || _c.Only.Contains(c.Id))).ToList();
        if (calendars.Count == 0) throw new InvalidOperationException("The user has no calendar that Longhand may add events to.");
        GoogleCalendar.CalendarInfo calendar;
        if (CalendarContext.Text(input, "calendar") is { Length: > 0 } wanted)
            calendar = calendars.FirstOrDefault(c => c.Name.Equals(wanted, StringComparison.CurrentCultureIgnoreCase))
                ?? throw new InvalidOperationException("There is no calendar called \"" + wanted + "\" that events can be added to. The calendars are: " + string.Join(", ", calendars.Select(c => c.Name)) + ".");
        else calendar = calendars.FirstOrDefault(c => c.Primary) ?? calendars[0];

        var (start, end) = GoogleCalendar.WhenJson(when);
        var body = new JsonObject { ["summary"] = title, ["start"] = start, ["end"] = end };
        if (CalendarContext.Text(input, "location") is { Length: > 0 } location) body["location"] = location;
        if (CalendarContext.Text(input, "description") is { Length: > 0 } description) body["description"] = description;
        var id = await _c.Google.InsertAsync(calendar.Id, body, ct);
        return "Added " + _c.Describe(title, when, calendar.Name, id) + ".";
    }
}

/// <summary>Lets the model change an event in the user's Google Calendar: its title, time, location or notes.</summary>
public sealed class GoogleCalendarChangeTool : IAgentTool
{
    public const string ToolName = "google_calendar_change_event";
    readonly GoogleCalendarContext _c;

    public GoogleCalendarChangeTool(GoogleCalendarContext context) => _c = context;

    public string Name => ToolName;
    public string Status => "changing your Google Calendar…";

    public ToolSpec Definition => ToolSpec.Object(
        ToolName,
        "Changes an event in the user's Google Calendar. Use it only when the user asked for that change, and only after " +
        "reading the calendar to find the event and its id; if it isn't clear which event they mean, ask instead. Give only " +
        "what changes: moving the start keeps the event's length unless an end is given. The id of a repeating event names one " +
        "occurrence: changing it (moving it, renaming it) changes only that occurrence. Returns the event as it is now.",
        new Dictionary<string, JsonElement>
        {
            ["id"] = CalendarContext.Json("""{"type":"string","description":"The event's id, as reading the calendar shows it ({id: …}); for a repeating event, the id of the occurrence."}"""),
            ["scope"] = CalendarContext.Json(CalendarContext.ScopeJson),
            ["title"] = CalendarContext.Json("""{"type":"string","description":"Optional: a new title."}"""),
            ["start"] = CalendarContext.Json("""{"type":"string","description":"Optional: a new start, a date-time like 2026-11-10T14:00 or a date for an all-day event, in the user's local time."}"""),
            ["end"] = CalendarContext.Json("""{"type":"string","description":"Optional: a new end (a date-time), or the last day of an all-day event (a date)."}"""),
            ["all_day"] = CalendarContext.Json("""{"type":"boolean","description":"Optional: make it all-day (true) or timed (false; then give a start time)."}"""),
            ["location"] = CalendarContext.Json("""{"type":"string","description":"Optional: a new location; an empty string removes it."}"""),
            ["description"] = CalendarContext.Json("""{"type":"string","description":"Optional: new notes; an empty string removes them."}"""),
        },
        "id");

    public async Task<string> RunAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        var id = CalendarContext.Text(input, "id");
        bool all = CalendarContext.AllOccurrences(input);
        var found = await _c.ReadEventAsync(id, all, ct);
        var old = found.Event;
        bool timeChanges = input.ContainsKey("start") || input.ContainsKey("end") || input.ContainsKey("all_day");
        if (all && old.Repeats && timeChanges) throw new InvalidOperationException("The times of every occurrence can't be changed from here. Change one occurrence (leave scope out), or tell the user to change the repeat in their calendar app.");
        var title = CalendarContext.Text(input, "title") is { Length: > 0 } t ? t : null;
        var when = timeChanges ? _c.When(input, old) : null;
        var location = CalendarContext.Text(input, "location");
        var description = CalendarContext.Text(input, "description");
        if (title is null && when is null && location is null && description is null) throw new InvalidOperationException("Nothing to change was given.");

        var body = new JsonObject();
        if (title is not null) body["summary"] = title;
        if (when is not null)
        {
            var (start, end) = GoogleCalendar.WhenJson(when);
            body["start"] = start;
            body["end"] = end;
        }
        if (location is not null) body["location"] = location.Length > 0 ? location : null;
        if (description is not null) body["description"] = description.Length > 0 ? description : null;

        await _c.Google.PatchAsync(found.CalendarId, found.EventId, body, found.ETag, ct);
        var shownId = all && old.Repeats ? GoogleCalendar.MakeId(found.CalendarId, found.EventId) : id!;
        return "Changed " + (!all && old.Repeats ? "that occurrence" : "it") + ". It is now " + _c.Describe(title ?? old.Title, when ?? new EventWhen(old.Start, old.End, old.AllDay), old.Calendar, shownId) + (all && old.Repeats ? " (and every occurrence that wasn't changed on its own)" : "") + ".";
    }
}

/// <summary>Lets the model delete an event from the user's Google Calendar.</summary>
public sealed class GoogleCalendarDeleteTool : IAgentTool
{
    public const string ToolName = "google_calendar_delete_event";
    readonly GoogleCalendarContext _c;

    public GoogleCalendarDeleteTool(GoogleCalendarContext context) => _c = context;

    public string Name => ToolName;
    public string Status => "removing it from your Google Calendar…";

    public ToolSpec Definition => ToolSpec.Object(
        ToolName,
        "Deletes an event from the user's Google Calendar. Use it only when the user clearly asked for that event to be " +
        "removed or cancelled, and only after reading the calendar to find it and its id; if it isn't clear which event they " +
        "mean, ask instead. The id of a repeating event names one occurrence: deleting it removes only that occurrence. Returns what was " +
        "deleted, so the reply can say so.",
        new Dictionary<string, JsonElement>
        {
            ["id"] = CalendarContext.Json("""{"type":"string","description":"The event's id, as reading the calendar shows it ({id: …}); for a repeating event, the id of the occurrence."}"""),
            ["scope"] = CalendarContext.Json(CalendarContext.ScopeJson),
        },
        "id");

    public async Task<string> RunAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        var id = CalendarContext.Text(input, "id");
        bool all = CalendarContext.AllOccurrences(input);
        var found = await _c.ReadEventAsync(id, all, ct);
        await _c.Google.DeleteAsync(found.CalendarId, found.EventId, found.ETag, ct);
        var old = found.Event;
        var shownId = all && old.Repeats ? GoogleCalendar.MakeId(found.CalendarId, found.EventId) : id!;
        return "Deleted " + (!all && old.Repeats ? "that occurrence of " : old.Repeats ? "every occurrence of " : "") + _c.Describe(old.Title, new EventWhen(old.Start, old.End, old.AllDay), old.Calendar, shownId) + ".";
    }
}
