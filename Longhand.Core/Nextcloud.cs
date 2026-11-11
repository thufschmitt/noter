using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Longhand.Services;

/// <summary>Where a Nextcloud account is and how to sign in to it: the server address, the login name and an app password.</summary>
public sealed record NextcloudAccount(string Server, string User, string Password);

public sealed record NextcloudLoginStart(Uri LoginUrl, Uri PollEndpoint, string PollToken);

/// <summary>A calendar: its name, its address on the server, and whether this account may add and change events in it.</summary>
public sealed record CalendarInfo(string Name, string Href, bool Writable = true)
{
    /// <summary>The last part of its address ("personal"), which with an event's file name makes the event's id.</summary>
    public string Segment => Href.TrimEnd('/').Split('/').Last();

    /// <summary>Its name as the connected-apps dialog lists it: one Claude may only read says so.</summary>
    public string Label => Writable ? Name : Name + " (read only)";
}

/// <summary>
/// Talks to a Nextcloud server: signing in through its browser login (Login Flow v2, which hands back an app password so
/// nothing has to be typed or pasted) and reading and writing calendar events over CalDAV.
/// </summary>
public sealed class Nextcloud
{
    const string UserAgent = "Longhand";

    static readonly XNamespace D = "DAV:";
    static readonly XNamespace C = "urn:ietf:params:xml:ns:caldav";

    readonly HttpClient _http;

