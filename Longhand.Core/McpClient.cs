using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Longhand.Services;

sealed class SessionLost : Exception { }

/// <summary>A tool one MCP server offers, as it described it.</summary>
public sealed record McpToolInfo(string Name, string Description, JsonElement InputSchema);

/// <summary>
/// Talks to one remote MCP server over the Streamable HTTP transport (JSON-RPC over POST; each answer comes back as JSON or as
/// a short server-sent event stream): starts a session, lists the server's tools and calls them. Longhand is the MCP client
/// itself, since ChatGPT's backend doesn't call MCP servers for it. Only what tools need: no resources, prompts or sampling.
/// </summary>
public sealed class McpClient
{
    public const string ProtocolVersion = "2025-06-18";
    static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(60) };

    readonly Connector _connector;
    readonly HttpClient _http;
    readonly SemaphoreSlim _gate = new(1, 1);
    string? _session, _version;
    bool _started;
    int _nextId;

    public McpClient(Connector connector, HttpClient? http = null)
    {
        _connector = connector;
        _http = http ?? SharedHttp;
    }

    public string Name => _connector.Name;

    /// <summary>Every tool the server offers (following its pages).</summary>
    public async Task<List<McpToolInfo>> ListToolsAsync(CancellationToken ct)
    {
        try { return await ListOnceAsync(ct); }
        catch (SessionLost) { return await ListOnceAsync(ct); }
    }

    async Task<List<McpToolInfo>> ListOnceAsync(CancellationToken ct)
    {
        await StartAsync(ct);
        var tools = new List<McpToolInfo>();
        string? cursor = null;
        for (int page = 0; page < 20; page++)
        {
            var result = await RequestAsync("tools/list", cursor is null ? new JsonObject() : new JsonObject { ["cursor"] = cursor }, ct);
            foreach (var t in result["tools"]?.AsArray() ?? new JsonArray())
            {
                var name = (string?)t?["name"];
                if (string.IsNullOrEmpty(name)) continue;
                var schema = t!["inputSchema"] is JsonObject s ? s.ToJsonString() : """{"type":"object","properties":{}}""";
                tools.Add(new McpToolInfo(name, (string?)t["description"] ?? (string?)t["title"] ?? "", JsonDocument.Parse(schema).RootElement.Clone()));
            }
            cursor = (string?)result["nextCursor"];
            if (string.IsNullOrEmpty(cursor)) break;
        }
        return tools;
    }

    /// <summary>Calls a tool and gives back what it returned, as text for the model. A tool's own error comes back marked as one.</summary>
    public async Task<string> CallAsync(string tool, IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken ct)
    {
        try { return await CallOnceAsync(tool, arguments, ct); }
        catch (SessionLost) { return await CallOnceAsync(tool, arguments, ct); }
    }

    async Task<string> CallOnceAsync(string tool, IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken ct)
    {
        await StartAsync(ct);
        var args = new JsonObject();
        foreach (var (key, value) in arguments) args[key] = JsonNode.Parse(value.GetRawText());
        var result = await RequestAsync("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = args }, ct);
        var text = ResultText(result);
        return result["isError"]?.GetValue<bool>() == true ? "Error: " + text : text;
    }

    /// <summary>The parts of a tool's result the model can read: its text, text resources, and a mention of anything else.</summary>
    static string ResultText(JsonNode result)
    {
        var parts = new List<string>();
        foreach (var item in result["content"]?.AsArray() ?? new JsonArray())
        {
            switch ((string?)item?["type"])
            {
                case "text": parts.Add((string?)item!["text"] ?? ""); break;
                case "resource": parts.Add((string?)item!["resource"]?["text"] ?? $"[resource {(string?)item["resource"]?["uri"]}]"); break;
                case "resource_link": parts.Add($"[link: {(string?)item!["name"] ?? (string?)item["uri"]} {(string?)item["uri"]}]"); break;
                case "image": parts.Add("[an image, not shown]"); break;
                case "audio": parts.Add("[audio, not played]"); break;
            }
        }
        if (parts.Count == 0 && result["structuredContent"] is JsonNode structured) parts.Add(structured.ToJsonString());
        var text = string.Join("\n", parts).Trim();
        if (text.Length == 0) text = "(the tool returned nothing)";
        return text.Length > 20000 ? text[..20000] + " …(cut)" : text;
    }

    async Task StartAsync(CancellationToken ct)
    {
        if (_started) return;
        await _gate.WaitAsync(ct);
        try
        {
            if (_started) return;
            var result = await RequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "Longhand", ["version"] = "1" },
            }, ct);
            _version = (string?)result["protocolVersion"] ?? ProtocolVersion;
            await NotifyAsync("notifications/initialized", ct);
            _started = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    HttpRequestMessage Post(JsonObject message)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _connector.Url) { Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (_connector.Token is { Length: > 0 } token) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (_session is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _session);
        if (_version is not null) request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _version);
        request.Headers.UserAgent.ParseAdd("Longhand");
        return request;
    }

    async Task NotifyAsync(string method, CancellationToken ct)
    {
        using var request = Post(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method });
        using var response = await _http.SendAsync(request, ct);
        // 202 Accepted is the answer; anything else for a notification is ignored, as servers differ.
    }

    /// <summary>
    /// Why a request was refused: what the server says in its <c>WWW-Authenticate</c> header (<c>error="invalid_token"</c>), then the
    /// message in the body (<c>{"error":{"message":…}}</c>, as Google and JSON-RPC servers send it, or its first words when it is plain
    /// text). A body that is really a successful answer (Google sends the tool list with its 401) says nothing and is left out.
    /// Empty when there is nothing to say.
    /// </summary>
    static async Task<string> ErrorDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var parts = new List<string>();
        foreach (var challenge in response.Headers.WwwAuthenticate)
        {
            var error = Regex.Match(challenge.Parameter ?? "", "error=\"([^\"]*)\"");
            var description = Regex.Match(challenge.Parameter ?? "", "error_description=\"([^\"]*)\"");
            if (error.Success) parts.Add(error.Groups[1].Value);
            if (description.Success) parts.Add(description.Groups[1].Value);
        }
        var body = await BodyDetailAsync(response, ct);
        if (body.Length > 0 && !parts.Any(p => body.Contains(p, StringComparison.OrdinalIgnoreCase))) parts.Add(body);
        return string.Join(": ", parts);
    }

    static async Task<string> BodyDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (text.Length > 2_000_000) text = text[..2_000_000]; // a tool list can be long: it is read whole to see that it is one
            try
            {
                var node = JsonNode.Parse(text);
                if (node?["result"] is JsonObject result && node["error"] is null && result["isError"]?.GetValue<bool>() != true) return "";
                var message = (string?)node?["error"]?["message"] ?? (string?)node?["message"] ?? (string?)node?["error_description"] ?? (node?["error"] as JsonValue)?.ToString()
                    ?? (string?)node?["result"]?["content"]?[0]?["text"];
                if (!string.IsNullOrWhiteSpace(message)) text = message;
            }
            catch (JsonException) { }
            text = Regex.Replace(Regex.Replace(text, "<[^>]+>", " "), @"\s+", " ").Trim();
            return text.Length > 240 ? text[..240] + "…" : text;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return "";
        }
    }

    async Task<JsonNode> RequestAsync(string method, JsonObject parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        using var request = Post(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // What the server says is what tells a token that has run out from one the server won't accept (a client it doesn't trust, a missing scope).
            var why = await ErrorDetailAsync(response, ct);
            throw new InvalidOperationException($"{_connector.Name} refused the sign-in" + (why.Length > 0 ? $" ({why})" : "") + ". Sign in to it again in the connected apps.");
        }
        if (response.StatusCode == HttpStatusCode.NotFound && _session is not null)
        {
            // The server forgot the session: the caller starts a new one and tries again.
            _session = null;
            _version = null;
            _started = false;
            throw new SessionLost();
        }
        if (!response.IsSuccessStatusCode)
        {
            var why = await ErrorDetailAsync(response, ct);
            throw new InvalidOperationException($"{_connector.Name} answered {(int)response.StatusCode}" + (why.Length > 0 ? $": {why}" : "") + ".");
        }
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessions)) _session ??= sessions.FirstOrDefault();

        JsonNode? message = null;
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            var data = new StringBuilder();
            while (message is null && await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.StartsWith("data:", StringComparison.Ordinal)) { data.Append(line.AsSpan(5).TrimStart()); continue; }
                if (line.Length != 0 || data.Length == 0) continue;
                JsonNode? candidate = null;
                try { candidate = JsonNode.Parse(data.ToString()); } catch (JsonException) { }
                data.Clear();
                // Other messages (progress, logs, requests to the client) may come first: wait for the answer to this one.
                if (candidate?["id"] is JsonNode cid && cid.ToJsonString() == id.ToString() && (candidate["result"] is not null || candidate["error"] is not null)) message = candidate;
            }
        }
        else
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            try { message = JsonNode.Parse(text); } catch (JsonException) { }
            if (message is JsonArray batch) message = batch.FirstOrDefault(m => m?["id"]?.ToJsonString() == id.ToString());
        }
        if (message is null) throw new InvalidOperationException($"{_connector.Name} gave no answer to {method}.");
        if (message["error"] is JsonNode error) throw new InvalidOperationException($"{_connector.Name}: {(string?)error["message"] ?? "error"}");
        return message["result"] ?? new JsonObject();
    }
}

