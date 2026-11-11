using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Longhand.Services;

/// <summary>
/// Checks for prompt caching (where the cache breakpoints go, and that nothing that changes per request sits before them) and for
/// compaction (the earlier page folded into a summary that is sent back in place of the turns it covers).
/// </summary>
static class CachingChecks
{
    static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    static bool Ephemeral(JsonElement e) => e.TryGetProperty("cache_control", out var c) && c.ValueKind == JsonValueKind.Object && c.GetProperty("type").GetString() == "ephemeral";

    static IEnumerable<JsonElement> Blocks(JsonElement message) =>
        message.GetProperty("content") is { ValueKind: JsonValueKind.Array } c ? c.EnumerateArray() : Enumerable.Empty<JsonElement>();

    static List<JsonElement> Bodies(FakeAnthropic api) =>
        api.Requests.Where(r => r.Path.EndsWith("/messages")).Select(r => JsonDocument.Parse(r.Body).RootElement.Clone()).ToList();

    static string Betas(FakeAnthropic api, int i) =>
        api.Requests.Where(r => r.Path.EndsWith("/messages")).ElementAt(i).Headers.TryGetValue("anthropic-beta", out var b) ? b : "";

    public static async Task RunAsync(Action<string, bool> check)
    {
        // ----- caching -----
        {
            await using var api = new FakeAnthropic { AskForTool = false };
            var service = new ClaudeService("sk-ant-placeholder", model: "claude-haiku-5-5", baseUrl: api.BaseUrl);
            await service.AskAsync(Png, new[] { new Turn("first", "One."), new Turn("second", "Two.") }, _ => { }, default);
            var root = Bodies(api)[0];

            var system = root.GetProperty("system");
            check("the system prompt is cached", system.ValueKind == JsonValueKind.Array && system.GetArrayLength() == 1 && Ephemeral(system[0]));
            check("the system prompt holds nothing that changes, such as the time", !system[0].GetProperty("text").GetString()!.Contains("It is now"));
            check("the growing conversation is cached automatically", Ephemeral(root));

            var messages = root.GetProperty("messages").EnumerateArray().ToList();
            var marked = messages.SelectMany(Blocks).Where(Ephemeral).ToList();
            check("one breakpoint in the messages, at the end of the earlier page", marked.Count == 1 && marked[0].GetProperty("text").GetString()!.Contains("Two."));
            var last = messages[^1].GetProperty("content");
            check("the time goes last, in the newest note", last[last.GetArrayLength() - 1].GetProperty("text").GetString()!.StartsWith("It is now"));
            check("no more than four breakpoints", marked.Count + 2 <= 4);
            check("no compaction beta without a summary", !Betas(api, 0).Contains("compact-"));
        }

        // ----- compaction, in the service -----
        {
            await using var api = new FakeAnthropic { AskForTool = false };
            var service = new ClaudeService("sk-ant-placeholder", model: "claude-haiku-5-5", baseUrl: api.BaseUrl);
            var turns = Enumerable.Range(1, 8).Select(i => new Turn("note " + i, "Reply " + i + ".")).ToList();
            var summary = await service.CompactAsync(null, turns, default);
            var compactRequest = Bodies(api)[0];
            check("compaction asks the API for a summary, with its beta", compactRequest.GetProperty("compaction").GetProperty("type").GetString() == "summarize" && Betas(api, 0).Contains("compact-2026-09-04"));
            check("compaction is told what the notebook needs kept", compactRequest.GetProperty("compaction").GetProperty("instructions").GetString()!.Contains("Do not call tools"));
            check("compaction is sent every turn to fold", compactRequest.GetProperty("messages").GetArrayLength() == 16);
            check("compaction is sent the same tools a reply is", compactRequest.GetProperty("tools").GetArrayLength() == 2);
            check("the summary comes back with its model", summary is not null && summary.Model == "claude-haiku-5-5" && summary.Block.Contains(FakeAnthropic.SummaryText));

            await service.AskAsync(Png, new[] { new Turn("note 9", "Reply 9.") }, _ => { }, default, summary: summary);
            var ask = Bodies(api)[1];
            var messages = ask.GetProperty("messages").EnumerateArray().ToList();
            var block = messages[0].GetProperty("content")[0];
            check("the summary goes first, as an assistant message of its own", messages[0].GetProperty("role").GetString() == "assistant" && block.GetProperty("type").GetString() == "compaction");
            check("the summary is sent back verbatim, signature and all", block.GetProperty("content").GetString() == FakeAnthropic.SummaryText && block.GetProperty("signature").GetString() == "sig-compact" && block.GetProperty("encrypted_content").GetString() == "enc-1");
            check("only the turns after the summary follow it", messages.Count == 1 + 2 + 1 && messages[1].GetProperty("content").GetString()!.Contains("note 9"));
            check("a request with a summary carries the compaction beta", Betas(api, 1).Contains("compact-2026-09-04"));

            var other = new ClaudeService("sk-ant-placeholder", model: "claude-sonnet-5-5", baseUrl: api.BaseUrl);
            await other.AskAsync(Png, new[] { new Turn("note 9", "Reply 9.") }, _ => { }, default, summary: summary);
            check("another model's summary is not sent", !Bodies(api)[2].GetProperty("messages")[0].GetProperty("content").ToString().Contains("compaction"));
        }

        // ----- a summary the API won't take -----
        {
            await using var api = new FakeAnthropic { AskForTool = false, RejectSummary = true };
            var service = new ClaudeService("sk-ant-placeholder", model: "claude-haiku-5-5", baseUrl: api.BaseUrl);
            var summary = new PageSummary("claude-haiku-5-5", """{"type":"compaction","content":"old","signature":"bad"}""");
            var result = await service.AskAsync(Png, new[] { new Turn("note 9", "Reply 9.") }, _ => { }, default, summary: summary);
            var bodies = Bodies(api);
            check("a refused summary still gets an answer, sent without it", result.Outcome == ReplyOutcome.Done && result.Body.Length > 0 && bodies.Count == 2 && !bodies[1].GetProperty("messages").ToString().Contains("\"compaction\""));
            check("and is reported, so it can be dropped", result.SummaryRejected);
        }

        // ----- compaction, on a page -----
        {
            await using var api = new FakeAnthropic { AskForTool = false };
            var view = new FakeView();
            var data = new FakeData(view, api.BaseUrl);
            var session = new PageSession(view, data);
            await session.StartAsync();
            async Task Write()
            {
                double y = session.Notes.Where(n => !n.IsHint).Select(n => n.Bottom).DefaultIfEmpty(160).Max();
                session.StrokesCollected(new[] { view.Draw(y + 4, y + 30) });
                await session.AskAsync();
            }
            static bool IsCompaction(FakeServer.Request r) => r.Body.Contains("\"compaction\":{");

            for (int i = 0; i < ClaudeService.CompactAt - 1; i++) await Write();
            await Task.Delay(100);
            check("a short page is not compacted", !api.Requests.Any(IsCompaction) && session.Notes.All(n => n.Summary is null));

            await Write();
            for (int waited = 0; waited < 4000 && !session.Notes.Any(n => n.Summary is not null); waited += 20) await Task.Delay(20);
            var exchanges = session.Notes.Where(n => !n.IsHint).OrderBy(n => n.Y).ToList();
            int at = exchanges.FindIndex(n => n.Summary is not null);
            check("a long page is compacted after the answer, keeping the latest turns word for word", api.Requests.Count(IsCompaction) == 1 && at == ClaudeService.CompactAt - ClaudeService.KeepRecent - 1);

            await session.SaveAsync();
            var saved = data.Pages.Values.Single().Notes;
            check("the summary is saved with the note it ends at", saved.Count(n => n.Summary is not null) == 1 && saved.Single(n => n.Summary is not null).Summary!.Block.Contains("sig-compact"));

            await Write();
            var next = Bodies(api)[^1].GetProperty("messages");
            check("the next answer is sent the summary, then only the turns after it", next[0].GetProperty("content")[0].GetProperty("type").GetString() == "compaction" && next.GetArrayLength() == 1 + 2 * ClaudeService.KeepRecent + 1);
        }
    }
}
