using System;
using Longhand.Services;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;

/// <summary>A tiny local HTTP server for the checks: a handler answers each request, and every request is kept for inspection.</summary>
abstract class FakeServer : IAsyncDisposable
{
    public sealed record Request(string Method, string Path, string Query, Dictionary<string, string> Headers, string Body);

    readonly HttpListener _listener = new();
    readonly Task _loop;
    public List<Request> Requests { get; } = new();
    public string BaseUrl { get; }

    protected FakeServer()
    {
        var port = new Random().Next(20000, 40000);
        BaseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _loop = Task.Run(Serve);
    }

    protected abstract Task Handle(Request request, HttpListenerResponse response);

    protected static async Task Write(HttpListenerResponse response, int status, string contentType, string body)
    {
        response.StatusCode = status;
        response.ContentType = contentType;
        var bytes = Encoding.UTF8.GetBytes(body);
        await response.OutputStream.WriteAsync(bytes);
    }

    async Task Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext c;
            try { c = await _listener.GetContextAsync(); } catch { return; }
            try
            {
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string? key in c.Request.Headers.AllKeys) if (key is not null) headers[key] = c.Request.Headers[key] ?? "";
                var body = await new StreamReader(c.Request.InputStream).ReadToEndAsync();
                var request = new Request(c.Request.HttpMethod, c.Request.Url!.AbsolutePath, c.Request.Url.Query, headers, body);
                lock (Requests) Requests.Add(request);
                await Handle(request, c.Response);
            }
            catch (Exception)
            {
                c.Response.StatusCode = 500;
            }
            finally
            {
                c.Response.Close();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Close();
        await _loop;
    }
}

/// <summary>Enough of a Nextcloud server for the sign-in flow and the calendar queries.</summary>
sealed class FakeNextcloud : FakeServer
{
    public const string User = "alice", Password = "app-pass-123";
    int _polls;

    public string Ics { get; set; } = "";
    /// <summary>Single events, by their path under the user's calendars ("personal/x.ics"): their data and version tag.</summary>
    public Dictionary<string, (string Ics, string ETag)> Events { get; } = new();
    int _versions;

    protected override async Task Handle(Request r, HttpListenerResponse response)
    {
        string basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}"));
        bool authorized = r.Headers.TryGetValue("Authorization", out var auth) && auth == basic;

