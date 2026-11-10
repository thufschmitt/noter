using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Longhand.Services;

/// <summary>Where a Nextcloud account is and how to sign in to it: the server address, the login name and an app password.</summary>
public sealed record NextcloudAccount(string Server, string User, string Password);

public sealed record NextcloudLoginStart(Uri LoginUrl, Uri PollEndpoint, string PollToken);

public sealed record CalendarInfo(string Name, string Href);

/// <summary>
/// Talks to a Nextcloud server: signing in through its browser login (Login Flow v2, which hands back an app password so
/// nothing has to be typed or pasted) and reading calendars over CalDAV. Read-only.
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
              <d:prop><d:displayname/><d:resourcetype/><c:supported-calendar-component-set/></d:prop>
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
            calendars.Add(new CalendarInfo(string.IsNullOrWhiteSpace(name) ? href.TrimEnd('/').Split('/').Last() : name, href));
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
            foreach (var data in doc.Descendants(C + "calendar-data"))
                events.AddRange(ICalendar.ParseEvents(data.Value, calendar.Name, floatingZone));
        }
        return events;
    }
}