    public Nextcloud(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>What the user typed ("cloud.example.com", "https://cloud.example.com/") as a server address, or null when it isn't usable.</summary>
    public static Uri? NormalizeServer(string? typed)
    {
        var text = (typed ?? "").Trim();
        if (text.Length == 0) return null;
        if (!text.Contains("://")) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        // The password travels in every request, so plain http is only for a server on this very PC.
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) return null;
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/index.php", StringComparison.OrdinalIgnoreCase)) path = path[..^"/index.php".Length];
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + path);
    }

    static Uri Under(string server, string path) => new(server.TrimEnd('/') + path);

    // ---------- sign-in (Login Flow v2) ----------

    public async Task<NextcloudLoginStart> BeginLoginAsync(string server, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Under(server, "/index.php/login/v2")) { Content = new StringContent("") };
        request.Headers.UserAgent.ParseAdd(UserAgent);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"That doesn't look like a Nextcloud server (it answered {(int)response.StatusCode}).");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        NextcloudLoginStart start;
        try
        {
            var poll = root.GetProperty("poll");
            start = new NextcloudLoginStart(
                new Uri(root.GetProperty("login").GetString()!),
                new Uri(poll.GetProperty("endpoint").GetString()!),
                poll.GetProperty("token").GetString()!);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or UriFormatException)
        {
            throw new InvalidOperationException("The server's sign-in answer wasn't what Nextcloud sends.");
        }
        // The page that is opened and the address polled must be as safe as the server address itself.
        if (NormalizeServer(start.LoginUrl.ToString()) is null || NormalizeServer(start.PollEndpoint.ToString()) is null)
            throw new InvalidOperationException("The server sent a sign-in address that isn't https.");
        return start;
    }

    /// <summary>Waits for the user to approve the sign-in in the browser. 404 means "not yet".</summary>
    public async Task<NextcloudAccount> WaitForLoginAsync(NextcloudLoginStart start, TimeSpan every, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Post, start.PollEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = start.PollToken }),
            };
            request.Headers.UserAgent.ParseAdd(UserAgent);
            using var response = await _http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var root = doc.RootElement;
                return new NextcloudAccount(
                    root.GetProperty("server").GetString()!.TrimEnd('/'),
                    root.GetProperty("loginName").GetString()!,
                    root.GetProperty("appPassword").GetString()!);
            }
            if (response.StatusCode != HttpStatusCode.NotFound)
                throw new InvalidOperationException($"Nextcloud refused the sign-in ({(int)response.StatusCode}).");
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The sign-in wasn't approved in the browser in time.");
            await Task.Delay(every, ct);
        }
    }

    /// <summary>Asks the server to forget this app password. Best effort: the caller forgets it locally either way.</summary>
    public async Task RevokeAsync(NextcloudAccount account, CancellationToken ct)
    {
        try
        {
            using var request = Authorized(HttpMethod.Delete, Under(account.Server, "/ocs/v2.php/core/apppassword"), account);
            request.Headers.Add("OCS-APIRequest", "true");
            using var response = await _http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Offline or unreachable: the password stays valid on the server until it is removed in Nextcloud's security settings.
        }
    }

    // ---------- calendars (CalDAV) ----------

    HttpRequestMessage Authorized(HttpMethod method, Uri uri, NextcloudAccount account)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{account.User}:{account.Password}")));
        request.Headers.UserAgent.ParseAdd(UserAgent);
        return request;
    }

    async Task<XDocument> DavAsync(NextcloudAccount account, string method, Uri uri, string depth, string body, CancellationToken ct)
    {
        using var request = Authorized(new HttpMethod(method), uri, account);
        request.Headers.Add("Depth", depth);
        request.Content = new StringContent(body, Encoding.UTF8, "application/xml");
        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("Nextcloud no longer accepts the saved sign-in. Connect to Nextcloud again in the connected apps.");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Nextcloud's calendar answered {(int)response.StatusCode}.");
        return XDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    public async Task<List<CalendarInfo>> ListCalendarsAsync(NextcloudAccount account, CancellationToken ct)
    {
        const string body = """
            <d:propfind xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
              <d:prop><d:displayname/><d:resourcetype/><c:supported-calendar-component-set/><d:current-user-privilege-set/></d:prop>
            </d:propfind>
            """;
        var home = Under(account.Server, "/remote.php/dav/calendars/" + Uri.EscapeDataString(account.User) + "/");
        var doc = await DavAsync(account, "PROPFIND", home, "1", body, ct);

        var calendars = new List<CalendarInfo>();
        foreach (var response in doc.Descendants(D + "response"))
        {
            var props = response.Descendants(D + "prop").FirstOrDefault();
            if (props is null || props.Element(D + "resourcetype")?.Element(C + "calendar") is null) continue; // inbox, outbox, trash...
            var components = props.Element(C + "supported-calendar-component-set")?.Elements(C + "comp").Select(e => (string?)e.Attribute("name")).ToList();
            if (components is { Count: > 0 } && !components.Contains("VEVENT")) continue; // task lists
            var href = (string?)response.Element(D + "href");
            if (href is null) continue;
            var name = (string?)props.Element(D + "displayname");
            // A calendar shared with this account read-only says so in its privileges; a server that doesn't say is trusted to refuse.
            var privileges = props.Element(D + "current-user-privilege-set")?.Descendants(D + "privilege").SelectMany(p => p.Elements()).Select(e => e.Name).ToList();
            bool writable = privileges is null || privileges.Count == 0 || privileges.Any(n => n == D + "all" || n == D + "write" || n == D + "write-content" || n == D + "bind");
            calendars.Add(new CalendarInfo(string.IsNullOrWhiteSpace(name) ? href.TrimEnd('/').Split('/').Last() : name, href, writable));
        }
        return calendars;
    }

    /// <summary>The events that overlap [start, end), repeating events expanded into their occurrences by the server.</summary>
    public async Task<List<CalendarEvent>> EventsAsync(NextcloudAccount account, DateTimeOffset start, DateTimeOffset end, TimeZoneInfo floatingZone, CancellationToken ct)
    {
        string Stamp(DateTimeOffset t) => t.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var body = $"""
            <c:calendar-query xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
              <d:prop><c:calendar-data><c:expand start="{Stamp(start)}" end="{Stamp(end)}"/></c:calendar-data></d:prop>
              <c:filter><c:comp-filter name="VCALENDAR"><c:comp-filter name="VEVENT"><c:time-range start="{Stamp(start)}" end="{Stamp(end)}"/></c:comp-filter></c:comp-filter></c:filter>
            </c:calendar-query>
            """;

        var events = new List<CalendarEvent>();
        foreach (var calendar in await ListCalendarsAsync(account, ct))
        {
            var doc = await DavAsync(account, "REPORT", new Uri(new Uri(account.Server.TrimEnd('/') + "/"), calendar.Href), "1", body, ct);
            foreach (var response in doc.Descendants(D + "response"))
            {
                var data = response.Descendants(C + "calendar-data").FirstOrDefault();
                if (data is null) continue;
                // The event's id is its calendar and file name ("personal/abc.ics"), as the server spells them.
                var href = ((string?)response.Element(D + "href") ?? "").TrimEnd('/');
                var id = calendar.Segment + "/" + href.Split('/').Last();
                foreach (var e in ICalendar.ParseEvents(data.Value, calendar.Name, floatingZone))
                    events.Add(e with { Id = IsEventId(id) ? id : "" });
            }
        }
        return events;
    }

    // ---------- single events ----------

    static readonly Regex EventId = new(@"^[A-Za-z0-9._~%@+=:,!$&'()*-]+/[A-Za-z0-9._~%@+=:,!$&'()*-]+\.ics$", RegexOptions.Compiled);

    /// <summary>Whether this is an event id as <see cref="EventsAsync"/> gives them: a calendar and a file name, nothing that climbs out.</summary>
    public static bool IsEventId(string? id) => id is not null && EventId.IsMatch(id) && !id.Split('/').Any(part => part is "." or "..");

    Uri EventUri(NextcloudAccount account, string id)
    {
        if (!IsEventId(id)) throw new InvalidOperationException("That isn't an event id from the calendar. Read the calendar to find the event first.");
        // The id is already spelled as the server's address spells it, so it is used as is.
        return new Uri(account.Server.TrimEnd('/') + "/remote.php/dav/calendars/" + Uri.EscapeDataString(account.User) + "/" + id);
    }

    async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await _http.SendAsync(request, ct);
        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
                response.Dispose();
                throw new InvalidOperationException("Nextcloud no longer accepts the saved sign-in. Connect to Nextcloud again in the connected apps.");
            case HttpStatusCode.Forbidden:
                response.Dispose();
                throw new InvalidOperationException("Nextcloud doesn't allow changing that calendar (it may be shared with the user read-only).");
            case HttpStatusCode.NotFound:
                response.Dispose();
                throw new InvalidOperationException("There is no such event in the calendar any more. Read the calendar again.");
            case HttpStatusCode.PreconditionFailed:
                response.Dispose();
                throw new InvalidOperationException("The event changed in the calendar since it was read, so nothing was changed. Read the calendar again.");
        }
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new InvalidOperationException($"Nextcloud's calendar answered {status}.");
        }
        return response;
    }

    /// <summary>The event's calendar data and its version tag (ETag), which a change or deletion must name.</summary>
    public async Task<(string Ics, string? ETag)> GetEventAsync(NextcloudAccount account, string id, CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Get, EventUri(account, id), account);
        using var response = await SendAsync(request, ct);
        return (await response.Content.ReadAsStringAsync(ct), response.Headers.ETag?.ToString());
    }

    /// <summary>
    /// Writes an event. With an ETag, only over that very version of it; without one, only where no event is yet, so nothing
    /// is ever overwritten unseen.
    /// </summary>
    public async Task PutEventAsync(NextcloudAccount account, string id, string ics, string? etag, CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Put, EventUri(account, id), account);
        request.Content = new StringContent(ics, Encoding.UTF8, "text/calendar");
        if (etag is null) request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        else request.Headers.TryAddWithoutValidation("If-Match", etag);
        using var response = await SendAsync(request, ct);
    }

    /// <summary>Deletes an event, only if it is still the version that was read.</summary>
    public async Task DeleteEventAsync(NextcloudAccount account, string id, string? etag, CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Delete, EventUri(account, id), account);
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        using var response = await SendAsync(request, ct);
    }
}