        if (r.Path == "/index.php/login/v2" && r.Method == "POST")
            await Write(response, 200, "application/json", $$"""{"poll":{"token":"tok123","endpoint":"{{BaseUrl}}/login/v2/poll"},"login":"{{BaseUrl}}/login/v2/flow/tok123"}""");
        else if (r.Path == "/login/v2/poll" && r.Method == "POST")
        {
            if (!r.Body.Contains("token=tok123")) await Write(response, 400, "text/plain", "bad token");
            else if (++_polls < 3) await Write(response, 404, "text/plain", "");
            else await Write(response, 200, "application/json", $$"""{"server":"{{BaseUrl}}","loginName":"{{User}}","appPassword":"{{Password}}"}""");
        }
        else if (r.Path == "/ocs/v2.php/core/apppassword" && r.Method == "DELETE")
            await Write(response, authorized ? 200 : 401, "text/plain", "");
        else if (!authorized)
            await Write(response, 401, "text/plain", "no");
        else if (r.Method == "PROPFIND" && r.Path == $"/remote.php/dav/calendars/{User}/")
            await Write(response, 207, "application/xml", """
                <?xml version="1.0"?>
                <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
                  <d:response><d:href>/remote.php/dav/calendars/alice/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response>
                  <d:response><d:href>/remote.php/dav/calendars/alice/personal/</d:href><d:propstat><d:prop><d:displayname>Personal</d:displayname>
                    <d:resourcetype><d:collection/><c:calendar/></d:resourcetype>
                    <c:supported-calendar-component-set><c:comp name="VEVENT"/><c:comp name="VTODO"/></c:supported-calendar-component-set>
                    <d:current-user-privilege-set><d:privilege><d:read/></d:privilege><d:privilege><d:write/></d:privilege></d:current-user-privilege-set></d:prop></d:propstat></d:response>
                  <d:response><d:href>/remote.php/dav/calendars/alice/team_shared_by_bob/</d:href><d:propstat><d:prop><d:displayname>Team</d:displayname>
                    <d:resourcetype><d:collection/><c:calendar/></d:resourcetype>
                    <d:current-user-privilege-set><d:privilege><d:read/></d:privilege></d:current-user-privilege-set></d:prop></d:propstat></d:response>
                  <d:response><d:href>/remote.php/dav/calendars/alice/tasks/</d:href><d:propstat><d:prop><d:displayname>Tasks</d:displayname>
                    <d:resourcetype><d:collection/><c:calendar/></d:resourcetype>
                    <c:supported-calendar-component-set><c:comp name="VTODO"/></c:supported-calendar-component-set></d:prop></d:propstat></d:response>
                  <d:response><d:href>/remote.php/dav/calendars/alice/inbox/</d:href><d:propstat><d:prop><d:displayname>Inbox</d:displayname>
                    <d:resourcetype><d:collection/><c:schedule-inbox/></d:resourcetype></d:prop></d:propstat></d:response>
                </d:multistatus>
                """);
        else if (r.Method == "REPORT" && r.Path == $"/remote.php/dav/calendars/{User}/personal/")
            await Write(response, 207, "application/xml", $"""
                <?xml version="1.0"?>
                <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
                  <d:response><d:href>/remote.php/dav/calendars/alice/personal/a.ics</d:href><d:propstat><d:prop>
                    <c:calendar-data>{System.Security.SecurityElement.Escape(Ics)}</c:calendar-data></d:prop></d:propstat></d:response>
                </d:multistatus>
                """);
        else if (r.Method == "REPORT" && r.Path == $"/remote.php/dav/calendars/{User}/team_shared_by_bob/")
            await Write(response, 207, "application/xml", """<?xml version="1.0"?><d:multistatus xmlns:d="DAV:"/>""");
        else if (r.Path.StartsWith($"/remote.php/dav/calendars/{User}/") && r.Path.EndsWith(".ics"))
            await Event(r, response);
        else
            await Write(response, 404, "text/plain", "nope");
    }

    /// <summary>GET, PUT and DELETE of one event, honouring If-Match and If-None-Match as a CalDAV server does.</summary>
    async Task Event(Request r, HttpListenerResponse response)
    {
        var key = r.Path[$"/remote.php/dav/calendars/{User}/".Length..];
        bool exists = Events.TryGetValue(key, out var current);
        if (key.StartsWith("team_shared_by_bob/") && r.Method != "GET") { await Write(response, 403, "text/plain", "read-only"); return; }
        if (r.Headers.TryGetValue("If-Match", out var match) && (!exists || match != current.ETag)) { await Write(response, 412, "text/plain", "changed"); return; }
        if (r.Headers.TryGetValue("If-None-Match", out var none) && none == "*" && exists) { await Write(response, 412, "text/plain", "exists"); return; }
        switch (r.Method)
        {
            case "GET" when exists:
                response.Headers["ETag"] = current.ETag;
                await Write(response, 200, "text/calendar", current.Ics);
                break;
            case "PUT":
                Events[key] = (r.Body, $"\"v{++_versions}\"");
                response.Headers["ETag"] = Events[key].ETag;
                await Write(response, exists ? 204 : 201, "text/plain", "");
                break;
            case "DELETE" when exists:
                Events.Remove(key);
                await Write(response, 204, "text/plain", "");
                break;
            default:
                await Write(response, 404, "text/plain", "no such event");
                break;
        }
    }
}

/// <summary>
/// Enough of ChatGPT's Codex backend (the Responses API, streamed) to check the service: it asks for the calendar tool once,
/// then answers, and renews sign-ins at /oauth/token. It accepts only the access token it last handed out.
/// </summary>
sealed class FakeCodex : FakeServer
{
    /// <summary>False answers straight away, without first asking for the calendar tool.</summary>
    public bool AskForTool { get; set; } = true;
    /// <summary>Answers every request with this HTTP error instead (0 = answer normally).</summary>
    public int FailStatus { get; set; }
    /// <summary>Waits this long before answering, so a test can act while the answer is still being written.</summary>
    public int ReplyDelayMs { get; set; }
    /// <summary>Ends the answer as incomplete for this reason ("max_output_tokens", "content_filter") instead of completed.</summary>
    public string? Incomplete { get; set; }
    /// <summary>Fails the answer mid-stream with this error code instead.</summary>
    public string? FailCode { get; set; }
    public string FinalText { get; set; } = "HEARD: what is on tomorrow\n---\nYou have a standup at 9.";
    /// <summary>The tool it asks for first, and with what.</summary>
    public string ToolName { get; set; } = "calendar_events";
    public string ToolArguments { get; set; } = """{"start":"2026-11-11","end":"2026-11-11"}""";