/// <summary>One tool of a connected app, offered to the model under a name that says whose it is (<c>todoist__add_task</c>).</summary>
public sealed class McpTool : IAgentTool
{
    readonly McpClient _client;
    readonly McpToolInfo _info;

    McpTool(McpClient client, McpToolInfo info, string name)
    {
        _client = client;
        _info = info;
        Name = name;
    }

    public string Name { get; }
    public string Status => $"asking {_client.Name}…";

    public ToolSpec Definition => new(Name, $"[{_client.Name}] {_info.Description}".Trim(), _info.InputSchema);

    public Task<string> RunAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct) => _client.CallAsync(_info.Name, input, ct);

    // The clients and their tool lists are kept a while, so each ask doesn't start every session over.
    static readonly ConcurrentDictionary<string, (McpClient Client, List<McpToolInfo> Tools, DateTimeOffset At)> Cache = new();
    static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The tools of every connected app, named so the model can tell them apart, and the apps that couldn't be reached (an
    /// app that is down doesn't stop the answer).
    /// </summary>
    public static async Task<(List<IAgentTool> Tools, List<string> Unreachable)> ForAsync(IReadOnlyList<Connector> connectors, CancellationToken ct, HttpClient? http = null)
    {
        var (tools, failures) = await ForWithReasonsAsync(connectors, ct, http);
        return (tools, failures.Select(f => f.Name).ToList());
    }

    /// <summary>
    /// Like <see cref="ForAsync"/>, but says why each app that couldn't be reached couldn't be: what the server answered, so the user
    /// can tell a sign-in that has run out from a server that turns the app away.
    /// </summary>
    public static async Task<(List<IAgentTool> Tools, List<(string Name, string Reason)> Failures)> ForWithReasonsAsync(IReadOnlyList<Connector> connectors, CancellationToken ct, HttpClient? http = null)
    {
        var tools = new List<IAgentTool>();
        var unreachable = new List<(string Name, string Reason)>();
        var reasons = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var lists = await Task.WhenAll(connectors.Select(async c =>
        {
            var key = c.Name + "\n" + c.Url + "\n" + c.Token;
            if (Cache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < KeepFor) return (c, cached.Client, cached.Tools, Ok: true);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var client = new McpClient(c, http);
                var list = await client.ListToolsAsync(timeout.Token);
                Cache[key] = (client, list, DateTimeOffset.UtcNow);
                return (c, client, list, Ok: true);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // The reason, without the app's name in front (the list names it).
                var reason = ex is OperationCanceledException ? "it took too long to answer" : ex.Message;
                foreach (var prefix in new[] { c.Name + ": ", c.Name + " " })
                    if (reason.StartsWith(prefix, StringComparison.Ordinal)) { reason = reason[prefix.Length..]; break; }
                reasons[c.Name] = reason;
                return (c, (McpClient?)null, new List<McpToolInfo>(), Ok: false);
            }
        }));
        foreach (var (connector, client, list, ok) in lists)
        {
            if (!ok || client is null) { unreachable.Add((connector.Name, reasons.GetValueOrDefault(connector.Name, ""))); continue; }
            foreach (var info in list)
            {
                var name = ToolName(connector.Name, info.Name);
                for (int n = 2; !taken.Add(name); n++) name = ToolName(connector.Name, info.Name, n);
                tools.Add(new McpTool(client, info, name));
            }
        }
        return (tools, unreachable);
    }

    /// <summary>Forgets the kept sessions, as when the apps change.</summary>
    public static void Forget() => Cache.Clear();

    /// <summary>"Todo List" + "add task" → "todo_list__add_task": letters, digits, _ and - only, at most 64 characters.</summary>
    public static string ToolName(string app, string tool, int n = 1)
    {
        static string Clean(string s) => Regex.Replace(Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9_-]+", "_"), "_+", "_").Trim('_');
        var suffix = n > 1 ? "_" + n : "";
        var a = Clean(app);
        var t = Clean(tool);
        if (a.Length == 0) a = "app";
        if (t.Length == 0) t = "tool";
        var name = a + "__" + t;
        return name.Length + suffix.Length <= 64 ? name + suffix : name[..(64 - suffix.Length)] + suffix;
    }
}
