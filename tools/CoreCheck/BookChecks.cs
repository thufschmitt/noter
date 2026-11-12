using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Longhand.Services;

sealed class MemorySettings : ISettings
{
    readonly Dictionary<string, string> _d = new();
    public IReadOnlyList<KeyValuePair<string, string>> All() => _d.ToList();
    public string? Get(string key) => _d.TryGetValue(key, out var v) ? v : null;
    public void Set(string key, string value) => _d[key] = value;
    public void Remove(string key) => _d.Remove(key);
}

sealed class MemorySecrets : ISecrets
{
    readonly Dictionary<string, string> _d = new();
    public string? Get(string name) => _d.TryGetValue(name, out var v) ? v : null;
    public void Set(string name, string secret) => _d[name] = secret;
    public void Remove(string name) => _d.Remove(name);
}

/// <summary>Checks for the connected-apps and Nextcloud books that the Windows app and the web server share.</summary>
static class BookChecks
{
    public static async Task RunAsync(Action<string, bool> check)
    {
        var book = new ConnectorBook(new MemorySettings(), new MemorySecrets());
        check("there are no connected apps until some are added", book.List().Count == 0);

        book.Add("notes", "https://notes.example/mcp", AppAccess.Open);
        book.Add("keyed", "https://keyed.example/mcp", AppAccess.Token);
        book.Add("signin", "https://signin.example/mcp");
        var apps = book.List().ToDictionary(a => a.Name);
        check("apps are listed by name with how they let you in", book.List().Select(a => a.Name).SequenceEqual(new[] { "keyed", "notes", "signin" }) && apps["notes"].Access == AppAccess.Open && apps["keyed"].Access == AppAccess.Token && apps["signin"].Access == AppAccess.SignIn);
        check("an open app is ready, the others wait for a token or a sign-in", apps["notes"].Ready && !apps["keyed"].Ready && !apps["signin"].Ready);
        check("names are made unique", book.UniqueName("fresh") == "fresh" && book.UniqueName("NOTES") == "NOTES-2" && book.UniqueName("notes") == "notes-2");

        book.SaveToken("keyed", "  tok-123 ");
        check("a saved token makes a token app ready", book.List().First(a => a.Name == "keyed").Ready);
        var (active, expired) = await book.ActiveAsync(new McpOAuth(), default);
        check("ready apps are offered to Claude with their token (none for an open one)", active.Count == 2 && active.Single(c => c.Name == "keyed").Token == "tok-123" && active.Single(c => c.Name == "notes").Token is null && expired.Count == 0);

        await using var fake = new FakeOAuthServer();
        var oauth = new McpOAuth();
        var request = await oauth.BeginAsync(fake.BaseUrl + "/mcp", McpOAuth.RedirectUri, default);
        var session = await oauth.FinishAsync(request, "?code=abc&state=" + Uri.EscapeDataString(request.State), default);
        book.SaveSession("signin", session);
        check("a signed-in app is ready", book.List().First(a => a.Name == "signin").Ready);
        (active, expired) = await book.ActiveAsync(oauth, default);
        check("and offered with its access token", active.Single(c => c.Name == "signin").Token == "access-1");

        book.SaveSession("signin", session with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5) });
        (active, expired) = await book.ActiveAsync(oauth, default);
        check("a token that is about to expire is renewed on the way", active.Single(c => c.Name == "signin").Token == "access-2");
        (active, _) = await book.ActiveAsync(oauth, default);
        check("and the renewal is kept", active.Single(c => c.Name == "signin").Token == "access-2");

        book.SaveSession("signin", session with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5), RefreshToken = "revoked" });
        (active, expired) = await book.ActiveAsync(oauth, default);
        check("an app whose sign-in can't be renewed is left out and named", !active.Any(c => c.Name == "signin") && expired.SequenceEqual(new[] { "signin" }));

        book.SignOut("keyed");
        check("signing out takes the token away", !book.List().First(a => a.Name == "keyed").Ready && book.List().Any(a => a.Name == "keyed"));
        book.Remove("notes");
        check("removing an app forgets it", book.List().Select(a => a.Name).SequenceEqual(new[] { "keyed", "signin" }));

        // ----- a server that doesn't register clients (Google's Calendar): the user's own client, its secret, offline access -----
        var gSettings = new MemorySettings();
        var gSecrets = new MemorySecrets();
        var gBook = new ConnectorBook(gSettings, gSecrets);
        var gUrl = fake.BaseUrl + "/gcal";
        var plain = false;
        try { await oauth.BeginAsync(gUrl, McpOAuth.RedirectUri, default); } catch (InvalidOperationException ex) { plain = ex.Message.Contains("client ID"); }
        check("without a client of its own such a server is refused, saying what to add", plain && fake.Registrations == 1);
        check("a server that answers the handshake to anyone but publishes its sign-in is a sign-in (Google's does), and an open one stays open", await oauth.InspectAsync(gUrl, default) == McpAccess.SignIn && await oauth.InspectAsync(fake.BaseUrl + "/open", default) == McpAccess.Open);
        var own = new OAuthClient("gcid", "gsecret", "scope-a scope-b");
        gBook.Add("google-calendar", gUrl, AppAccess.SignIn, own);
        check("the client is kept: its id and scope with the app, its secret as a secret", gBook.ClientFor("google-calendar") == own && gBook.List().Single().OwnClient && !gSettings.All().Any(v => v.Value.Contains("gsecret")) && gSecrets.Get("mcp-client-secret:google-calendar") == "gsecret");
        check("an app that registers itself has no client of its own", gBook.ClientFor("nothing") is null && book.ClientFor("signin") is null && !book.List().First(a => a.Name == "signin").OwnClient);
        var gRequest = await oauth.BeginAsync(gUrl, McpOAuth.RedirectUri, default, gBook.ClientFor("google-calendar"));
        var gq = McpOAuth.ParseQuery(gRequest.AuthorizeUrl.Query);
        check("signing in uses that client and registers nothing", gq["client_id"] == "gcid" && fake.Registrations == 1 && gRequest.AuthorizeUrl.AbsolutePath == "/gauthorize" && gq["kept"] == "1");
        check("it asks for the scopes the user gave, not the server's", gq["scope"] == "scope-a scope-b");
        check("and for offline access, which Google only gives to those who ask like this", gq["access_type"] == "offline" && gq["prompt"] == "consent" && !gq["scope"].Contains("offline_access"));
        check("an ordinary server that offers the offline scope isn't sent those", !McpOAuth.ParseQuery(request.AuthorizeUrl.Query).ContainsKey("access_type"));
        var gSession = await oauth.FinishAsync(gRequest, "?code=abc&state=" + Uri.EscapeDataString(gRequest.State), default);
        check("the code is traded with the client's secret", gSession.AccessToken == "access-g1" && gSession.RefreshToken == "refresh-g1" && fake.LastGoogleForm["client_secret"] == "gsecret" && fake.LastGoogleForm["client_id"] == "gcid");
        gBook.SaveSession("google-calendar", gSession);
        check("signing in keeps the client", gBook.ClientFor("google-calendar") == own && gBook.List().Single().Ready);
        gBook.SaveSession("google-calendar", gSession with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5) });
        (active, expired) = await gBook.ActiveAsync(oauth, default);
        check("renewing sends the secret too, and keeps the refresh token", active.Single().Token == "access-g2" && fake.LastGoogleForm["client_secret"] == "gsecret" && fake.LastGoogleForm["grant_type"] == "refresh_token" && expired.Count == 0 && gSecrets.Get("mcp-refresh:google-calendar") == "refresh-g1");
        gBook.SignOut("google-calendar");
        check("signing out keeps the client, so signing in again is one tap", gBook.ClientFor("google-calendar") == own && !gBook.List().Single().Ready);
        gBook.Remove("google-calendar");
        check("removing the app forgets the secret", gSecrets.Get("mcp-client-secret:google-calendar") is null && gBook.List().Count == 0);

        var settings = new MemorySettings();
        var secrets = new MemorySecrets();
        var cloud = new NextcloudBook(settings, secrets);
        check("no Nextcloud account until one is saved", cloud.Load() is null);
        cloud.Save(new NextcloudAccount("https://cloud.example", "me", "app-pass"));
        var again = new NextcloudBook(settings, secrets).Load();
        check("a Nextcloud account survives saving and loading", again == new NextcloudAccount("https://cloud.example", "me", "app-pass"));
        check("no Nextcloud calendars are chosen to begin with", cloud.Calendars() is null);
        cloud.SaveCalendars(new[] { "team", "personal", "team" });
        check("a choice of calendars is kept, once each, and read back by another book", new NextcloudBook(settings, secrets).Calendars()!.OrderBy(c => c).SequenceEqual(new[] { "personal", "team" }));
        cloud.SaveCalendars(Array.Empty<string>());
        check("choosing none is a choice too", cloud.Calendars() is { Count: 0 });
        cloud.SaveCalendars(null);
        check("a null choice goes back to using all of them", cloud.Calendars() is null);
        cloud.SaveCalendars(new[] { "personal" });
        cloud.Clear();
        check("clearing forgets the account and its password", cloud.Load() is null && secrets.Get("nextcloud-app-password") is null && settings.All().Count == 0);
    }
}