    /// <summary>The access token requests must carry; renewing replaces it.</summary>
    public string AccessToken { get; set; } = Jwt(DateTimeOffset.UtcNow.AddDays(10), "acct-1");
    public string RefreshToken { get; set; } = "refresh-1";
    public int Renewals { get; private set; }

    /// <summary>A sign-in this fake accepts.</summary>
    public CodexSignIn SignIn => new(AccessToken, RefreshToken, "acct-1");

    /// <summary>An unsigned JWT with the claims the app reads: when it expires and the ChatGPT account.</summary>
    public static string Jwt(DateTimeOffset expires, string account)
    {
        static string B64(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var claims = "{\"exp\":" + expires.ToUnixTimeSeconds() + ",\"https://api.openai.com/auth\":{\"chatgpt_account_id\":\"" + account + "\"}}";
        return B64("""{"alg":"none"}""") + "." + B64(claims) + ".sig";
    }

    static string Sse(object data) => "data: " + System.Text.Json.JsonSerializer.Serialize(data) + "\n\n";

    protected override async Task Handle(Request r, HttpListenerResponse response)
    {
        if (r.Method == "POST" && r.Path == "/oauth/token")
        {
            using var doc = System.Text.Json.JsonDocument.Parse(r.Body);
            if (doc.RootElement.GetProperty("refresh_token").GetString() != RefreshToken || doc.RootElement.GetProperty("grant_type").GetString() != "refresh_token")
            {
                await Write(response, 400, "application/json", """{"error":"invalid_grant","error_description":"refresh_token_reused"}""");
                return;
            }
            Renewals++;
            AccessToken = Jwt(DateTimeOffset.UtcNow.AddDays(10), "acct-1");
            RefreshToken = "refresh-" + (Renewals + 1);
            await Write(response, 200, "application/json", System.Text.Json.JsonSerializer.Serialize(new { access_token = AccessToken, refresh_token = RefreshToken, id_token = AccessToken }));
            return;
        }
        if (r.Method != "POST" || !r.Path.EndsWith("/responses")) { await Write(response, 404, "text/plain", ""); return; }

        if (ReplyDelayMs > 0) await Task.Delay(ReplyDelayMs);
        if (!r.Headers.TryGetValue("Authorization", out var auth) || auth != "Bearer " + AccessToken)
        {
            await Write(response, 401, "application/json", """{"detail":"Could not parse your authentication token."}""");
            return;
        }
        if (FailStatus != 0)
        {
            await Write(response, FailStatus, "application/json", """{"error":{"message":"The usage limit has been reached","type":"usage_limit_reached"}}""");
            return;
        }

        var sb = new StringBuilder();
        sb.Append(Sse(new { type = "response.created", response = new { id = "resp_1" } }));
        if (AskForTool && !r.Body.Contains("\"function_call_output\""))
        {
            // Some reasoning (kept encrypted, to be sent back), then a call to the calendar.
            var reasoning = new { type = "reasoning", id = "rs_1", summary = Array.Empty<object>(), encrypted_content = "enc-reasoning-1" };
            sb.Append(Sse(new { type = "response.output_item.added", item = reasoning }));
            sb.Append(Sse(new { type = "response.output_item.done", item = reasoning }));
            sb.Append(Sse(new { type = "response.output_item.added", item = new { type = "function_call", id = "fc_1", call_id = "call_1", name = ToolName, arguments = "" } }));
            sb.Append(Sse(new { type = "response.output_item.done", item = new { type = "function_call", id = "fc_1", call_id = "call_1", name = ToolName, arguments = ToolArguments } }));
        }
        else
        {
            sb.Append(Sse(new { type = "response.output_item.added", item = new { type = "message", id = "msg_1", role = "assistant", content = Array.Empty<object>() } }));
            foreach (var piece in Chunks(FinalText)) sb.Append(Sse(new { type = "response.output_text.delta", delta = piece }));
            if (FailCode is not null)
            {
                sb.Append(Sse(new { type = "response.failed", response = new { error = new { code = FailCode, message = "Something went wrong (" + FailCode + ")." } } }));
                await Write(response, 200, "text/event-stream", sb.ToString());
                return;
            }
            sb.Append(Sse(new { type = "response.output_item.done", item = new { type = "message", id = "msg_1", role = "assistant", content = new[] { new { type = "output_text", text = FinalText } } } }));
        }
        sb.Append(Incomplete is not null
            ? Sse(new { type = "response.incomplete", response = new { id = "resp_1", incomplete_details = new { reason = Incomplete } } })
            : Sse(new { type = "response.completed", response = new { id = "resp_1", usage = new { input_tokens = 10, output_tokens = 30 } } }));
        await Write(response, 200, "text/event-stream", sb.ToString());
    }

    static IEnumerable<string> Chunks(string text)
    {
        for (int i = 0; i < text.Length; i += 7) yield return text.Substring(i, Math.Min(7, text.Length - i));
    }
}

/// <summary>
/// An MCP server over the Streamable HTTP transport, for the client: a session id, two pages of tools (the second answered as an
/// event stream, after a log message), tool calls that echo, a token it insists on, and a way to forget the session.
/// </summary>
sealed class FakeMcp : FakeServer
{
    public string Token { get; set; } = "mcp-token";
    public string Session { get; private set; } = "session-1";
    public int Sessions { get; private set; }
    /// <summary>Answers requests in the current session with 404, as a server that restarted would.</summary>
    public bool ForgetSession { get; set; }

