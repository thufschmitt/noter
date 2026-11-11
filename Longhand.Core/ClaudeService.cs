using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Core;
using Anthropic.Helpers.Beta;
using Anthropic.Models.Beta;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;

namespace Longhand.Services;

/// <summary>One finished exchange on the page: what the user wrote (transcribed) and what Claude wrote back.</summary>
public sealed record Turn(string Heard, string Reply);

public enum ReplyOutcome { Done, Truncated, Refused }

/// <param name="SummaryRejected">The API refused the page summary that was sent (it was answered without it), so it should be dropped.</param>
public sealed record ReplyResult(ReplyOutcome Outcome, string Heard, string Body, IReadOnlyList<string> Drawings, bool SummaryRejected = false);

/// <summary>
/// The API's summary of the earlier page (a compaction block), kept exactly as it came back so it can be sent back verbatim,
/// and the model that wrote it: a summary is only sent to the model that made it.
/// </summary>
public sealed record PageSummary(string Model, string Block);

/// <summary>
/// Sends the newest handwriting (as a PNG) plus a text transcript of earlier turns to Claude,
/// streaming the reply back as it is written.
/// </summary>
public sealed class ClaudeService
{
    /// <summary>The most earlier turns sent after the page summary. Compaction normally keeps them below this.</summary>
    const int MaxTurns = 12;
    /// <summary>Once this many turns follow the summary, the older ones are folded into a new summary.</summary>
    public const int CompactAt = MaxTurns;
    /// <summary>How many of the latest turns stay word for word after a compaction.</summary>
    public const int KeepRecent = 4;

