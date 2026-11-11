using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Longhand.Services;

/// <summary>
/// Checks for answering through a ChatGPT subscription (the Codex backend): reading the pasted sign-in, what a request looks
/// like, renewing the sign-in, and what the page is told when something goes wrong. Against a fake of the backend: the real one
/// can't be reached from the checks.
/// </summary>
static class CodexChecks
{
    static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    static async Task<Exception?> Fails(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception ex) { return ex; }
    }

    public static async Task RunAsync(Action<string, bool> check)
    {
        // ----- the pasted sign-in -----
        var access = FakeCodex.Jwt(DateTimeOffset.UtcNow.AddDays(9), "acct-from-token");
        var whole = $$"""{"OPENAI_API_KEY":null,"tokens":{"id_token":"{{access}}","access_token":"{{access}}","refresh_token":"rt-1","account_id":"acct-9"},"last_refresh":"2026-11-01T10:00:00Z"}""";
        check("the whole auth.json is read", CodexSignIn.Parse(whole) is { AccessToken: var a, RefreshToken: "rt-1", AccountId: "acct-9" } && a == access);
        check("without an account id, it is read from the token", CodexSignIn.Parse($$"""{"access_token":"{{access}}","refresh_token":"rt"}""")?.AccountId == "acct-from-token");
        check("an API key alone, or anything else, isn't a sign-in", CodexSignIn.Parse("""{"OPENAI_API_KEY":"sk-x","tokens":null}""") is null && CodexSignIn.Parse("sk-ant-123") is null && CodexSignIn.Parse("") is null);
        var stored = CodexSignIn.Parse(whole)!;
        check("what is stored reads back the same", CodexSignIn.Parse(stored.ToJson()) == stored && !stored.ToJson().Contains("id_token"));
        check("a token running out within minutes is renewed first; a fresh one isn't", new CodexSignIn(FakeCodex.Jwt(DateTimeOffset.UtcNow.AddMinutes(2), "a"), "r", "a").NeedsRefresh(DateTimeOffset.UtcNow)
            && !stored.NeedsRefresh(DateTimeOffset.UtcNow) && !(stored with { RefreshToken = "" }).NeedsRefresh(DateTimeOffset.UtcNow.AddDays(30)));

        // ----- what a request looks like -----
        {
            await using var api = new FakeCodex { AskForTool = false };
            var service = new CodexService(api.SignIn, model: "gpt-6.1-terra", baseUrl: api.BaseUrl);
            var result = await service.AskAsync(Png, new[] { new Turn("first", "One.") }, _ => { }, default, typed: "and this", cacheKey: "conv-1");
            var request = api.Requests.Single(r => r.Path.EndsWith("/responses"));
            check("it goes to the Codex backend's responses, as a stream", request.Path == "/responses" && request.Headers["Accept"].Contains("text/event-stream"));
            check("with the access token and the ChatGPT account", request.Headers["Authorization"] == "Bearer " + api.AccessToken && request.Headers["chatgpt-account-id"] == "acct-1");
            check("saying it is Longhand, not posing as the Codex CLI", request.Headers["originator"] == "longhand" && !request.Body.Contains("codex_cli_rs") && request.Headers["User-Agent"].StartsWith("Longhand"));

            var body = JsonDocument.Parse(request.Body).RootElement;
            check("nothing is stored on OpenAI's side, and the reasoning comes back encrypted", !body.GetProperty("store").GetBoolean() && body.GetProperty("stream").GetBoolean()
                && body.GetProperty("include").EnumerateArray().Any(e => e.GetString() == "reasoning.encrypted_content"));
            check("the conversation's model, low effort, and a cache key", body.GetProperty("model").GetString() == "gpt-6.1-terra" && body.GetProperty("reasoning").GetProperty("effort").GetString() == "low"
                && body.GetProperty("prompt_cache_key").GetString() == "conv-1" && !body.TryGetProperty("instructions", out _));
            check("web search is offered", body.GetProperty("tools").EnumerateArray().Any(t => t.GetProperty("type").GetString() == "web_search"));

            var input = body.GetProperty("input").EnumerateArray().ToList();
            string Text(JsonElement message, int i) => message.GetProperty("content")[i].GetProperty("text").GetString()!;
            check("the instructions come first, as a developer message, with nothing that changes in them", input[0].GetProperty("role").GetString() == "developer" && Text(input[0], 0).Contains("HEARD:") && !Text(input[0], 0).Contains("It is now"));
            check("the earlier page follows as text", input[1].GetProperty("role").GetString() == "user" && Text(input[1], 0).Contains("first")
                && input[2].GetProperty("role").GetString() == "assistant" && input[2].GetProperty("content")[0].GetProperty("type").GetString() == "output_text" && Text(input[2], 0) == "HEARD: first\n---\nOne.");
            var newest = input[^1].GetProperty("content").EnumerateArray().ToList();
            check("then the handwriting as a picture, the typed words, and the time last", newest[0].GetProperty("type").GetString() == "input_image" && newest[0].GetProperty("image_url").GetString()!.StartsWith("data:image/png;base64,")
                && newest.Any(c => c.TryGetProperty("text", out var t) && t.GetString()!.Contains("and this")) && newest[^1].GetProperty("text").GetString()!.StartsWith("It is now"));
            check("and the streamed reply is read", result.Outcome == ReplyOutcome.Done && result.Heard == "what is on tomorrow" && result.Body == "You have a standup at 9.");
        }

        // ----- renewing the sign-in -----
        {
            await using var api = new FakeCodex { AskForTool = false };
            var renewed = new List<CodexSignIn>();
            var runningOut = new CodexSignIn(FakeCodex.Jwt(DateTimeOffset.UtcNow.AddMinutes(1), "acct-1"), api.RefreshToken, "acct-1");
            var service = new CodexService(runningOut, s => { renewed.Add(s); return Task.CompletedTask; }, baseUrl: api.BaseUrl, refreshUrl: api.BaseUrl + "/oauth/token");
            await service.AskAsync(Png, Array.Empty<Turn>(), _ => { }, default);
            var refresh = api.Requests.Single(r => r.Path == "/oauth/token");
            check("a sign-in running out is renewed before asking", api.Renewals == 1 && api.Requests.Last().Headers["Authorization"] == "Bearer " + api.AccessToken);
            check("renewing sends the refresh token as JSON, as the Codex CLI does", refresh.Headers["Content-Type"].StartsWith("application/json")
                && JsonDocument.Parse(refresh.Body).RootElement is var rb && rb.GetProperty("grant_type").GetString() == "refresh_token" && rb.GetProperty("refresh_token").GetString() == "refresh-1" && rb.GetProperty("client_id").GetString()!.StartsWith("app_"));
            check("and the renewed sign-in (with its new refresh token) is handed back to be stored", renewed.Count == 1 && renewed[0].RefreshToken == api.RefreshToken && renewed[0].AccessToken == api.AccessToken);

            var refused = new CodexSignIn(FakeCodex.Jwt(DateTimeOffset.UtcNow.AddDays(5), "acct-1"), api.RefreshToken, "acct-1");
            await new CodexService(refused, baseUrl: api.BaseUrl, refreshUrl: api.BaseUrl + "/oauth/token").AskAsync(Png, Array.Empty<Turn>(), _ => { }, default);
            check("a refused token is renewed once, and the request tried again", api.Renewals == 2 && api.Requests.Count(r => r.Path == "/responses" && r.Headers["Authorization"] != "Bearer " + api.AccessToken) >= 1);

            var stale = new CodexSignIn(FakeCodex.Jwt(DateTimeOffset.UtcNow.AddMinutes(1), "acct-1"), "refresh-used-elsewhere", "acct-1");
            var failure = await Fails(() => new CodexService(stale, baseUrl: api.BaseUrl, refreshUrl: api.BaseUrl + "/oauth/token").AskAsync(Png, Array.Empty<Turn>(), _ => { }, default));
            check("a refresh token already used elsewhere (the Codex CLI renewed) asks for a fresh paste", failure is CodexSignInException && failure.Message.Contains("auth.json") && failure.Message.Contains("Codex CLI"));
        }

        // ----- when something goes wrong -----
        {
            await using var api = new FakeCodex { AskForTool = false, FailStatus = 429 };
            var limited = await Fails(() => new CodexService(api.SignIn, baseUrl: api.BaseUrl).AskAsync(Png, Array.Empty<Turn>(), _ => { }, default));
            check("a usage limit says so", limited is CodexRateLimitException && limited.Message.Contains("usage limit"));
            api.FailStatus = 0;
            api.FailCode = "server_is_overloaded";
            var failed = await Fails(() => new CodexService(api.SignIn, baseUrl: api.BaseUrl).AskAsync(Png, Array.Empty<Turn>(), _ => { }, default));
            check("a failed answer passes on why", failed is InvalidOperationException && failed.Message.Contains("server_is_overloaded"));
            api.FailCode = null;
            api.Incomplete = "max_output_tokens";
            check("an answer cut short is marked so", (await new CodexService(api.SignIn, baseUrl: api.BaseUrl).AskAsync(Png, Array.Empty<Turn>(), _ => { }, default)).Outcome == ReplyOutcome.Truncated);
            api.Incomplete = "content_filter";
            check("an answer withheld is a refusal", (await new CodexService(api.SignIn, baseUrl: api.BaseUrl).AskAsync(Png, Array.Empty<Turn>(), _ => { }, default)).Outcome == ReplyOutcome.Refused);
        }
    }
}
