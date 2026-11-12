using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Longhand.Services;

/// <summary>
/// An OAuth client the user registered with the service themselves (Google's, for one, doesn't let apps register on their own):
/// its id, its secret when it has one, and the scopes to ask for when the server doesn't say which (space separated).
/// </summary>
public sealed record OAuthClient(string ClientId, string? ClientSecret = null, string? Scope = null);

/// <summary>What a signed-in MCP server needs to keep working: the tokens and where to refresh them (and the client secret, when the client has one).</summary>
public sealed record OAuthSession(string Url, string ClientId, string TokenEndpoint, string? Resource, string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt, string? ClientSecret = null)
{
    public bool NeedsRefresh => DateTimeOffset.UtcNow > ExpiresAt - TimeSpan.FromMinutes(2);
}

/// <summary>How a server lets you in.</summary>
public enum McpAccess { Open, SignIn, Token }

/// <summary>A sign-in that has been set up and is waiting for the user to approve it in the browser.</summary>
public sealed record SignInRequest(Uri AuthorizeUrl, string State, string Verifier, string Url, string ClientId, string TokenEndpoint, string? Resource, string RedirectUri, string? ClientSecret = null);

/// <summary>
/// The OAuth sign-in that MCP servers ask for: find the server's authorization server, register as a public client (or use the
/// client the user registered themselves), authorize with PKCE, then trade the code for tokens and keep them fresh. Longhand's MCP client then sends the access token.
/// </summary>
public sealed class McpOAuth
{
    /// <summary>
    /// Where the browser lands after approval. Servers only accept https addresses here, so this is a page on the project's
    /// GitHub Pages site (callback.html) that hands the result to the app through the longhand:// address.
    /// </summary>
    public const string RedirectUri = "https://thufschmitt.github.io/noter/callback.html";

