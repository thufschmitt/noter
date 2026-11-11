using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Longhand.Services;

/// <summary>The sign-in was refused, and refreshing it didn't help: the user has to paste a fresh one.</summary>
public sealed class CodexSignInException : Exception
{
    public CodexSignInException(string message) : base(message) { }
}

/// <summary>
/// A ChatGPT sign-in, as the Codex CLI keeps it in <c>~/.codex/auth.json</c>: an access token (a JWT, sent as the bearer), the
/// refresh token that renews it, and the ChatGPT account id. The user pastes that file; Longhand keeps its own copy and
/// renews it. Refresh tokens are single-use, so once either Longhand or the Codex CLI renews, the other's copy stops working.
/// </summary>
public sealed record CodexSignIn(string AccessToken, string RefreshToken, string AccountId)
{
    public const string RefreshUrl = "https://auth.openai.com/oauth/token";
    const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";

    /// <summary>
    /// What the user pasted: the whole auth.json (<c>{"tokens":{"access_token",…}}</c>), or just its <c>tokens</c> object.
    /// Null when it isn't a ChatGPT sign-in (an API key alone, or not JSON).
    /// </summary>
    public static CodexSignIn? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var doc = JsonDocument.Parse(text.Trim());
            var root = doc.RootElement;
            var tokens = root.TryGetProperty("tokens", out var t) && t.ValueKind == JsonValueKind.Object ? t : root;
            string? Get(string name) => tokens.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var access = Get("access_token");
            var refresh = Get("refresh_token") ?? "";
            if (string.IsNullOrEmpty(access)) return null;
            var account = Get("account_id") ?? AccountFromToken(access) ?? AccountFromToken(Get("id_token"));
            return account is null ? null : new CodexSignIn(access, refresh, account);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>How Longhand stores it: the same shape as auth.json's tokens, so it can be pasted back into Codex if wanted.</summary>
    public string ToJson() => new JsonObject
    {
        ["tokens"] = new JsonObject { ["access_token"] = AccessToken, ["refresh_token"] = RefreshToken, ["account_id"] = AccountId },
    }.ToJsonString();

    /// <summary>When the access token stops working, from its <c>exp</c> claim; null when it doesn't say.</summary>
    public DateTimeOffset? Expires => Claims(AccessToken) is { } claims && claims.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds)
        ? DateTimeOffset.FromUnixTimeSeconds(seconds)
        : null;

    /// <summary>True when the access token has run out, or will within five minutes: then it is renewed before use.</summary>
    public bool NeedsRefresh(DateTimeOffset now) => RefreshToken.Length > 0 && Expires is { } expires && expires - now < TimeSpan.FromMinutes(5);

    /// <summary>A new access token (and refresh token, which is single-use) from the refresh token.</summary>
    public async Task<CodexSignIn> RefreshAsync(HttpClient http, CancellationToken ct, string refreshUrl = RefreshUrl)
    {
        if (RefreshToken.Length == 0) throw new CodexSignInException("The ChatGPT sign-in has run out. Paste a fresh one from ~/.codex/auth.json (gear button).");
        var body = new JsonObject { ["grant_type"] = "refresh_token", ["client_id"] = ClientId, ["refresh_token"] = RefreshToken }.ToJsonString();
        using var request = new HttpRequestMessage(HttpMethod.Post, refreshUrl) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new CodexSignInException("ChatGPT no longer accepts the sign-in (it may have been renewed by the Codex CLI since). Paste a fresh one from ~/.codex/auth.json (gear button).");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Renewing the ChatGPT sign-in failed ({(int)response.StatusCode}).");
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        string? Get(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var access = Get("access_token") ?? throw new InvalidOperationException("Renewing the ChatGPT sign-in gave no access token.");
        return new CodexSignIn(access, Get("refresh_token") ?? RefreshToken, AccountFromToken(access) ?? AccountFromToken(Get("id_token")) ?? AccountId);
    }

    /// <summary>The ChatGPT account id in a token's claims (<c>https://api.openai.com/auth</c> → <c>chatgpt_account_id</c>).</summary>
    static string? AccountFromToken(string? jwt) =>
        Claims(jwt) is { } claims && claims.TryGetProperty("https://api.openai.com/auth", out var auth) && auth.ValueKind == JsonValueKind.Object
        && auth.TryGetProperty("chatgpt_account_id", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;

    /// <summary>A JWT's payload. Only read, never trusted for anything but timing and the account id the server checks anyway.</summary>
    static JsonElement? Claims(string? jwt)
    {
        var parts = jwt?.Split('.');
        if (parts is not { Length: 3 }) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }
}
