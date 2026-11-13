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

public sealed record ReplyResult(ReplyOutcome Outcome, string Heard, string Body, IReadOnlyList<string> Drawings, IReadOnlyList<EraseRequest>? Erases = null);

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
        - Sometimes my newest message is only a list of changes I made by hand to your layouts (see below, when layouts can be edited), with no handwriting. Then there is nothing to transcribe: answer the changes.
        - Sometimes my newest note is typed text instead of handwriting, or comes with some. Treat typed text like handwriting: answer it the same way, and in HEARD repeat what I typed (shortened if it is long) rather than describing it.
        - I may have defined personal symbols, listed in my message with their meaning and sometimes my own drawing of each. A symbol in my handwriting is a shortcut: do what its meaning says, using the words written next to it, or the whole note when the symbol stands alone, and use your tools to do it. In HEARD, write each symbol you see in brackets, like [envelope symbol], followed by any words. Only act when you are reasonably sure it is one of my symbols; if you aren't, say what you think you saw and ask, and do nothing else.
        - Some tools read or change the user's own data, such as their calendar. Read it when the question is about it. Add, change or delete something only when I asked for that (in words or with one of my symbols); before changing or deleting an event, read the calendar to find it, and if it isn't clear which one I mean, ask instead. Say in your reply what you added, changed or deleted. A repeating event is listed once for each occurrence, each with its own {id}: changing or deleting one changes that occurrence only (the default), which is what moving or editing one instance of it means. Use scope "all" only when I clearly mean every occurrence, and say so in your reply.
        """;

    /// <summary>What the model is told about what it can put on the page besides words. There is no HTML: structure is a layout, a picture is SVG.</summary>
    const string DrawingPrompt = """
        - You can show things that words in lines can't, up to three per reply, after your words. For anything with structure (a board, a list, a table, columns, a tree, a set-out sum, an agenda or schedule) write a layout: <layout id="week" title="Week"> … </layout>. For a picture (a diagram, sketch, chart, map, floor plan, geometry figure) write an inline SVG: <svg viewBox="0 0 400 300">…</svg> with a width between 300 and 600, a height of at most 450 and a <title> that says what it shows. There is no HTML, no markdown and no code fences.
        - A layout is made of: <row> (its children side by side, sharing the width equally; add the attribute fit to give a child only the room it needs), <col> (its children one under another), <box> (a bordered card; words can go straight in it), <text> (plain words) and <rule/> (a line). title="…" on the layout, a col or a box gives it a heading. You never write sizes or coordinates: the page lays it out, in the handwriting font, and wraps the words. By default everything is in the ink colour. color="red" on any part (the layout, a col, a box, a text, a rule) changes its words and its lines (borders, rules) to that colour, and everything inside it inherits it; bg="yellow" gives a part a soft tinted background. The names are red, orange, yellow, green, teal, blue, purple, pink, brown and gray, which stay readable on a light and on a dark page; #rgb or #rrggbb also work but don't adapt to the theme. Use colour to mean something (status, priority, a warning), not to decorate, and keep most of a layout in the ink colour. A table is a col of rows of boxes. Example: <layout id="board" title="Week"><row><col id="todo" drop title="To do"><box id="c1" edit>Call Anna</box></col><col id="doing" drop title="Doing"/></row></layout>
        - Agendas and schedules: whenever you show the user events or a plan with times (a day or a week from their calendar, or one you propose), put it on a timeline. Do not write it as a list in words, a table or a picture. A timeline is a layout with one <col timeline="08:00-18:00" title="Thu 12 Nov"> for each day (several days side by side in a <row>; one day is just the col), and in each col one <box> for each event, with start="09:00" and end="09:30": the time of day in 24 hours, as HH:MM, and nothing else (no date, no seconds, no am/pm, no time zone). The page puts each box at its time, as tall as the event lasts, and puts events that overlap side by side, so you never write sizes or positions. Put the event's title in the box, as short as you can, and give each box a short id (e1, e2, …). An event with no time of day, or an all-day event, is a box with no start: it is listed above the hours. Give every col the same range (or write just timeline and the hours fit the events). After reading the calendar with a tool, put what it returned on a timeline; don't say in words what the timeline already shows. Example, two days: <layout id="agenda" title="Thu 12 – Fri 13 Nov"><row><col id="thu" timeline="08:00-18:00" title="Thu 12"><box id="e1" start="09:00" end="09:30">Standup</box><box id="e2" start="14:00" end="15:30" color="blue">Dentist</box></col><col id="fri" timeline="08:00-18:00" title="Fri 13"><box id="e3" start="10:00" end="11:00">Sprint review</box></col></row></layout>
        - A picture is a pen sketch: stroke="currentColor" fill="none" stroke-width="2" to "3" stroke-linecap="round" stroke-linejoin="round"; use fill="currentColor" with an opacity for shading; no background rectangle. Label with <text font-size="20" fill="currentColor"> (the handwriting font is already set; SVG does not wrap words, so break long lines yourself). currentColor is the ink colour, which follows the light and dark theme: keep most lines in it, and use hex colours (stroke="#C62828") only where colour means something. Don't use the colour names of layouts in SVG. Pictures can't be edited by me. Marks I write on top of your layouts and pictures (circles, ticks, crossings-out, notes) can stop making sense: when they are done, answered, or the block under them was redrawn, erase them: after your words write <erase over="board"/>, where over is the id of the block (give a block an id when you might need this). To erase only part of a block add x, y, w and h, fractions from 0 to 1 of the block measured from its top left: <erase over="board" x="0.5" y="0" w="0.5" h="0.3"/>. It takes off only my ink that lies mostly inside that region, and I can undo it. Erase only marks that are clearly pointless now, never my other writing, and say in your words what you erased. Keep everything simple and uncluttered, and don't repeat in words what a block shows. Use blocks only when they help: most replies are just words (but events and schedules always go on a timeline).
        """;

    /// <summary>Added when the user can select, move, turn and delete parts of a layout (the page says so).</summary>
    const string EditablePrompt = """
        - Layouts can be interactive. edit on a part (<box id="c1" edit>) lets me select it with my stylus, move it to another place, turn it or delete it. drop on a row, col or box (<col id="doing" drop>) lets me drop edit parts into it, empty ones too. Give every edit and drop part a short id; I can't change anything else. In a timeline col I can move an edit item to another time, drag its end to change its length, and move it to another timeline col (that col needs drop to receive items; I can always change times inside a col); I can't turn items there. When you put an agenda on a timeline, make its boxes edit and its cols drop by default, so I can reschedule things (<col id="thu" timeline="08:00-18:00" drop><box id="e1" edit start="09:00" end="09:30">Standup</box></col>). When I edit, nothing is sent until I ask: the changes stay on the page. When I ask, my message lists what I changed by id (for example: c1 moved from #todo (position 1) to #doing (position 2 of 2); c2 turned 15° clockwise; c3 deleted; e1 rescheduled from 09:00–09:30 to 10:00–10:30; e2 resized from 14:00–15:00 to 14:00–16:00; e3 moved from #thu 14:00–15:00 to #fri 10:00–11:00), and each layout in the conversation above is shown as it is now, with the changes made. In HEARD, describe the changes in [brackets] (for example [moved "Call Anna" to Doing]).
        - What my edits mean is up to you: for example, a card moved into the column of another status can mean "change its status". Act on it with your tools only when the meaning is clear; otherwise say what you think I did and ask, and change nothing. A box of an agenda stands for an event: its words are the event's title, and its id is only the layout's own (a calendar's ids aren't valid here), so when I reschedule one and you act on it, find the event again in the calendar by its title and old time before you change it (for a repeating event, that is the occurrence I moved, and only it changes). When you have acted, say what you did. To redraw a layout in place, write a new <layout> with the same id as the old one (<layout id="board">): it replaces the old one where it is, and I can undo that. Give a layout an id on its root when you expect it to change. A new id adds a new layout below.
        """;

    const string NotEditablePrompt = "- The user cannot select, move, turn or delete parts of your layouts on this device, so don't use edit or drop and don't offer to make them interactive.";

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
        Action<string>? onStatus = null, bool hasContext = false, IReadOnlyList<LegendEntry>? legend = null, string? typed = null, string? cacheKey = null,
        string? edits = null, bool canEdit = false)
    {
        var prompt = SystemPrompt.TrimEnd() + "\n" + DrawingPrompt.TrimEnd() + "\n" + (canEdit ? EditablePrompt : NotEditablePrompt).TrimEnd();
        var input = new JsonArray { Message("developer", Text(prompt)) };
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
            content.Add(Text(png is not null ? "The next picture is my newest handwriting." : typed is not null ? "The next message is my newest note, typed." : "The next message lists my newest changes to your drawings."));
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
        if (!string.IsNullOrWhiteSpace(edits))
            content.Add(Text((png is null && string.IsNullOrWhiteSpace(typed) ? "" : "Also: ") + edits));
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
        return new ReplyResult(outcome, final.Heard, final.Body, final.Drawings ?? Array.Empty<string>(), final.Erases);
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
