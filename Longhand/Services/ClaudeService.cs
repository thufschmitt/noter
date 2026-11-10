using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;

namespace Longhand.Services;

/// <summary>One finished exchange on the page: what the user wrote (transcribed) and what Claude wrote back.</summary>
public sealed record Turn(string Heard, string Reply);

public enum ReplyOutcome { Done, Truncated, Refused }

public sealed record ReplyResult(ReplyOutcome Outcome, string Heard, string Body);

/// <summary>
/// Sends the newest handwriting (as a PNG) plus a text transcript of earlier turns to Claude,
/// streaming the reply back as it is written.
/// </summary>
public sealed class ClaudeService
{
    public const string ModelId = "claude-opus-5-5";
    const int MaxTurns = 12;

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
        """;

    readonly AnthropicClient _client;

    /// <param name="baseUrl">Only for tests against a local fake of the API; the app leaves it null.</param>
    public ClaudeService(string apiKey, string? baseUrl = null)
    {
        _client = baseUrl is null
            ? new AnthropicClient { ApiKey = apiKey }
            : new AnthropicClient { ApiKey = apiKey, BaseUrl = baseUrl };
    }

    public async Task<ReplyResult> AskAsync(byte[] png, IReadOnlyList<Turn> history, Action<ReplyParser.Parsed> onProgress, CancellationToken ct)
    {
        var messages = new List<BetaMessageParam>();
        foreach (var turn in history.Skip(Math.Max(0, history.Count - MaxTurns)))
        {
            messages.Add(new BetaUserMessageParam { Content = $"(earlier handwriting, transcribed) {turn.Heard}" });
            messages.Add(new BetaAssistantMessageParam { Content = $"HEARD: {turn.Heard}\n---\n{turn.Reply}" });
        }
        messages.Add(new BetaUserMessageParam
        {
            Content = new List<BetaContentBlockParam>
            {
                new BetaImageBlockParam
                {
                    Source = new BetaBase64ImageSource { Data = Convert.ToBase64String(png), MediaType = MediaType.ImagePng },
                },
                new BetaTextBlockParam { Text = "This is my newest handwriting." },
            },
        });

        var parameters = new MessageCreateParams
        {
            Model = ModelId,
            MaxTokens = 8000,
            System = SystemPrompt,
            Messages = messages,
            // Short notebook replies: low effort keeps the wait short. Thinking stays on (it can't be disabled on this model).
            OutputConfig = new BetaOutputConfig { Effort = Effort.Low },
            // If a safety classifier declines, the API re-serves the request on a suitable fallback model in the same stream.
            Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
            Fallbacks = new Default(),
        };

        var text = new StringBuilder();
        BetaStopReason? stop = null;
        await foreach (var ev in _client.Beta.Messages.CreateStreaming(parameters, ct))
        {
            if (ev.TryPickContentBlockDelta(out var blockDelta) && blockDelta.Delta.TryPickText(out var textDelta))
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

        var final = ReplyParser.Parse(text.ToString(), finished: true);
        var outcome = stop switch
        {
            BetaStopReason.Refusal => ReplyOutcome.Refused,
            BetaStopReason.MaxTokens => ReplyOutcome.Truncated,
            _ => ReplyOutcome.Done,
        };
        return new ReplyResult(outcome, final.Heard, final.Body);
    }
}
