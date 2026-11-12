using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

/// <summary>A tiny stand-in for an MCP server behind OAuth (discovery, registration and token endpoints), for the sign-in checks.</summary>
sealed class FakeOAuthServer : IAsyncDisposable
{
    readonly HttpListener _listener = new();
    readonly Task _loop;

    public string BaseUrl { get; }
    public string? RegisteredRedirect, RegisteredAuthMethod, LastVerifier;
    /// <summary>The last form sent to the Google-like token endpoint (/gtoken), and how many registrations were asked for.</summary>
    public Dictionary<string, string> LastGoogleForm = new();
    public int Registrations;

    public FakeOAuthServer()
    {
        var port = new Random().Next(20000, 40000);
        BaseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _loop = Task.Run(Serve);
    }

    public string Challenge(string verifier) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    async Task Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext c;
            try { c = await _listener.GetContextAsync(); } catch { return; }
            var path = c.Request.Url!.AbsolutePath;
            var body = await new StreamReader(c.Request.InputStream).ReadToEndAsync();
            string? json = null;
            var auth = c.Request.Headers["Authorization"];
            switch (path)
            {
                case "/open":
                    json = FakeMcp.Answer(body, "add_note") ?? "{}";
                    if (json == "") { c.Response.StatusCode = 202; json = null; }
                    break;
                case "/closed" when auth == "Bearer secret-token-1":
                    json = FakeMcp.Answer(body, "lookup") ?? "{}";
                    if (json == "") { c.Response.StatusCode = 202; json = null; }
                    break;
                case "/closed":
                    c.Response.StatusCode = 401; // asks for a token but says nothing about signing in
                    break;
                case "/mcp" when auth == "Bearer access-1":
                    json = FakeMcp.Answer(body, "search") ?? "{}";
                    if (json == "") { c.Response.StatusCode = 202; json = null; }
                    break;
                // A server like Google's: no registration (the client is the user's own, with a secret), no offline_access scope.
                case "/gcal" when auth is "Bearer access-g1" or "Bearer access-g2":
                    json = FakeMcp.Answer(body, "list_events") ?? "{}";
                    if (json == "") { c.Response.StatusCode = 202; json = null; }
                    break;
                case "/gcal" when body.Contains("\"method\":\"initialize\""):
                    json = FakeMcp.Answer(body, "list_events");
                    break;
                case "/gcal":
                    c.Response.StatusCode = 401;
                    c.Response.Headers["WWW-Authenticate"] = $"Bearer resource_metadata=\"{BaseUrl}/.well-known/oauth-protected-resource/gcal\"";
                    break;
                case "/.well-known/oauth-protected-resource/gcal":
                    json = $$"""{"resource":"{{BaseUrl}}/gcal","authorization_servers":["{{BaseUrl}}/google"],"scopes_supported":["https://example/calendar.readonly"]}""";
                    break;
                case "/.well-known/oauth-authorization-server/google":
                    json = $$"""{"issuer":"{{BaseUrl}}/google","authorization_endpoint":"{{BaseUrl}}/gauthorize?kept=1","token_endpoint":"{{BaseUrl}}/gtoken","scopes_supported":["openid","email","profile"]}""";
                    break;
                case "/gtoken":
                    var gform = ParseForm(body);
                    LastGoogleForm = gform;
                    if (gform.GetValueOrDefault("client_id") != "gcid" || gform.GetValueOrDefault("client_secret") != "gsecret") { c.Response.StatusCode = 401; json = """{"error":"invalid_client"}"""; }
                    else if (gform["grant_type"] == "authorization_code") json = """{"access_token":"access-g1","refresh_token":"refresh-g1","expires_in":3599,"token_type":"Bearer"}""";
                    else if (gform["grant_type"] == "refresh_token" && gform["refresh_token"] == "refresh-g1") json = """{"access_token":"access-g2","expires_in":3599,"token_type":"Bearer"}""";
                    else c.Response.StatusCode = 400;
                    break;
                case "/406":
                    c.Response.StatusCode = 406;
                    break;
                case "/mcp":
                    c.Response.StatusCode = 401;
                    c.Response.Headers["WWW-Authenticate"] = $"Bearer realm=\"OAuth\", resource_metadata=\"{BaseUrl}/.well-known/oauth-protected-resource/mcp\", scope=\"read write\"";
                    break;
                case "/.well-known/oauth-protected-resource/mcp":
                    json = $$"""{"resource":"{{BaseUrl}}/mcp","authorization_servers":["{{BaseUrl}}/"]}""";
                    break;
                case "/.well-known/oauth-authorization-server":
                    json = $$"""{"issuer":"{{BaseUrl}}","authorization_endpoint":"{{BaseUrl}}/authorize","token_endpoint":"{{BaseUrl}}/token","registration_endpoint":"{{BaseUrl}}/register","scopes_supported":["read","write","offline_access"]}""";
                    break;
                case "/register":
                    Registrations++;
                    using (var doc = JsonDocument.Parse(body))
                    {
                        RegisteredRedirect = doc.RootElement.GetProperty("redirect_uris")[0].GetString();
                        RegisteredAuthMethod = doc.RootElement.GetProperty("token_endpoint_auth_method").GetString();
                    }
                    c.Response.StatusCode = 201;
                    json = """{"client_id":"cid-1"}""";
                    break;
                case "/token":
                    var form = ParseForm(body);
                    if (form["grant_type"] == "authorization_code")
                    {
                        LastVerifier = form["code_verifier"];
                        json = """{"access_token":"access-1","refresh_token":"refresh-1","expires_in":3600,"token_type":"Bearer"}""";
                    }
                    else if (form["grant_type"] == "refresh_token" && form["refresh_token"] == "refresh-1")
                        json = """{"access_token":"access-2","expires_in":"3600","token_type":"Bearer"}""";
                    else c.Response.StatusCode = 400;
                    break;
                default:
                    c.Response.StatusCode = 404;
                    break;
            }
            if (json is not null)
            {
                c.Response.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes(json);
                await c.Response.OutputStream.WriteAsync(bytes);
            }
            c.Response.Close();
        }
    }

    static Dictionary<string, string> ParseForm(string body)
    {
        var d = new Dictionary<string, string>();
        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = pair.IndexOf('=');
            d[Uri.UnescapeDataString(pair[..i])] = Uri.UnescapeDataString(pair[(i + 1)..].Replace('+', ' '));
        }
        return d;
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Close();
        await _loop;
    }
}
