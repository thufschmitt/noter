using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Longhand.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Longhand.Web;

/// <summary>
/// Connected apps for the web version: MCP servers (added from the registry or by address, signed in through the browser), and the
/// Nextcloud calendar. The same shared logic as the Windows app (<see cref="ConnectorBook"/>, <see cref="McpOAuth"/>,
/// <see cref="McpRegistry"/>, <see cref="Nextcloud"/>); only how the browser is sent to sign in, and where secrets are kept, differ.
/// </summary>
public sealed class WebApps
{
    /// <summary>A sign-in that was started and is waiting for the browser to come back to /oauth/callback.</summary>
    sealed record Pending(string Name, SignInRequest Request, DateTimeOffset Started, bool Google = false);

    sealed class NextcloudLogin
    {
        public required CancellationTokenSource Cts;
        public volatile bool Done;
        public volatile string? Error;
    }

    static readonly TimeSpan SignInWindow = TimeSpan.FromMinutes(10);

    readonly WebStore _store;
    readonly McpOAuth _oauth = new();
    readonly McpRegistry _registry;
    readonly Nextcloud _nextcloud = new();
    readonly ConcurrentDictionary<string, Pending> _pending = new();
    readonly CancellationToken _stopping;
    NextcloudLogin? _login;

    public WebApps(WebStore store, CancellationToken stopping)
    {
        _store = store;
        _stopping = stopping;
        _registry = new McpRegistry(baseUrl: store.Options.RegistryBaseUrl);
    }

    public Nextcloud NextcloudClient => _nextcloud;

    GoogleEndpoints GoogleEp => _store.Options.GoogleEndpoints ?? GoogleEndpoints.Default;

    /// <summary>Google Calendar, called with the user's sign-in (renewed when it runs low).</summary>
    public GoogleCalendar GoogleClient => new(ct => _store.Google.AccessTokenAsync(_oauth, ct), GoogleEp);

    object GoogleStatus() => new
    {
        clientId = _store.Google.Client()?.ClientId,
        hasSecret = !string.IsNullOrEmpty(_store.Google.Client()?.ClientSecret),
        canWrite = _store.Google.CanWrite,
        connected = _store.Google.Connected,
    };

    /// <summary>The apps ready for a request (tokens close to expiring are renewed), and the ones whose sign-in has lapsed.</summary>
    public Task<(System.Collections.Generic.List<Connector> Active, System.Collections.Generic.List<string> Expired)> ActiveAsync(CancellationToken ct) =>
        _store.Connectors.ActiveAsync(_oauth, ct);

