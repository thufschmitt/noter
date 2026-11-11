using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Longhand.Services;

/// <summary>Checks for prompt caching: where the cache breakpoints go, and that nothing that changes per request sits before them.</summary>
static class CachingChecks
{
    public static async Task RunAsync(Action<string, bool> check)
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
        await using var api = new FakeAnthropic { AskForTool = false };
        var service = new ClaudeService("sk-ant-placeholder", model: "claude-haiku-5-5", baseUrl: api.BaseUrl);
        await service.AskAsync(png, new[] { new Turn("first", "One."), new Turn("second", "Two.") }, _ => { }, default);
        using var doc = JsonDocument.Parse(api.Requests.First(r => r.Path.EndsWith("/messages")).Body);
        var root = doc.RootElement;
        static bool Ephemeral(JsonElement e) => e.TryGetProperty("cache_control", out var c) && c.ValueKind == JsonValueKind.Object && c.GetProperty("type").GetString() == "ephemeral";

        var system = root.GetProperty("system");
        check("the system prompt is cached", system.ValueKind == JsonValueKind.Array && system.GetArrayLength() == 1 && Ephemeral(system[0]));
        check("the system prompt holds nothing that changes, such as the time", !system[0].GetProperty("text").GetString()!.Contains("It is now"));
        check("the growing conversation is cached automatically", Ephemeral(root));

        var messages = root.GetProperty("messages").EnumerateArray().ToList();
        var marked = messages.SelectMany(m => m.GetProperty("content") is { ValueKind: JsonValueKind.Array } c ? c.EnumerateArray() : Enumerable.Empty<JsonElement>()).Where(Ephemeral).ToList();
        check("one breakpoint in the messages, at the end of the earlier page", marked.Count == 1 && marked[0].GetProperty("text").GetString()!.Contains("Two."));
        var last = messages[^1].GetProperty("content");
        check("the time goes last, in the newest note", last[last.GetArrayLength() - 1].GetProperty("text").GetString()!.StartsWith("It is now"));
        check("no more than four breakpoints", marked.Count + 2 <= 4);
    }
}
