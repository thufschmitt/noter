using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Security.Credentials;
using Windows.Storage;

namespace Longhand.Services;

/// <summary>An MCP server the user added, and whether it is signed in.</summary>
public sealed record ConnectedApp(string Name, string Url, bool SignedIn);

/// <summary>
/// The connected apps. What identifies the sign-in (server address, client id, token endpoint, expiry) is kept in local settings;
/// the tokens themselves are kept in Windows Credential Manager next to the API key.
/// </summary>
public static class ConnectorStore
{
    const string Resource = "Longhand";
    const string Container = "mcp";

    sealed record Stored(string Url, string? ClientId, string? TokenEndpoint, string? Resource, DateTimeOffset? ExpiresAt);

    static ApplicationDataContainer Settings => ApplicationData.Current.LocalSettings.CreateContainer(Container, ApplicationDataCreateDisposition.Always);

    static string AccessUser(string name) => "mcp-access:" + name;
    static string RefreshUser(string name) => "mcp-refresh:" + name;

    public static IReadOnlyList<ConnectedApp> List() =>
        Settings.Values
            .Select(v => (Name: v.Key, Stored: Read(v.Value as string)))
            .Where(v => v.Stored is not null)
            .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .Select(v => new ConnectedApp(v.Name, v.Stored!.Url, Secret(AccessUser(v.Name)) is not null))
            .ToList();

    public static void Add(string name, string url) =>
        Settings.Values[name] = JsonSerializer.Serialize(new Stored(url, null, null, null, null));

    public static void Remove(string name)
    {
        SignOut(name);
        Settings.Values.Remove(name);
    }

    public static void SignOut(string name)
    {
        Forget(AccessUser(name));
        Forget(RefreshUser(name));
    }

    public static void SaveSession(string name, OAuthSession session)
    {
        Settings.Values[name] = JsonSerializer.Serialize(new Stored(session.Url, session.ClientId, session.TokenEndpoint, session.Resource, session.ExpiresAt));
        Keep(AccessUser(name), session.AccessToken);
        if (session.RefreshToken is not null) Keep(RefreshUser(name), session.RefreshToken);
    }

    /// <summary>
    /// The signed-in apps, ready for a request: tokens close to expiring are refreshed first. Apps whose sign-in can no longer be
    /// renewed are left out and named in <c>Expired</c>, so the user can be asked to sign in again.
    /// </summary>
    public static async Task<(List<Connector> Active, List<string> Expired)> ActiveAsync(McpOAuth oauth, CancellationToken ct)
    {
        var active = new List<Connector>();
        var expired = new List<string>();
        foreach (var app in List().Where(a => a.SignedIn))
        {
            var stored = Read(Settings.Values[app.Name] as string);
            var access = Secret(AccessUser(app.Name));
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

    static string? Secret(string user)
    {
        try
        {
            var credential = new PasswordVault().Retrieve(Resource, user);
            credential.RetrievePassword();
            return string.IsNullOrEmpty(credential.Password) ? null : credential.Password;
        }
        catch
        {
            return null; // Retrieve throws when nothing is stored.
        }
    }

    static void Keep(string user, string secret)
    {
        Forget(user);
        new PasswordVault().Add(new PasswordCredential(Resource, user, secret));
    }

    static void Forget(string user)
    {
        try
        {
            var vault = new PasswordVault();
            vault.Remove(vault.Retrieve(Resource, user));
        }
        catch
        {
            // Nothing stored.
        }
    }
}
