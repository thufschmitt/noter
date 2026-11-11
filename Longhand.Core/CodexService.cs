using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Longhand.Services;

/// <summary>One finished exchange on the page: what the user wrote (transcribed) and what the model wrote back.</summary>
public sealed record Turn(string Heard, string Reply);

public enum ReplyOutcome { Done, Truncated, Refused }

public sealed record ReplyResult(ReplyOutcome Outcome, string Heard, string Body, IReadOnlyList<string> Drawings);

/// <summary>ChatGPT is limiting requests (the plan's usage, or too many at once).</summary>
public sealed class CodexRateLimitException : Exception
{
    public CodexRateLimitException(string message) : base(message) { }
}

/// <summary>
/// Sends the newest handwriting (as a PNG) plus a text transcript of earlier turns to an OpenAI model through the user's
/// ChatGPT subscription, the way the Codex CLI does (the Responses API at chatgpt.com's Codex backend), and streams the reply
/// back as it is written. The app's own tools (the calendar) run here between rounds; web search runs on OpenAI's side.
/// This backend isn't a public API: it may change without notice.
/// </summary>
public sealed class CodexService
{
    public const string DefaultBaseUrl = "https://chatgpt.com/backend-api/codex";
    const int MaxTurns = 12;
    const int MaxToolRounds = 6;

    const string SystemPrompt = """
        You are writing back inside a handwritten notebook. Each new user message carries an image of what the user just wrote by hand with a stylus: dark ink on white. It may be a question, a note, some math, a list, or a sketch or diagram. Earlier messages are text transcriptions of what they wrote before, with your replies.

        Reply in exactly this format:
        HEARD: <a one-line transcription of the new handwriting; describe any drawing briefly in [brackets]>
        ---
        <your reply>

        Rules for your reply:
        - It will be shown in a handwriting font on ruled notebook lines, so write plain text only. No markdown, no asterisks, no headings, no code fences, no LaTeX.
        - Write math with Unicode symbols (x², √2, π, ≤, ≠, →, ∫, ½). For lists, start lines with "–" or "1.".
        - Be brief, like a note written back on the page: usually one to four short sentences. Go longer only when the user asks for detail or a worked solution.
        - Answer the newest handwriting, using the earlier page for context. If the ink is unreadable, say what you can make out and ask the user to rewrite it.
        - Sometimes the image also shows earlier parts of the page, with the newest handwriting in black on top: grey is what I wrote before and blue is your earlier replies. The black ink is then usually an annotation of that content (circling, striking out, a correction, a question in the margin). Answer the annotation, and in HEARD describe it and what it points at in [brackets], followed by any words I wrote. Your reply is written at the end of the page, away from the annotation, so say which part you mean when it isn't obvious.
        - You can search the web, and may have tools for the user's own data. Use them when they would make the answer better or when the user asks for something current or personal. If you use any tool, call it before writing anything else: write no text until all tool use is finished, then give the reply in the format above.
        - You can show things that words in lines can't: a table, a layout in columns, set-out math, a diagram or sketch. Put each after your words as a block of static HTML, up to three: <figure aria-label="what it shows in a few words">…</figure>. Inside, use plain HTML (table, ul, ol, p, h3, strong, em, mark, sub, sup, hr, blockquote) and CSS in style attributes or a <style> element. No scripts, no images, no links, no forms, no external anything, no code fences: only what is in the block is shown. It already looks like the page: the handwriting font at 26px on 40px lines, in the ink colour, on a clear background, and tables get ruled borders. Style it further only when it helps, and use currentColor for the ink.
        - For a picture (a diagram, sketch, chart, floor plan, map or geometry figure), put an inline SVG in the figure: <svg viewBox="0 0 400 300">…</svg> with a width between 300 and 600 and a height of at most 450, and a <title> that says what it shows. Draw like a pen sketch: stroke="currentColor" fill="none" stroke-width="2" to "3" stroke-linecap="round" stroke-linejoin="round"; use fill="currentColor" with an opacity for shading; no background rectangle. Label with <text font-size="20" fill="currentColor">. Keep it simple and uncluttered, and don't repeat what a block shows in your words. Use blocks only when they help: most replies are just words.
        - Sometimes my newest note is typed text instead of handwriting, or comes with some. Treat typed text like handwriting: answer it the same way, and in HEARD repeat what I typed (shortened if it is long) rather than describing it.
        - I may have defined personal symbols, listed in my message with their meaning and sometimes my own drawing of each. A symbol in my handwriting is a shortcut: do what its meaning says, using the words written next to it, or the whole note when the symbol stands alone, and use your tools to do it. In HEARD, write each symbol you see in brackets, like [envelope symbol], followed by any words. Only act when you are reasonably sure it is one of my symbols; if you aren't, say what you think you saw and ask, and do nothing else.
        - Some tools read or change the user's own data, such as their calendar. Read it when the question is about it. Add, change or delete something only when I asked for that (in words or with one of my symbols); before changing or deleting an event, read the calendar to find it, and if it isn't clear which one I mean, ask instead. Say in your reply what you added, changed or deleted.
        """;