    bool Allowed(string url) => McpRegistry.IsPublicHttps(url)
        || (_store.Options.AllowPrivateApps && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https");

    static object Shape(ConnectedApp a) => new { name = a.Name, url = a.Url, access = a.Access, ready = a.Ready, ownClient = a.OwnClient };

    object NextcloudStatus()
    {
        var account = _store.Nextcloud.Load();
        var login = _login;
        return new
        {
            connected = account is not null,
            host = account is null ? null : new Uri(account.Server).Host,
            user = account?.User,
            pending = login is { Done: false },
            error = login?.Error,
        };
    }

    /// <summary>A choice is nothing (go back to the default) or calendars the account has.</summary>
    static bool ValidChoice(string[]? ids, System.Collections.Generic.HashSet<string> known) => ids is null || (ids.Length <= 200 && ids.All(known.Contains));

    /// <summary>Google's calendars, each with whether the model uses it (the user's choice, else Google's own switches).</summary>
    async Task<object> GoogleCalendarsAsync(CancellationToken ct)
    {
        var chosen = _store.Google.Calendars();
        var calendars = await GoogleClient.ListCalendarsAsync(ct);
        return new { custom = chosen is not null, calendars = calendars.Select(c => new { id = c.Id, name = c.Name, writable = c.Writable, used = GoogleCalendar.IsUsed(c, chosen) }) };
    }

    async Task<object> NextcloudCalendarsAsync(NextcloudAccount account, CancellationToken ct)
    {
        var chosen = _store.Nextcloud.Calendars();
        var calendars = await _nextcloud.ListCalendarsAsync(account, ct);
        return new { custom = chosen is not null, calendars = calendars.Select(c => new { id = c.Segment, name = c.Name, writable = c.Writable, used = chosen is null || chosen.Contains(c.Segment) }) };
    }

    public void Map(WebApplication app)
    {
        app.MapGet("/api/apps", () => new { apps = _store.Connectors.List().Select(Shape), nextcloud = NextcloudStatus(), google = GoogleStatus() });

        app.MapPost("/api/apps", async (HttpContext ctx, AddApp body) =>
        {
            var name = McpRegistry.Slug(body.Name ?? "");
            var url = (body.Url ?? "").Trim();
            if (string.IsNullOrWhiteSpace(body.Name) || !Allowed(url))
                return Results.BadRequest(new { error = "An app needs a name and an https address that is reachable from the internet." });
            McpAccess found;
            try { found = await _oauth.InspectAsync(url, ctx.RequestAborted); }
            catch (OperationCanceledException) { return Results.StatusCode(499); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
            var access = found switch
            {
                McpAccess.SignIn => AppAccess.SignIn,
                McpAccess.Token => AppAccess.Token,
                _ => body.TokenRequired == true ? AppAccess.Token : AppAccess.Open,
            };
            // An OAuth client the user registered with the service themselves (for services that don't let apps register).
            OAuthClient? client = null;
            if (!string.IsNullOrWhiteSpace(body.ClientId))
            {
                // A client ID says the user knows it signs in, even if the server didn't say so to the probe (a server may answer anyone until a tool is called).
                if (access == AppAccess.Open) access = AppAccess.SignIn;
                if (access != AppAccess.SignIn) return Results.BadRequest(new { error = "That server doesn't use a browser sign-in, so a client ID is of no use." });
                var id = body.ClientId.Trim();
                if (id.Length > 300 || id.Any(char.IsWhiteSpace) || (body.ClientSecret ?? "").Length > 300 || (body.Scope ?? "").Length > 1000)
                    return Results.BadRequest(new { error = "That client ID, secret or scope doesn't look right." });
                client = new OAuthClient(id, string.IsNullOrWhiteSpace(body.ClientSecret) ? null : body.ClientSecret.Trim(), body.Scope);
            }
            var unique = _store.Connectors.UniqueName(name);
            _store.Connectors.Add(unique, url, access, client);
            return Results.Ok(new { app = Shape(_store.Connectors.List().First(a => a.Name == unique)) });
        });

        app.MapPost("/api/apps/{name}/token", (string name, TokenBody body) =>
        {
            var token = (body.Token ?? "").Trim();
            if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = token[7..].Trim();
            if (token.Length == 0 || _store.Connectors.List().FirstOrDefault(a => a.Name == name) is not { Access: AppAccess.Token })
                return Results.BadRequest(new { error = "That app doesn't take a token." });
            _store.Connectors.SaveToken(name, token);
            return Results.Ok();
        });

        app.MapPost("/api/apps/{name}/signout", (string name) =>
        {
            _store.Connectors.SignOut(name);
            return Results.Ok();
        });

        app.MapDelete("/api/apps/{name}", (string name) =>
        {
            _store.Connectors.Remove(name);
            return Results.Ok();
        });

        // Starts a browser sign-in: the page opens the returned address; the service sends the browser back to /oauth/callback.
        app.MapPost("/api/apps/{name}/signin", async (HttpContext ctx, string name) =>
        {
            if (_store.Connectors.List().FirstOrDefault(a => a.Name == name) is not { Access: AppAccess.SignIn } found)
                return Results.BadRequest(new { error = "That app doesn't use a browser sign-in." });
            try
            {
                var redirect = $"{ctx.Request.Scheme}://{ctx.Request.Host}/oauth/callback";
                var request = await _oauth.BeginAsync(found.Url, redirect, ctx.RequestAborted, _store.Connectors.ClientFor(name));
                foreach (var old in _pending.Where(p => DateTimeOffset.UtcNow - p.Value.Started > SignInWindow).Select(p => p.Key)) _pending.TryRemove(old, out _);
                _pending[request.State] = new Pending(name, request, DateTimeOffset.UtcNow);
                return Results.Ok(new { url = request.AuthorizeUrl.ToString() });
            }
            catch (OperationCanceledException) { return Results.StatusCode(499); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/oauth/callback", async (HttpContext ctx) =>
        {
            var query = ctx.Request.QueryString.Value ?? "";
            string title, message;
            int status = 200;
            if (McpOAuth.ParseQuery(query).TryGetValue("state", out var state) && _pending.TryRemove(state, out var pending)
                && DateTimeOffset.UtcNow - pending.Started <= SignInWindow)
            {
                try
                {
                    var session = await _oauth.FinishAsync(pending.Request, query, ctx.RequestAborted);
                    if (pending.Google) _store.Google.SaveSession(session);
                    else _store.Connectors.SaveSession(pending.Name, session);
                    (title, message) = pending.Google
                        ? ("Connected", "Longhand is now connected to your Google Calendar" + (_store.Google.CanWrite ? "" : " (read-only)") + ". You can close this tab.")
                        : ("Signed in", $"Longhand is now signed in to {pending.Name}. You can close this tab.");
                }
                catch (Exception ex)
                {
                    (status, title, message) = (400, "Couldn't sign in", ex.Message);
                }
            }
            else
            {
                (status, title, message) = (400, "Couldn't sign in", "This sign-in wasn't started here, or it took too long. Start it again from Longhand.");
            }
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-store";
            Func<string, string> h = WebUtility.HtmlEncode!;
            await ctx.Response.WriteAsync($"<!doctype html><meta charset=utf-8><meta name=viewport content=\"width=device-width\"><title>{h(title)}</title>"
                + "<body style=\"font:16px system-ui;max-width:30em;margin:4em auto;padding:0 1em\">"
                + $"<h2>{h(title)}</h2><p>{h(message)}</p><script>try{{opener&&opener.postMessage('longhand-oauth','{ctx.Request.Scheme}://{ctx.Request.Host}')}}catch(e){{}}</script>");
        });

        // ----- the MCP registry -----

        app.MapGet("/api/registry", async (HttpContext ctx, string? q, string? cursor) =>
        {
            try
            {
                var page = await _registry.SearchAsync(q, cursor, ctx.RequestAborted);
                return Results.Ok(new
                {
                    servers = page.Servers.Select(s => new
                    {
                        name = s.Name,
                        title = s.Title,
                        description = s.Description,
                        website = s.Website,
                        host = s.Host,
                        url = s.Remote.Url,
                        tokenRequired = s.Remote.TokenRequired,
                        variables = s.Remote.Variables.Select(v => new { name = v.Name, description = v.Description, @default = v.Default, choices = v.Choices, required = v.Required }),
                    }),
                    hidden = page.Hidden,
                    nextCursor = page.NextCursor,
                });
            }
            catch (OperationCanceledException) { return Results.StatusCode(499); }
            catch (Exception ex) { return Results.Json(new { error = "Couldn't search the registry: " + ex.Message }, statusCode: 502); }
        });

        // ----- Google Calendar: through Google's own API, with an OAuth client the user made in their own Google Cloud project -----

        app.MapGet("/api/google", () => GoogleStatus());

        app.MapPut("/api/google/client", (GoogleClientBody body) =>
        {
            var id = (body.ClientId ?? "").Trim();
            if (id.Length == 0 || id.Length > 300 || id.Any(char.IsWhiteSpace) || (body.ClientSecret ?? "").Length > 300)
                return Results.BadRequest(new { error = "Paste the client ID of the OAuth client you made in Google Cloud (it ends in .apps.googleusercontent.com)." });
            // A secret left blank next to the same client ID is the one already kept.
            var kept = _store.Google.Client() is { } current && current.ClientId == id ? current.ClientSecret : null;
            var secret = string.IsNullOrWhiteSpace(body.ClientSecret) ? kept : body.ClientSecret.Trim();
            _store.Google.SaveClient(new OAuthClient(id, secret), body.Write == true);
            return Results.Ok(GoogleStatus());
        });

        // Starts the browser sign-in: the page opens the returned address; Google sends the browser back to /oauth/callback.
        app.MapPost("/api/google/connect", (HttpContext ctx) =>
        {
            if (_store.Google.Client() is not { } client) return Results.BadRequest(new { error = "Save your Google OAuth client first." });
            var redirect = $"{ctx.Request.Scheme}://{ctx.Request.Host}/oauth/callback";
            var request = GoogleSignIn.Begin(client, redirect, _store.Google.CanWrite, GoogleEp);
            foreach (var old in _pending.Where(p => DateTimeOffset.UtcNow - p.Value.Started > SignInWindow).Select(p => p.Key)) _pending.TryRemove(old, out _);
            _pending[request.State] = new Pending("google-calendar", request, DateTimeOffset.UtcNow, Google: true);
            return Results.Ok(new { url = request.AuthorizeUrl.ToString() });
        });

        // The calendars to use: GET lists them with the ones in use ticked; PUT keeps the ids the user ticked (null goes back to Google's own switches).
        app.MapGet("/api/google/calendars", async (HttpContext ctx) =>
        {
            if (!_store.Google.Connected) return Results.BadRequest(new { error = "Google Calendar isn't connected." });
            try { return Results.Ok(await GoogleCalendarsAsync(ctx.RequestAborted)); }
            catch (OperationCanceledException) { return Results.StatusCode(499); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPut("/api/google/calendars", async (HttpContext ctx, CalendarsBody body) =>
        {
            if (!_store.Google.Connected) return Results.BadRequest(new { error = "Google Calendar isn't connected." });
            try
            {
                var known = (await GoogleClient.ListCalendarsAsync(ctx.RequestAborted)).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
                if (!ValidChoice(body.Ids, known)) return Results.BadRequest(new { error = "Those aren't calendars of your Google account." });
                _store.Google.SaveCalendars(body.Ids);
                return Results.Ok(await GoogleCalendarsAsync(ctx.RequestAborted));
            }
            catch (OperationCanceledException) { return Results.StatusCode(499); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/google/disconnect", async (HttpContext ctx, ForgetBody body) =>
        {
            if (_store.Google.TokenToRevoke() is { } token)
            {
                try { await GoogleCalendar.RevokeAsync(token, GoogleEp, ct: ctx.RequestAborted); }
                catch (Exception) { /* forgotten here either way; it can be removed in the Google account's security page */ }
            }
            if (body.Forget == true) _store.Google.Forget(); else _store.Google.SignOut();
            return Results.Ok(GoogleStatus());
        });

        // ----- Nextcloud -----

        app.MapPost("/api/nextcloud/connect", async (HttpContext ctx, ServerBody body) =>
        {
            var server = Nextcloud.NormalizeServer(body.Server);
            if (server is null) return Results.BadRequest(new { error = "Type the address of your Nextcloud, like cloud.example.com. It has to be https." });
            try
            {
                var start = await _nextcloud.BeginLoginAsync(server.ToString(), ctx.RequestAborted);
                _login?.Cts.Cancel();
                var login = _login = new NextcloudLogin { Cts = CancellationTokenSource.CreateLinkedTokenSource(_stopping) };
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var account = await _nextcloud.WaitForLoginAsync(start, _store.Options.NextcloudPollInterval, TimeSpan.FromMinutes(10), login.Cts.Token);
                        _store.Nextcloud.Save(account);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { login.Error = ex.Message; }
                    finally { login.Done = true; }
                });
                return Results.Ok(new { url = start.LoginUrl.ToString() });
            }
            catch (OperationCanceledException) { return Results.StatusCode(499); }
            catch (Exception ex) { return Results.BadRequest(new { error = "Couldn't connect to Nextcloud: " + ex.Message }); }
        });

        // The calendars to use: GET lists them with the ones in use ticked; PUT keeps the ones the user ticked (null goes back to all of them).
        app.MapGet("/api/nextcloud/calendars", async (HttpContext ctx) =>
        {
            if (_store.Nextcloud.Load() is not { } account) return Results.BadRequest(new { error = "Nextcloud isn't connected." });
            try { return Results.Ok(await NextcloudCalendarsAsync(account, ctx.RequestAborted)); }
            catch (OperationCanceledException) { return Results.StatusCode(499); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPut("/api/nextcloud/calendars", async (HttpContext ctx, CalendarsBody body) =>
        {
            if (_store.Nextcloud.Load() is not { } account) return Results.BadRequest(new { error = "Nextcloud isn't connected." });
            try
            {
                var known = (await _nextcloud.ListCalendarsAsync(account, ctx.RequestAborted)).Select(c => c.Segment).ToHashSet(StringComparer.Ordinal);
                if (!ValidChoice(body.Ids, known)) return Results.BadRequest(new { error = "Those aren't calendars of your Nextcloud." });
                _store.Nextcloud.SaveCalendars(body.Ids);
                return Results.Ok(await NextcloudCalendarsAsync(account, ctx.RequestAborted));
            }
            catch (OperationCanceledException) { return Results.StatusCode(499); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/nextcloud/disconnect", async (HttpContext ctx) =>
        {
            _login?.Cts.Cancel();
            if (_store.Nextcloud.Load() is { } account)
            {
                try { await _nextcloud.RevokeAsync(account, ctx.RequestAborted); }
                catch (Exception) { /* the app password is forgotten here either way; it can be revoked in Nextcloud's settings */ }
            }
            _store.Nextcloud.Clear();
            return Results.Ok();
        });
    }

    sealed record AddApp(string? Name, string? Url, bool? TokenRequired, string? ClientId = null, string? ClientSecret = null, string? Scope = null);
    sealed record TokenBody(string? Token);
    sealed record ServerBody(string? Server);
    sealed record GoogleClientBody(string? ClientId, string? ClientSecret, bool? Write);
    sealed record CalendarsBody(string[]? Ids);
    sealed record ForgetBody(bool? Forget);
}
