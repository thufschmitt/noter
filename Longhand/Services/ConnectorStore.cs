using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Longhand.Services;

/// <summary>The connected apps on Windows: the shared <see cref="ConnectorBook"/> over local settings and Credential Manager.</summary>
public static class ConnectorStore
{
    static readonly ConnectorBook Book = new(WindowsSettings.Container("mcp"), new WindowsSecrets());

    public static IReadOnlyList<ConnectedApp> List() => Book.List();
    public static void Add(string name, string url, string access = AppAccess.SignIn) => Book.Add(name, url, access);
    public static void SaveToken(string name, string token) => Book.SaveToken(name, token);
    public static string UniqueName(string wanted) => Book.UniqueName(wanted);
    public static void Remove(string name) => Book.Remove(name);
    public static void SignOut(string name) => Book.SignOut(name);
    public static void SaveSession(string name, OAuthSession session) => Book.SaveSession(name, session);
    public static Task<(List<Connector> Active, List<string> Expired)> ActiveAsync(McpOAuth oauth, CancellationToken ct) => Book.ActiveAsync(oauth, ct);
}