    static readonly HttpClient SharedHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    CodexSignIn _signIn;
    readonly Func<CodexSignIn, Task>? _onRenewed;
    readonly string _model;
    readonly IReadOnlyList<IAgentTool> _agentTools;
    readonly TimeZoneInfo _zone;
    readonly string _baseUrl, _refreshUrl;
    readonly HttpClient _http;
    readonly string _session = Guid.NewGuid().ToString();

    /// <param name="onRenewed">Called with the renewed sign-in when the access token had to be refreshed, so it can be stored.</param>
    /// <param name="model">One of <see cref="ModelChoice.All"/>; null means the default.</param>
    /// <param name="agentTools">Tools the app runs for the model, such as the calendar.</param>
    /// <param name="zone">The user's time zone, for "now" in the prompt; this machine's when null. A server passes the browser's.</param>
    /// <param name="baseUrl">Only for tests against a local fake; the app leaves it null.</param>
    public CodexService(CodexSignIn signIn, Func<CodexSignIn, Task>? onRenewed = null, string? model = null, IReadOnlyList<IAgentTool>? agentTools = null,
        TimeZoneInfo? zone = null, string? baseUrl = null, string? refreshUrl = null, HttpClient? http = null)
    {
        _signIn = signIn;
        _onRenewed = onRenewed;
        _model = ModelChoice.Resolve(model);
        _agentTools = agentTools ?? Array.Empty<IAgentTool>();
        _zone = zone ?? TimeZoneInfo.Local;
        _baseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
        _refreshUrl = refreshUrl ?? CodexSignIn.RefreshUrl;
        _http = http ?? SharedHttp;
    }

    /// <summary>The model this service asks.</summary>
    public string Model => _model;