    static string Result(string id, string result) => "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":" + result + "}";
    static string Error(string id, string message) => "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"error\":{\"code\":-32601,\"message\":\"" + message + "\"}}";
    static string Tool(string name, string description, string properties, string? required = null) =>
        "{\"name\":\"" + name + "\",\"description\":\"" + description + "\",\"inputSchema\":{\"type\":\"object\",\"properties\":" + properties + (required is null ? "" : ",\"required\":[\"" + required + "\"]") + "}}";
    static string Text(string text) => "{\"type\":\"text\",\"text\":" + System.Text.Json.JsonSerializer.Serialize(text) + "}";

    /// <summary>The JSON-RPC answer to a request in <paramref name="body"/> for a server with one tool; "" for a notification, null if it isn't JSON-RPC.</summary>
    public static string? Answer(string body, string tool)
    {
        System.Text.Json.Nodes.JsonNode? m;
        try { m = System.Text.Json.Nodes.JsonNode.Parse(body); } catch (System.Text.Json.JsonException) { return null; }
        var method = (string?)m?["method"];
        if (method is null) return null;
        var id = m!["id"]?.ToJsonString() ?? "null";
        return method switch
        {
            "initialize" => Result(id, """{"protocolVersion":"2025-06-18","capabilities":{"tools":{}},"serverInfo":{"name":"fake","version":"1"}}"""),
            "tools/list" => Result(id, "{\"tools\":[" + Tool(tool, "Does " + tool + ".", """{"text":{"type":"string"}}""") + "]}"),
            "tools/call" => Result(id, "{\"content\":[" + Text(tool + " done") + "]}"),
            _ when method.StartsWith("notifications/") => "",
            _ => Error(id, "no such method"),
        };
    }