    const string InitializeBody = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"Longhand","version":"1"}}}""";

    readonly HttpClient _http;

    public McpOAuth(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Asks the server what it wants before anything is sent: nothing (Open), a browser sign-in (SignIn), or an access token
    /// the user has to get from the service themselves (Token).
    /// </summary>
    public async Task<McpAccess> InspectAsync(string mcpUrl, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, mcpUrl) { Content = new StringContent(InitializeBody, Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var code = (int)response.StatusCode;

        // Some servers (Google's Calendar) answer the opening handshake to anyone and only ask for a sign-in when a tool is
        // called. A server that publishes where its sign-in is described is one that wants it, whatever the handshake said.
        if (response.IsSuccessStatusCode) return await PublishesSignInAsync(mcpUrl, ct) ? McpAccess.SignIn : McpAccess.Open;
        if (code is 401 or 403)
        {
            if (response.Headers.TryGetValues("WWW-Authenticate", out var values) && Param(string.Join(", ", values), "resource_metadata") is not null) return McpAccess.SignIn;
            // No hint in the header: a server that supports sign-in still publishes where it is described.
            return await GetJsonAsync(WellKnown(new Uri(mcpUrl), "oauth-protected-resource"), ct) is not null ? McpAccess.SignIn : McpAccess.Token;
        }
        // 400, 405, 406 and 415 mean "I didn't like this request", not "you can't come in": a real call may well work.
        if (code is 400 or 405 or 406 or 415) return await PublishesSignInAsync(mcpUrl, ct) ? McpAccess.SignIn : McpAccess.Open;
        throw new InvalidOperationException($"The server answered {code}, so it can't be used from here.");
    }

    async Task<bool> PublishesSignInAsync(string mcpUrl, CancellationToken ct) =>
        await GetJsonAsync(WellKnown(new Uri(mcpUrl), "oauth-protected-resource"), ct) is { } meta && FirstString(meta, "authorization_servers") is not null;

    /// <param name="client">A client the user registered with the service; without one the app registers itself, which only some services allow.</param>
    public async Task<SignInRequest> BeginAsync(string mcpUrl, string redirectUri, CancellationToken ct, OAuthClient? client = null)
    {
        var (resourceMetadataUrl, challengeScope) = await ProbeAsync(mcpUrl, ct);
        var origin = new Uri(mcpUrl);
        var resourceMeta = await GetJsonAsync(resourceMetadataUrl ?? WellKnown(origin, "oauth-protected-resource"), ct)
            ?? throw new InvalidOperationException("The server didn't describe how to sign in.");

        var issuer = FirstString(resourceMeta, "authorization_servers")
            ?? throw new InvalidOperationException("The server didn't name an authorization server.");
        var resource = Str(resourceMeta, "resource") ?? mcpUrl;
        var meta = await DiscoverAuthorizationServerAsync(issuer, ct);

        var authorizeEndpoint = Str(meta, "authorization_endpoint") ?? throw new InvalidOperationException("The sign-in service has no authorization address.");
        var tokenEndpoint = Str(meta, "token_endpoint") ?? throw new InvalidOperationException("The sign-in service has no token address.");
        var registration = Str(meta, "registration_endpoint");
        if (registration is null && client is null)
            throw new InvalidOperationException("The sign-in service doesn't let apps register themselves. Register an OAuth client with the service yourself and add its client ID (and secret) with the app.");

        var scope = (string.IsNullOrWhiteSpace(client?.Scope) ? null : client!.Scope!.Trim()) ?? challengeScope ?? (resourceMeta.TryGetProperty("scopes_supported", out var scopes) && scopes.ValueKind == JsonValueKind.Array
            ? string.Join(' ', scopes.EnumerateArray().Select(s => s.GetString()).Where(s => !string.IsNullOrEmpty(s)))
            : null);
        if (string.IsNullOrWhiteSpace(scope)) scope = null;
        // Refresh tokens are only issued to clients that ask for this scope, when the service offers it.
        bool offlineScope = meta.TryGetProperty("scopes_supported", out var offered) && offered.ValueKind == JsonValueKind.Array
            && offered.EnumerateArray().Any(s => s.GetString() == "offline_access");
        if (scope is not null && offlineScope && !scope.Split(' ').Contains("offline_access"))
            scope += " offline_access";

        var clientId = client?.ClientId ?? await RegisterAsync(registration!, redirectUri, scope, ct);

        var verifier = RandomUrlSafe(48);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = RandomUrlSafe(24);
        var query = new List<KeyValuePair<string, string>>
        {
            new("response_type", "code"),
            new("client_id", clientId),
            new("redirect_uri", redirectUri),
            new("code_challenge", challenge),
            new("code_challenge_method", "S256"),
            new("state", state),
            new("resource", resource),
        };
        if (scope is not null) query.Add(new("scope", scope));
        // Services without that scope (Google's) hand out a refresh token only when asked like this; without one the user would be
        // asked to sign in again every hour. A service that doesn't know these ignores them.
        if (!offlineScope)
        {
            query.Add(new("access_type", "offline"));
            query.Add(new("prompt", "consent"));
        }

        var url = new UriBuilder(authorizeEndpoint);
        var existing = url.Query.TrimStart('?');
        url.Query = (existing.Length > 0 ? existing + "&" : "") + string.Join("&", query.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        return new SignInRequest(url.Uri, state, verifier, mcpUrl, clientId, tokenEndpoint, resource, redirectUri, client?.ClientSecret);
    }

    public async Task<OAuthSession> FinishAsync(SignInRequest request, string callbackQuery, CancellationToken ct)
    {
        var q = ParseQuery(callbackQuery);
        if (q.TryGetValue("error", out var error))
            throw new InvalidOperationException(q.TryGetValue("error_description", out var why) ? why : error);
        if (!q.TryGetValue("state", out var state) || state != request.State)
            throw new InvalidOperationException("The sign-in answer didn't match the request. Try again.");
        if (!q.TryGetValue("code", out var code))
            throw new InvalidOperationException("The sign-in finished without a code.");

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = request.RedirectUri,
            ["client_id"] = request.ClientId,
            ["code_verifier"] = request.Verifier,
        };
        if (request.Resource is not null) form["resource"] = request.Resource;
        if (!string.IsNullOrEmpty(request.ClientSecret)) form["client_secret"] = request.ClientSecret;
        return ReadTokens(await PostFormAsync(request.TokenEndpoint, form, ct), request.Url, request.ClientId, request.TokenEndpoint, request.Resource, previousRefresh: null, request.ClientSecret);
    }

    public async Task<OAuthSession> RefreshAsync(OAuthSession session, CancellationToken ct)
    {
        if (session.RefreshToken is null) throw new InvalidOperationException("No refresh token; sign in again.");
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = session.RefreshToken,
            ["client_id"] = session.ClientId,
        };
        if (session.Resource is not null) form["resource"] = session.Resource;
        if (!string.IsNullOrEmpty(session.ClientSecret)) form["client_secret"] = session.ClientSecret;
        return ReadTokens(await PostFormAsync(session.TokenEndpoint, form, ct), session.Url, session.ClientId, session.TokenEndpoint, session.Resource, session.RefreshToken, session.ClientSecret);
    }

    // ---------- discovery ----------

    /// <summary>An unauthenticated request makes the server say where its sign-in is described (RFC 9728).</summary>
    async Task<(string? ResourceMetadata, string? Scope)> ProbeAsync(string mcpUrl, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, mcpUrl) { Content = new StringContent(InitializeBody, Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        // A server that answers the handshake without a sign-in (Google's) says nothing here: where its sign-in is described is
        // then found at the well-known address.
        string? metadata = null, scope = null;
        if (response.Headers.TryGetValues("WWW-Authenticate", out var values))
        {
            var header = string.Join(", ", values);
            metadata = Param(header, "resource_metadata");
            scope = Param(header, "scope");
        }
        return (metadata, scope);
    }

    async Task<JsonElement> DiscoverAuthorizationServerAsync(string issuer, CancellationToken ct)
    {
        var uri = new Uri(issuer);
        var path = uri.AbsolutePath.TrimEnd('/');
        var origin = uri.GetLeftPart(UriPartial.Authority);
        var candidates = new[]
        {
            $"{origin}/.well-known/oauth-authorization-server{path}",
            $"{origin}/.well-known/openid-configuration{path}",
            $"{issuer.TrimEnd('/')}/.well-known/openid-configuration",
        };
        foreach (var candidate in candidates)
            if (await GetJsonAsync(candidate, ct) is { } meta) return meta;
        throw new InvalidOperationException("Couldn't find the server's sign-in service.");
    }

    static string WellKnown(Uri mcp, string name) => $"{mcp.GetLeftPart(UriPartial.Authority)}/.well-known/{name}{mcp.AbsolutePath.TrimEnd('/')}";

    async Task<JsonElement?> GetJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return null;
        }
    }

    async Task<string> RegisterAsync(string endpoint, string redirectUri, string? scope, CancellationToken ct)
    {
        var body = new Dictionary<string, object>
        {
            ["client_name"] = "Longhand",
            ["redirect_uris"] = new[] { redirectUri },
            ["grant_types"] = new[] { "authorization_code", "refresh_token" },
            ["response_types"] = new[] { "code" },
            ["token_endpoint_auth_method"] = "none",
        };
        if (scope is not null) body["scope"] = scope;
        using var response = await _http.PostAsync(endpoint, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"The server refused to register the app ({(int)response.StatusCode}): {Snippet(text)}");
        using var doc = JsonDocument.Parse(text);
        return Str(doc.RootElement, "client_id") ?? throw new InvalidOperationException("The server registered the app but gave no client id.");
    }

    // ---------- tokens ----------

    async Task<JsonElement> PostFormAsync(string endpoint, Dictionary<string, string> form, CancellationToken ct)
    {
        using var response = await _http.PostAsync(endpoint, new FormUrlEncodedContent(form), ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"The sign-in service refused ({(int)response.StatusCode}): {Snippet(text)}");
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    static OAuthSession ReadTokens(JsonElement json, string url, string clientId, string tokenEndpoint, string? resource, string? previousRefresh, string? clientSecret = null)
    {
        var access = Str(json, "access_token") ?? throw new InvalidOperationException("The sign-in service gave no access token.");
        var refresh = Str(json, "refresh_token") ?? previousRefresh;
        double seconds = 3600;
        if (json.TryGetProperty("expires_in", out var e))
            seconds = e.ValueKind == JsonValueKind.Number ? e.GetDouble() : double.TryParse(e.GetString(), out var parsed) ? parsed : 3600;
        return new OAuthSession(url, clientId, tokenEndpoint, resource, access, refresh, DateTimeOffset.UtcNow.AddSeconds(seconds), clientSecret);
    }

    // ---------- small helpers ----------

    public static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>();
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = pair.IndexOf('=');
            var key = Uri.UnescapeDataString((i < 0 ? pair : pair[..i]).Replace('+', ' '));
            var value = i < 0 ? "" : Uri.UnescapeDataString(pair[(i + 1)..].Replace('+', ' '));
            result[key] = value;
        }
        return result;
    }

    static string? Param(string header, string name)
    {
        var m = Regex.Match(header, name + "=\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static string? FirstString(JsonElement e, string array) =>
        e.TryGetProperty(array, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0 ? v[0].GetString() : null;

    internal static string RandomUrlSafe(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    internal static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static string Snippet(string text) => text.Length <= 200 ? text : text[..200] + "…";
}