    public async Task<ReplyResult> AskAsync(byte[]? png, IReadOnlyList<Turn> history, Action<ReplyParser.Parsed> onProgress, CancellationToken ct,
        Action<string>? onStatus = null, bool hasContext = false, IReadOnlyList<LegendEntry>? legend = null, string? typed = null, string? cacheKey = null)
    {
        var input = new JsonArray { Message("developer", Text(SystemPrompt)) };
        foreach (var turn in history.Skip(Math.Max(0, history.Count - MaxTurns)))
        {
            input.Add(Message("user", Text($"(earlier handwriting, transcribed) {turn.Heard}")));
            input.Add(Message("assistant", new JsonObject { ["type"] = "output_text", ["text"] = $"HEARD: {turn.Heard}\n---\n{turn.Reply}" }));
        }

        var content = new List<JsonNode>();
        // My own symbols come first, so the picture of the handwriting that follows is read with them in mind.
        if (legend is { Count: > 0 })
        {
            content.Add(Text("My personal symbols. When my newest handwriting contains one of these, it means what is written here:"));
            foreach (var entry in legend)
            {
                content.Add(Text($"Symbol \"{entry.Name}\" means: {entry.Meaning}" + (entry.Png is null ? " (no example drawing; recognise it from its name)" : " (my own drawing of it follows)")));
                if (entry.Png is not null) content.Add(Image(entry.Png));
            }
            content.Add(Text(png is null ? "The next message is my newest note, typed." : "The next picture is my newest handwriting."));
        }
        if (png is not null)
        {
            content.Add(Image(png));
            content.Add(Text(hasContext
                ? "This is my newest handwriting: the black ink. The grey ink and blue text are earlier parts of the page that it was written over (my earlier writing and your earlier replies)."
                : "This is my newest handwriting."));
        }
        if (!string.IsNullOrWhiteSpace(typed))
            content.Add(Text((png is null ? "This is my newest note, typed on a keyboard:\n" : "I also typed this, to go with it:\n") + typed));
        // The time changes every minute, so it goes last, after what can be cached.
        content.Add(Text(Context()));
        input.Add(Message("user", content.ToArray()));

        var text = new StringBuilder();
        var outcome = ReplyOutcome.Done;
        for (int round = 0; ; round++)
        {
            text.Clear(); // only the last round's text is the reply
            var round_ = await RoundAsync(input, cacheKey, text, onProgress, onStatus, ct);
            outcome = round_.Outcome;
            var calls = round_.Items.Where(i => (string?)i["type"] == "function_call").ToList();
            if (calls.Count == 0 || outcome != ReplyOutcome.Done || round >= MaxToolRounds) break;

            // The model asked for the app's own tools: the round's items (its reasoning, kept encrypted, and the calls) go back
            // with each call's result, and it is asked again. Nothing is stored on OpenAI's side, so all of it is resent.
            foreach (var item in round_.Items) input.Add(item.DeepClone());
            foreach (var call in calls)
            {
                var name = (string?)call["name"] ?? "";
                var callId = (string?)call["call_id"] ?? "";
                input.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = callId, ["output"] = await RunToolAsync(name, (string?)call["arguments"], ct) });
            }
        }

        var final = ReplyParser.Parse(text.ToString(), finished: true);
        return new ReplyResult(outcome, final.Heard, final.Body, final.Drawings ?? Array.Empty<string>());
    }

    async Task<string> RunToolAsync(string name, string? arguments, CancellationToken ct)
    {
        var tool = _agentTools.FirstOrDefault(t => t.Name == name);
        if (tool is null) return $"Error: there is no tool called {name}.";
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "Error: the tool's input must be a JSON object.";
            var input = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
            return await tool.RunAsync(input, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException)
        {
            return "Error: the tool's input wasn't valid JSON.";
        }
        catch (Exception ex)
        {
            // The model sees what went wrong and can say so, or try again differently.
            return "Error: " + ex.Message;
        }
    }

    sealed record Round(ReplyOutcome Outcome, List<JsonNode> Items);

    /// <summary>One request: streams its text into <paramref name="text"/> and returns the items it produced.</summary>
    async Task<Round> RoundAsync(JsonArray input, string? cacheKey, StringBuilder text, Action<ReplyParser.Parsed> onProgress, Action<string>? onStatus, CancellationToken ct)
    {
        var tools = new JsonArray();
        foreach (var t in _agentTools)
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = t.Definition.Name,
                ["description"] = t.Definition.Description,
                ["strict"] = false,
                ["parameters"] = JsonNode.Parse(t.Definition.Parameters.GetRawText()),
            });
        tools.Add(new JsonObject { ["type"] = "web_search", ["external_web_access"] = true });

        var body = new JsonObject
        {
            ["model"] = _model,
            ["input"] = input.DeepClone(),
            ["tools"] = tools,
            ["tool_choice"] = "auto",
            ["parallel_tool_calls"] = false,
            // Short notebook replies: low effort keeps the wait short.
            ["reasoning"] = new JsonObject { ["effort"] = "low" },
            ["store"] = false,
            ["stream"] = true,
            ["include"] = new JsonArray("reasoning.encrypted_content"),
        };
        if (cacheKey is not null) body["prompt_cache_key"] = cacheKey;
        var json = body.ToJsonString();

        using var response = await SendAsync(json, ct);
        var items = new List<JsonNode>();
        var outcome = ReplyOutcome.Done;
        bool completed = false;

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal)) { data.Append(line.AsSpan(5).TrimStart()); continue; }
            if (line.Length != 0 || data.Length == 0) continue;
            var payload = data.ToString();
            data.Clear();
            if (payload == "[DONE]") break;
            JsonNode? ev;
            try { ev = JsonNode.Parse(payload); } catch (JsonException) { continue; }
            if (ev is null) continue;
            switch ((string?)ev["type"])
            {
                case "response.output_text.delta":
                    text.Append((string?)ev["delta"]);
                    var parsed = ReplyParser.Parse(text.ToString(), finished: false);
                    if (parsed.Ready) onProgress(parsed);
                    break;
                case "response.output_item.added":
                    if (ToolStatus(ev["item"]) is { } status)
                    {
                        // Anything written before a tool call is throwaway preamble; the real reply comes after the last one.
                        text.Clear();
                        onStatus?.Invoke(status);
                    }
                    break;
                case "response.output_item.done":
                    if (ev["item"] is JsonNode item) items.Add(item.DeepClone());
                    break;
                case "response.completed":
                    completed = true;
                    break;
                case "response.incomplete":
                    completed = true;
                    var reason = (string?)ev["response"]?["incomplete_details"]?["reason"];
                    outcome = reason == "content_filter" ? ReplyOutcome.Refused : ReplyOutcome.Truncated;
                    break;
                case "response.failed":
                    throw Failure(ev["response"]?["error"]);
                case "error":
                    throw Failure(ev["error"] ?? ev);
            }
            if (completed) break;
        }
        if (!completed) throw new InvalidOperationException("The answer stopped before it was finished. Tap Ask to try again.");
        return new Round(outcome, items);
    }

    static Exception Failure(JsonNode? error)
    {
        var code = (string?)error?["code"] ?? (string?)error?["type"] ?? "";
        var message = (string?)error?["message"] ?? "the model couldn't answer";
        return code is "rate_limit_exceeded" or "usage_limit_reached" or "insufficient_quota"
            ? new CodexRateLimitException(message)
            : new InvalidOperationException(message);
    }

    /// <summary>Sends the request, renewing the sign-in first when it has run out, or once when the server refuses it.</summary>
    async Task<HttpResponseMessage> SendAsync(string json, CancellationToken ct)
    {
        if (_signIn.NeedsRefresh(DateTimeOffset.UtcNow)) await RenewAsync(ct);
        for (int attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/responses") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _signIn.AccessToken);
            request.Headers.TryAddWithoutValidation("chatgpt-account-id", _signIn.AccountId);
            // Longhand says who it is rather than posing as the Codex CLI.
            request.Headers.TryAddWithoutValidation("originator", "longhand");
            request.Headers.UserAgent.ParseAdd("Longhand");
            request.Headers.TryAddWithoutValidation("session-id", _session);
            request.Headers.Accept.ParseAdd("text/event-stream");
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode) return response;

            var status = response.StatusCode;
            var body = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            if (status == HttpStatusCode.Unauthorized && attempt == 0 && _signIn.RefreshToken.Length > 0)
            {
                await RenewAsync(ct);
                continue;
            }
            var detail = Detail(body);
            throw status switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new CodexSignInException("ChatGPT refused the sign-in" + (detail is null ? "" : $" ({detail})") + ". Paste a fresh one from ~/.codex/auth.json (gear button)."),
                HttpStatusCode.TooManyRequests => new CodexRateLimitException(detail ?? "Too many requests right now."),
                _ => new InvalidOperationException($"ChatGPT answered {(int)status}" + (detail is null ? "." : $": {detail}")),
            };
        }
    }

    async Task RenewAsync(CancellationToken ct)
    {
        _signIn = await _signIn.RefreshAsync(_http, ct, _refreshUrl);
        if (_onRenewed is not null) await _onRenewed(_signIn);
    }

    /// <summary>The error message in an error answer's body, if it has one.</summary>
    static string? Detail(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            return (string?)node?["error"]?["message"] ?? (string?)node?["detail"] ?? (string?)node?["message"];
        }
        catch (JsonException)
        {
            return body.Length is > 0 and < 300 ? body.Trim() : null;
        }
    }

    /// <summary>A short line for the page while a tool runs, or null when the item is not a tool call.</summary>
    string? ToolStatus(JsonNode? item) => (string?)item?["type"] switch
    {
        "web_search_call" => "searching the web…",
        "function_call" => _agentTools.FirstOrDefault(t => t.Name == (string?)item?["name"])?.Status ?? "working…",
        _ => null,
    };

    static JsonObject Message(string role, params JsonNode[] content) =>
        new() { ["type"] = "message", ["role"] = role, ["content"] = new JsonArray(content) };

    static JsonObject Text(string text) => new() { ["type"] = "input_text", ["text"] = text };

    static JsonObject Image(byte[] png) => new() { ["type"] = "input_image", ["image_url"] = "data:image/png;base64," + Convert.ToBase64String(png), ["detail"] = "high" };

    /// <summary>What the model can't know otherwise: when it is.</summary>
    string Context()
    {
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _zone);
        return $"It is now {now.ToString("dddd d MMMM yyyy, HH:mm", System.Globalization.CultureInfo.InvariantCulture)} (UTC{now:zzz}, time zone {_zone.Id}).";
    }
}