    const string CompactionInstructions = """
        Summarize this handwritten notebook page so the conversation can continue from the summary alone. Keep what the user wrote and asked, the answers given, and every fact, number, name, date, list and decision in them; what any drawings showed; what the user asked to be remembered or done; and anything still open. Write plain text, in the order things happened. Do not call tools; respond with the summary text only.
        """;
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
        - You can search the web and fetch pages, and may have connected apps. Use them when they would make the answer better or when the user asks for something current or personal. If you use any tool, call it before writing anything else: write no text until all tool use is finished, then give the reply in the format above.
        - You can show things that words in lines can't: a table, a layout in columns, set-out math, a diagram or sketch. Put each after your words as a block of static HTML, up to three: <figure aria-label="what it shows in a few words">…</figure>. Inside, use plain HTML (table, ul, ol, p, h3, strong, em, mark, sub, sup, hr, blockquote) and CSS in style attributes or a <style> element. No scripts, no images, no links, no forms, no external anything, no code fences: only what is in the block is shown. It already looks like the page: the handwriting font at 26px on 40px lines, in the ink colour, on a clear background, and tables get ruled borders. Style it further only when it helps, and use currentColor for the ink.
        - For a picture (a diagram, sketch, chart, floor plan, map or geometry figure), put an inline SVG in the figure: <svg viewBox="0 0 400 300">…</svg> with a width between 300 and 600 and a height of at most 450, and a <title> that says what it shows. Draw like a pen sketch: stroke="currentColor" fill="none" stroke-width="2" to "3" stroke-linecap="round" stroke-linejoin="round"; use fill="currentColor" with an opacity for shading; no background rectangle. Label with <text font-size="20" fill="currentColor">. Keep it simple and uncluttered, and don't repeat what a block shows in your words. Use blocks only when they help: most replies are just words.
        - Sometimes my newest note is typed text instead of handwriting, or comes with some. Treat typed text like handwriting: answer it the same way, and in HEARD repeat what I typed (shortened if it is long) rather than describing it.
        - I may have defined personal symbols, listed in my message with their meaning and sometimes my own drawing of each. A symbol in my handwriting is a shortcut: do what its meaning says, using the words written next to it, or the whole note when the symbol stands alone, and use your tools to do it. In HEARD, write each symbol you see in brackets, like [envelope symbol], followed by any words. Only act when you are reasonably sure it is one of my symbols; if you aren't, say what you think you saw and ask, and do nothing else.
        - Some tools read the user's own data, such as their calendar. Use them when the question is about it.
        """;

    readonly AnthropicClient _client;
    readonly IReadOnlyList<Connector> _connectors;
    readonly string _model;
    readonly IReadOnlyList<IAgentTool> _agentTools;
    readonly TimeZoneInfo _zone;

    /// <param name="connectors">Remote MCP servers Claude may call, on top of web search and fetch.</param>
    /// <param name="agentTools">Tools the app runs for Claude, such as reading the calendar.</param>
    /// <param name="zone">The user's time zone, for "now" in the prompt; this machine's when null. A server passes the browser's.</param>
    /// <param name="model">One of <see cref="ModelChoice.All"/>; null means the default.</param>
    /// <param name="baseUrl">Only for tests against a local fake of the API; the app leaves it null.</param>
    public ClaudeService(string apiKey, IReadOnlyList<Connector>? connectors = null, string? model = null, string? baseUrl = null, IReadOnlyList<IAgentTool>? agentTools = null, TimeZoneInfo? zone = null)
    {
        _zone = zone ?? TimeZoneInfo.Local;
        _agentTools = agentTools ?? Array.Empty<IAgentTool>();
        _model = ModelChoice.Resolve(model);
        _connectors = connectors ?? Array.Empty<Connector>();
        _client = baseUrl is null
            ? new AnthropicClient { ApiKey = apiKey }
            : new AnthropicClient { ApiKey = apiKey, BaseUrl = baseUrl };
    }

    /// <summary>The model this service asks.</summary>
    public string Model => _model;

    /// <param name="history">The earlier turns on the page; with a <paramref name="summary"/>, only those after the turns it covers.</param>
    /// <param name="summary">The page summary from <see cref="CompactAsync"/>, sent in place of the turns it covers. Ignored if another model made it.</param>
    public async Task<ReplyResult> AskAsync(byte[]? png, IReadOnlyList<Turn> history, Action<ReplyParser.Parsed> onProgress, CancellationToken ct, Action<string>? onStatus = null, bool hasContext = false, IReadOnlyList<LegendEntry>? legend = null, string? typed = null, PageSummary? summary = null)
    {
        if (summary is not null && summary.Model != _model) summary = null;
        bool started = false;
        try
        {
            return await AskCoreAsync(png, history, summary, p => { started = true; onProgress(p); }, ct, s => { started = true; onStatus?.Invoke(s); }, hasContext, legend, typed);
        }
        catch (AnthropicBadRequestException) when (summary is not null && !started)
        {
            // The summary was refused (a bad signature, or the tools changed since it was made): answer without it, from the turns.
            var result = await AskCoreAsync(png, history, null, onProgress, ct, onStatus, hasContext, legend, typed);
            return result with { SummaryRejected = true };
        }
    }

    async Task<ReplyResult> AskCoreAsync(byte[]? png, IReadOnlyList<Turn> history, PageSummary? summary, Action<ReplyParser.Parsed> onProgress, CancellationToken ct, Action<string>? onStatus, bool hasContext, IReadOnlyList<LegendEntry>? legend, string? typed)
    {
        var messages = Earlier(summary, history.Skip(Math.Max(0, history.Count - MaxTurns)).ToList(), cacheLast: true);
        var content = new List<BetaContentBlockParam>();
        // My own symbols come first, so the picture of the handwriting that follows is read with them in mind.
        if (legend is { Count: > 0 })
        {
            content.Add(new BetaTextBlockParam { Text = "My personal symbols. When my newest handwriting contains one of these, it means what is written here:" });
            foreach (var entry in legend)
            {
                content.Add(new BetaTextBlockParam
                {
                    Text = $"Symbol \"{entry.Name}\" means: {entry.Meaning}" + (entry.Png is null ? " (no example drawing; recognise it from its name)" : " (my own drawing of it follows)"),
                });
                if (entry.Png is not null)
                    content.Add(new BetaImageBlockParam { Source = new BetaBase64ImageSource { Data = Convert.ToBase64String(entry.Png), MediaType = MediaType.ImagePng } });
            }
            content.Add(new BetaTextBlockParam { Text = png is null ? "The next message is my newest note, typed." : "The next picture is my newest handwriting." });
        }
        if (png is not null)
        {
            content.Add(new BetaImageBlockParam
            {
                Source = new BetaBase64ImageSource { Data = Convert.ToBase64String(png), MediaType = MediaType.ImagePng },
            });
            content.Add(new BetaTextBlockParam
            {
                Text = hasContext
                    ? "This is my newest handwriting: the black ink. The grey ink and blue text are earlier parts of the page that it was written over (my earlier writing and your earlier replies)."
                    : "This is my newest handwriting.",
            });
        }
        if (!string.IsNullOrWhiteSpace(typed))
            content.Add(new BetaTextBlockParam { Text = (png is null ? "This is my newest note, typed on a keyboard:\n" : "I also typed this, to go with it:\n") + typed });
        // The time changes every minute, so it goes here, after everything cached, rather than in the system prompt.
        content.Add(new BetaTextBlockParam { Text = Context() });
        messages.Add(new BetaUserMessageParam { Content = content });

        var parameters = new MessageCreateParams
        {
            Model = _model,
            MaxTokens = 8000,
            // The system prompt never changes, so it (with the tools before it) is cached and shared by every request.
            System = new List<BetaTextBlockParam> { new() { Text = SystemPrompt, CacheControl = new BetaCacheControlEphemeral() } },
            // Automatic caching of the growing tail, so each round of tool use reads the rounds before it from the cache.
            CacheControl = new BetaCacheControlEphemeral(),
            Messages = messages,
            Tools = Tools(),
            McpServers = McpServers(),
            // Short notebook replies: low effort keeps the wait short on the larger models.
            OutputConfig = ModelChoice.UsesLowEffort(_model) ? new BetaOutputConfig { Effort = Effort.Low } : null,
            // If a safety classifier declines, the API re-serves the request on a suitable fallback model in the same stream.
            Betas = Betas(AnthropicBeta.ServerSideFallback2026_07_01, summary is not null),
            Fallbacks = new Default(),
        };

        var text = new StringBuilder();
        BetaStopReason? stop = null;

        // The runner makes the call, runs any of the app's tools that Claude asks for, and calls again with their results
        // (keeping the thinking and tool blocks it needs to), until Claude gives a reply with no more tool calls.
        var runnable = _agentTools.Select(t => (IBetaRunnableTool)new BetaRunnableTool
        {
            Name = t.Name,
            Definition = t.Definition,
            Run = async (use, toolCt) => await t.RunAsync(use.Input, toolCt),
        }).ToList();
        var runner = _client.Beta.Messages.ToolRunner(parameters, runnable, thread: false, maxIterations: MaxToolRounds);

        await foreach (var stream in runner.Streaming(ct))
        {
            text.Clear(); // only the last round's text is the reply
            await foreach (var ev in stream)
            {
                if (ev.TryPickContentBlockStart(out var blockStart) && ToolStatus(blockStart.ContentBlock) is { } status)
                {
                    // Anything written before a tool call is throwaway preamble; the real reply comes after the last one.
                    text.Clear();
                    onStatus?.Invoke(status);
                }
                else if (ev.TryPickContentBlockDelta(out var blockDelta) && blockDelta.Delta.TryPickText(out var textDelta))
                {
                    text.Append(textDelta.Text);
                    var parsed = ReplyParser.Parse(text.ToString(), finished: false);
                    if (parsed.Ready) onProgress(parsed);
                }
                else if (ev.TryPickDelta(out var messageDelta) && messageDelta.Delta.StopReason is { } reason)
                {
                    stop = reason.Value();
                }
            }
        }

        var final = ReplyParser.Parse(text.ToString(), finished: true);
        var outcome = stop switch
        {
            BetaStopReason.Refusal => ReplyOutcome.Refused,
            BetaStopReason.MaxTokens or BetaStopReason.PauseTurn => ReplyOutcome.Truncated,
            _ => ReplyOutcome.Done,
        };
        return new ReplyResult(outcome, final.Heard, final.Body, final.Drawings ?? Array.Empty<string>());
    }

    /// <summary>
    /// Folds the earlier page into a summary with the API's on-demand compaction: the previous summary, if any, and the given turns.
    /// Null when no summary came back. The request carries the same system prompt and tools as a reply, which the summary is bound to.
    /// </summary>
    public async Task<PageSummary?> CompactAsync(PageSummary? earlier, IReadOnlyList<Turn> turns, CancellationToken ct)
    {
        if (earlier is not null && earlier.Model != _model) earlier = null;
        var parameters = new MessageCreateParams
        {
            Model = _model,
            MaxTokens = 4096,
            System = new List<BetaTextBlockParam> { new() { Text = SystemPrompt, CacheControl = new BetaCacheControlEphemeral() } },
            Messages = Earlier(earlier, turns, cacheLast: false),
            Tools = Tools().Concat(_agentTools.Select(t => t.Definition)).ToList(),
            McpServers = McpServers(),
            Betas = Betas(AnthropicBeta.Compact2026_09_04, false),
            Compaction = new BetaCompactionConfig { Instructions = CompactionInstructions },
        };
        var message = await _client.Beta.Messages.Create(parameters, cancellationToken: ct);
        foreach (var block in message.Content)
            if (block.TryPickCompaction(out var compaction) && !string.IsNullOrWhiteSpace(compaction.Content))
                return new PageSummary(_model, JsonSerializer.Serialize(compaction.RawData));
        return null;
    }

    /// <summary>
    /// The earlier page as messages: the summary first (the API wants it there), then the turns after it. With
    /// <paramref name="cacheLast"/>, a cache breakpoint ends them: the next note's request starts with the same messages,
    /// so it reads them from the cache instead of paying for them again.
    /// </summary>
    static List<BetaMessageParam> Earlier(PageSummary? summary, IReadOnlyList<Turn> turns, bool cacheLast)
    {
        var messages = new List<BetaMessageParam>();
        if (summary is not null)
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(summary.Block)!;
            messages.Add(new BetaAssistantMessageParam { Content = new List<BetaContentBlockParam> { BetaCompactionBlockParam.FromRawUnchecked(raw) } });
        }
        for (var i = 0; i < turns.Count; i++)
        {
            var turn = turns[i];
            messages.Add(new BetaUserMessageParam { Content = $"(earlier handwriting, transcribed) {turn.Heard}" });
            var said = $"HEARD: {turn.Heard}\n---\n{turn.Reply}";
            var reply = cacheLast && i == turns.Count - 1
                ? new BetaTextBlockParam { Text = said, CacheControl = new BetaCacheControlEphemeral() }
                : new BetaTextBlockParam { Text = said };
            messages.Add(new BetaAssistantMessageParam { Content = new List<BetaContentBlockParam> { reply } });
        }
        return messages;
    }

    List<BetaRequestMcpServerUrlDefinition>? McpServers() =>
        _connectors.Count == 0 ? null : _connectors.Select(c => new BetaRequestMcpServerUrlDefinition { Name = c.Name, Url = c.Url, AuthorizationToken = c.Token }).ToList();

    List<ApiEnum<string, AnthropicBeta>> Betas(AnthropicBeta first, bool compacted)
    {
        var betas = new List<ApiEnum<string, AnthropicBeta>> { first };
        if (compacted) betas.Add(AnthropicBeta.Compact2026_09_04);
        if (_connectors.Count > 0) betas.Add(AnthropicBeta.McpClient2025_11_20);
        return betas;
    }

    IReadOnlyList<BetaToolUnion> Tools()
    {
        var tools = new List<BetaToolUnion>
        {
            new BetaWebSearchTool20260318 { MaxUses = 5 },
            new BetaWebFetchTool20260318 { MaxUses = 5 },
        };
        foreach (var c in _connectors) tools.Add(new BetaMcpToolset { McpServerName = c.Name });
        return tools;
    }

    /// <summary>A short line for the page while a tool runs, or null when the block is not a tool call.</summary>
    string? ToolStatus(ContentBlock block)
    {
        if (block.TryPickBetaServerToolUse(out var server))
            return server.Name.ToString() switch
            {
                "web_search" => "searching the web…",
                "web_fetch" => "reading a page…",
                _ => "working…",
            };
        if (block.TryPickBetaMcpToolUse(out var mcp)) return $"asking {mcp.ServerName}…";
        if (block.TryPickBetaToolUse(out var use)) return _agentTools.FirstOrDefault(t => t.Name == use.Name)?.Status ?? "working…";
        return null;
    }

    /// <summary>What Claude can't know otherwise: when it is.</summary>
    string Context()
    {
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _zone);
        return $"It is now {now.ToString("dddd d MMMM yyyy, HH:mm", System.Globalization.CultureInfo.InvariantCulture)} (UTC{now:zzz}, time zone {_zone.Id}).";
    }
}
