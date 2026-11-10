using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Longhand.Services;

/// <summary>How an MCP server lets you in: it asks for nothing, for a browser sign-in, or for an access token the user pastes.</summary>
public static class AppAccess
{
    public const string SignIn = "oauth", Token = "token", Open = "open";
}

/// <summary>An MCP server the user added. <c>Ready</c> means it has what it needs to be used (an open server always does).</summary>
public sealed record ConnectedApp(string Name, string Url, string Access, bool Ready);

/// <summary>
/// The connected apps. What identifies the sign-in (server address, client id, token endpoint, expiry) is kept in the settings; the
/// tokens themselves are kept as secrets (Credential Manager on Windows, a private file on the web server).
/// </summary>
public sealed class ConnectorBook
{
    readonly ISettings _settings;
    readonly ISecrets _secrets;

    public ConnectorBook(ISettings settings, ISecrets secrets)
    {
        _settings = settings;
        _secrets = secrets;
    }

    // Mode is null for apps saved before there were other kinds of access: those were all browser sign-ins.
    sealed record Stored(string Url, string? ClientId, string? TokenEndpoint, string? Resource, DateTimeOffset? ExpiresAt, string? Mode = null);

    static string ModeOf(Stored stored) => stored.Mode ?? AppAccess.SignIn;

    static string AccessUser(string name) => "mcp-access:" + name;
    static string RefreshUser(string name) => "mcp-refresh:" + name;

    public IReadOnlyList<ConnectedApp> List() =>
        _settings.All()
            .Select(v => (Name: v.Key, Stored: Read(v.Value)))
            .Where(v => v.Stored is not null)
            .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .Select(v => new ConnectedApp(v.Name, v.Stored!.Url, ModeOf(v.Stored!), ModeOf(v.Stored!) == AppAccess.Open || Secret(AccessUser(v.Name)) is not null))
            .ToList();

    public void Add(string name, string url, string access = AppAccess.SignIn) =>
        _settings.Set(name, JsonSerializer.Serialize(new Stored(url, null, null, null, null, access)));

    /// <summary>For a server that wants a token the user got from the service itself. It has no expiry and nothing to refresh.</summary>
    public void SaveToken(string name, string token) => Keep(AccessUser(name), token.Trim());

    /// <summary>The name, made unique among the apps already added.</summary>
    public string UniqueName(string wanted)
    {
        var taken = new HashSet<string>(List().Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(wanted)) return wanted;
        for (int i = 2; ; i++) if (!taken.Contains(wanted + "-" + i)) return wanted + "-" + i;
    }

    public void Remove(string name)
    {
        SignOut(name);
        _settings.Remove(name);
    }

    public void SignOut(string name)
    {
        Forget(AccessUser(name));
        Forget(RefreshUser(name));
    }

    public void SaveSession(string name, OAuthSession session)
    {
        _settings.Set(name, JsonSerializer.Serialize(new Stored(session.Url, session.ClientId, session.TokenEndpoint, session.Resource, session.ExpiresAt, AppAccess.SignIn)));
        Keep(AccessUser(name), session.AccessToken);
        if (session.RefreshToken is not null) Keep(RefreshUser(name), session.RefreshToken);
    }

    /// <summary>
    /// The signed-in apps, ready for a request: tokens close to expiring are refreshed first. Apps whose sign-in can no longer be
    /// renewed are left out and named in <c>Expired</c>, so the user can be asked to sign in again.
    /// </summary>
    public async Task<(List<Connector> Active, List<string> Expired)> ActiveAsync(McpOAuth oauth, CancellationToken ct)
    {
        var active = new List<Connector>();
        var expired = new List<string>();
        foreach (var app in List().Where(a => a.Ready))
        {
            var stored = Read(_settings.Get(app.Name));
            if (app.Access == AppAccess.Open) { active.Add(new Connector(app.Name, app.Url, null)); continue; }
            var access = Secret(AccessUser(app.Name));
            if (app.Access == AppAccess.Token) { if (access is not null) active.Add(new Connector(app.Name, app.Url, access)); continue; }
            if (stored is null || access is null || stored.ClientId is null || stored.TokenEndpoint is null) continue;

            var session = new OAuthSession(stored.Url, stored.ClientId, stored.TokenEndpoint, stored.Resource, access, Secret(RefreshUser(app.Name)), stored.ExpiresAt ?? DateTimeOffset.MinValue);
            if (session.NeedsRefresh)
            {
                try
                {
                    session = await oauth.RefreshAsync(session, ct);
                    SaveSession(app.Name, session);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    expired.Add(app.Name);
                    continue;
                }
            }
            active.Add(new Connector(app.Name, app.Url, session.AccessToken));
        }
        return (active, expired);
    }

    static Stored? Read(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<Stored>(json); }
        catch (JsonException) { return null; }
    }

    string? Secret(string user) => _secrets.Get(user) is { Length: > 0 } s ? s : null;

    void Keep(string user, string secret) => _secrets.Set(user, secret);

    void Forget(string user) => _secrets.Remove(user);
}
