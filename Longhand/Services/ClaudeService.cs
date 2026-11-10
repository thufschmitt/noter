using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Helpers.Beta;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;

namespace Longhand.Services;

/// <summary>One finished exchange on the page: what the user wrote (transcribed) and what Claude wrote back.</summary>
public sealed record Turn(string Heard, string Reply);

public enum ReplyOutcome { Done, Truncated, Refused }

public sealed record ReplyResult(ReplyOutcome Outcome, string Heard, string Body, IReadOnlyList<string> Drawings);

/// <summary>
/// Sends the newest handwriting (as a PNG) plus a text transcript of earlier turns to Claude,
/// streaming the reply back as it is written.
/// </summary>
public sealed class ClaudeService
{
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
        - You can search the web and fetch pages, and may have connected apps. Use them when they would make the answer better or when the user asks for something current or personal. If you use any tool, call it before writing anything else: write no text until all tool use is finished, then give the reply in the format above.
        - You can draw. When a picture says it better than words (a diagram, sketch, chart, floor plan, map or geometry figure), put a complete SVG in your reply after your words, up to three. Write it as <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 400 300">…</svg> with a width between 300 and 600 and a height of at most 450, and a <title> that says in a few words what it shows. Draw like a pen sketch: stroke="currentColor" fill="none" stroke-width="2" to "3" stroke-linecap="round" stroke-linejoin="round"; use fill="currentColor" with an opacity for shading; no background rectangle, no scripts, no external images, no code fences. Label with <text font-family="Ink Free, Segoe Print, sans-serif" font-size="20" fill="currentColor">. Keep it simple and uncluttered, and don't describe the drawing at length in words. Draw only when it helps: most replies are just words.
        - I may have defined personal symbols, listed in my message with their meaning and sometimes my own drawing of each. A symbol in my handwriting is a shortcut: do what its meaning says, using the words written next to it, or the whole note when the symbol stands alone, and use your tools to do it. In HEARD, write each symbol you see in brackets, like [envelope symbol], followed by any words. Only act when you are reasonably sure it is one of my symbols; if you aren't, say what you think you saw and ask, and do nothing else.
        - Some tools read the user's own data, such as their calendar. Use them when the question is about it.
        """;

    readonly AnthropicClient _client;
    readonly IReadOnlyList<Connector> _connectors;
    readonly string _model;
    readonly IReadOnlyList<IAgentTool> _agentTools;

    /// <param name="connectors">Remote MCP servers Claude may call, on top of web search and fetch.</param>
    /// <param name="agentTools">Tools the app runs for Claude, such as reading the calendar.</param>
    /// <param name="model">One of <see cref="ModelChoice.All"/>; null means the default.</param>
    /// <param name="baseUrl">Only for tests against a local fake of the API; the app leaves it null.</param>
    public ClaudeService(string apiKey, IReadOnlyList<Connector>? connectors = null, string? model = null, string? baseUrl = null, IReadOnlyList<IAgentTool>? agentTools = null)
    {
        _agentTools = agentTools ?? Array.Empty<IAgentTool>();
        _model = ModelChoice.Resolve(model);
        _connectors = connectors ?? Array.Empty<Connector>();
        _client = baseUrl is null
            ? new AnthropicClient { ApiKey = apiKey }
            : new AnthropicClient { ApiKey = apiKey, BaseUrl = baseUrl };
    }

    public async Task<ReplyResult> AskAsync(byte[] png, IReadOnlyList<Turn> history, Action<ReplyParser.Parsed> onProgress, CancellationToken ct, Action<string>? onStatus = null, bool hasContext = false, IReadOnlyList<LegendEntry>? legend = null)
    {
        var messages = new List<BetaMessageParam>();
        foreach (var turn in history.Skip(Math.Max(0, history.Count - MaxTurns)))
        {
            messages.Add(new BetaUserMessageParam { Content = $"(earlier handwriting, transcribed) {turn.Heard}" });
            messages.Add(new BetaAssistantMessageParam { Content = $"HEARD: {turn.Heard}\n---\n{turn.Reply}" });
        }
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
            content.Add(new BetaTextBlockParam { Text = "The next picture is my newest handwriting." });
        }
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
        messages.Add(new BetaUserMessageParam { Content = content });

        var parameters = new MessageCreateParams
        {
            Model = _model,
            MaxTokens = 8000,
            System = SystemPrompt + Context(),
            Messages = messages,
            Tools = Tools(),
            McpServers = _connectors.Count == 0 ? null : _connectors.Select(c => new BetaRequestMcpServerUrlDefinition { Name = c.Name, Url = c.Url, AuthorizationToken = c.Token }).ToList(),
            // Short notebook replies: low effort keeps the wait short on the larger models.
            OutputConfig = ModelChoice.UsesLowEffort(_model) ? new BetaOutputConfig { Effort = Effort.Low } : null,
            // If a safety classifier declines, the API re-serves the request on a suitable fallback model in the same stream.
            Betas = _connectors.Count == 0
                ? [AnthropicBeta.ServerSideFallback2026_07_01]
                : [AnthropicBeta.ServerSideFallback2026_07_01, AnthropicBeta.McpClient2025_11_20],
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
    static string Context()
    {
        var now = DateTimeOffset.Now;
        return $"\n\nIt is now {now.ToString("dddd d MMMM yyyy, HH:mm", System.Globalization.CultureInfo.InvariantCulture)} (UTC{now:zzz}, time zone {TimeZoneInfo.Local.Id}).";
    }
}