    protected override async Task Handle(Request r, HttpListenerResponse response)
    {
        if (r.Method != "POST" || r.Path != "/mcp") { await Write(response, 404, "text/plain", ""); return; }
        if (!r.Headers.TryGetValue("Authorization", out var auth) || auth != "Bearer " + Token) { await Write(response, 401, "text/plain", ""); return; }
        var m = System.Text.Json.Nodes.JsonNode.Parse(r.Body)!;
        var method = (string?)m["method"];
        var id = m["id"]?.ToJsonString() ?? "null";
        if (method == "initialize")
        {
            Sessions++;
            Session = "session-" + Sessions;
            ForgetSession = false;
            response.Headers["Mcp-Session-Id"] = Session;
            await Write(response, 200, "application/json", Result(id, """{"protocolVersion":"2025-03-26","capabilities":{"tools":{}},"serverInfo":{"name":"notes","version":"1"}}"""));
            return;
        }
        if (!r.Headers.TryGetValue("Mcp-Session-Id", out var session) || session != Session || ForgetSession) { await Write(response, 404, "text/plain", "no such session"); return; }
        switch (method)
        {
            case "notifications/initialized":
                await Write(response, 202, "text/plain", "");
                break;
            case "tools/list" when (m["params"]?["cursor"]) is null:
                await Write(response, 200, "application/json", Result(id, "{\"tools\":[" + Tool("add note", "Adds a note.", """{"text":{"type":"string"}}""", "text") + "],\"nextCursor\":\"page-2\"}"));
                break;
            case "tools/list":
                // The second page comes as an event stream, with a log message before the answer.
                await Write(response, 200, "text/event-stream",
                    "event: message\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/message\",\"params\":{\"level\":\"info\",\"data\":\"listing\"}}\n\n" +
                    "event: message\ndata: " + Result(id, "{\"tools\":[" + Tool("search", "Searches the notes.", """{"q":{"type":"string"}}""") + "]}") + "\n\n");
                break;
            case "tools/call":
                var name = (string?)m["params"]?["name"];
                var args = m["params"]?["arguments"]?.ToJsonString() ?? "{}";
                await Write(response, 200, "application/json", name == "search"
                    ? Result(id, "{\"content\":[" + Text("nothing found") + "],\"isError\":true}")
                    : Result(id, "{\"content\":[" + Text("added: ") + "," + Text(args) + ",{\"type\":\"image\",\"data\":\"AAAA\",\"mimeType\":\"image/png\"}]}"));
                break;
            default:
                await Write(response, 200, "application/json", Error(id, "no such method"));
                break;
        }
    }
}

/// <summary>A stand-in for the MCP registry: one page of servers, then a second one after the cursor.</summary>
sealed class FakeRegistry : FakeServer
{
    protected override async Task Handle(Request r, HttpListenerResponse response)
    {
        if (r.Path != "/v0/servers") { await Write(response, 404, "text/plain", ""); return; }
        if (r.Query.Contains("cursor=next-1"))
        {
            await Write(response, 200, "application/json", """{"servers":[{"server":{"name":"com.example/second","description":"Second page","remotes":[{"type":"sse","url":"https://second.example/sse"}]}}],"metadata":{"count":1}}""");
            return;
        }
        await Write(response, 200, "application/json", """
            {"servers":[
              {"server":{"name":"io.github.acme/todo-list","title":"Todo List","description":"Tasks","websiteUrl":"https://acme.example","remotes":[{"type":"streamable-http","url":"https://mcp.acme.example/mcp"}]},
               "_meta":{"io.modelcontextprotocol.registry/official":{"status":"active"}}},
              {"server":{"name":"com.example/tenant","description":"Needs a tenant","remotes":[{"type":"streamable-http","url":"https://{tenant}.example.com/mcp","variables":{"tenant":{"description":"Your tenant","isRequired":true,"choices":["eu","us"],"default":"eu"}}}]}},
              {"server":{"name":"com.example/keyed","description":"Bearer token","remotes":[{"type":"streamable-http","url":"https://keyed.example/mcp","headers":[{"name":"Authorization","isRequired":true,"isSecret":true,"value":"Bearer {api_key}","variables":{"api_key":{"isSecret":true}}}]}]}},
              {"server":{"name":"com.example/optional-key","description":"Optional token","remotes":[{"type":"streamable-http","url":"https://opt.example/mcp","headers":[{"name":"Authorization","isSecret":true}]}]}},
              {"server":{"name":"com.example/custom-header","description":"Wants X-Api-Key","remotes":[{"type":"streamable-http","url":"https://hdr.example/mcp","headers":[{"name":"X-Api-Key","isRequired":true}]}]}},
              {"server":{"name":"com.example/local-only","description":"Runs on your PC","packages":[{"registryType":"npm","identifier":"x","transport":{"type":"stdio"}}]}},
              {"server":{"name":"com.example/private","description":"On a LAN","remotes":[{"type":"streamable-http","url":"https://192.168.1.5/mcp"}]}},
              {"server":{"name":"com.example/plain-http","description":"No TLS","remotes":[{"type":"streamable-http","url":"http://insecure.example/mcp"}]}},
              {"server":{"name":"com.example/old","description":"Deleted","remotes":[{"type":"streamable-http","url":"https://old.example/mcp"}]},"_meta":{"io.modelcontextprotocol.registry/official":{"status":"deleted"}}},
              {"server":{"name":"io.github.acme/todo-list","description":"Duplicate version","remotes":[{"type":"streamable-http","url":"https://mcp.acme.example/mcp"}]}}
            ],"metadata":{"nextCursor":"next-1","count":10}}
            """);
    }
}
