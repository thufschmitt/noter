using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Longhand.Services;

/// <summary>
/// Checks for connected apps now that Longhand is the MCP client itself: sessions, listing and calling tools over Streamable
/// HTTP, naming them for the model, leaving out apps that can't be reached, and a tool call going all the way through an answer.
/// </summary>
static class McpChecks
{
    static IReadOnlyDictionary<string, JsonElement> Args(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    public static async Task RunAsync(Action<string, bool> check)
    {
        check("tool names say whose they are, in what the model accepts", McpTool.ToolName("Todo List", "add task") == "todo_list__add_task" && McpTool.ToolName("Ünïcode!", "x.y") == "n_code__x_y"
            && McpTool.ToolName(new string('a', 50), new string('b', 50)).Length == 64 && McpTool.ToolName("a", "b", 2) == "a__b_2");

        await using var server = new FakeMcp();
        var notes = new Connector("Notes", server.BaseUrl + "/mcp", server.Token);

        // ----- the client -----
        var client = new McpClient(notes);
        var tools = await client.ListToolsAsync(default);
        var init = server.Requests.First();
        check("a session is started: initialize, then the initialized notification", init.Body.Contains("\"initialize\"") && init.Body.Contains(McpClient.ProtocolVersion) && server.Requests[1].Body.Contains("notifications/initialized"));
        check("every request carries the app's token and accepts JSON or a stream", server.Requests.All(r => r.Headers["Authorization"] == "Bearer " + server.Token && r.Headers["Accept"].Contains("text/event-stream") && r.Headers["Accept"].Contains("application/json")));
        check("later requests carry the session and the version the server chose", server.Requests.Skip(1).All(r => r.Headers["Mcp-Session-Id"] == "session-1" && r.Headers["MCP-Protocol-Version"] == "2025-03-26"));
        check("tools are listed across pages, the second sent as an event stream after a log message", tools.Select(t => t.Name).SequenceEqual(new[] { "add note", "search" }) && tools[0].InputSchema.GetProperty("required")[0].GetString() == "text");

        var added = await client.CallAsync("add note", Args("""{"text":"milk"}"""), default);
        check("a call sends the arguments and gives back the text, saying what else there was", added.Contains("added:") && added.Contains("milk") && added.Contains("[an image, not shown]"));
        check("a tool's own error comes back as one", (await client.CallAsync("search", Args("{}"), default)).StartsWith("Error: nothing found"));

        server.ForgetSession = true;
        var again = await client.CallAsync("add note", Args("""{"text":"eggs"}"""), default);
        check("a session the server forgot is started again, and the call goes through", again.Contains("eggs") && server.Sessions == 2);

        var refused = new McpClient(notes with { Token = "wrong" });
        string? refusal = null;
        try { await refused.ListToolsAsync(default); } catch (InvalidOperationException ex) { refusal = ex.Message; }
        check("a refused token says to sign in again", refusal?.Contains("Sign in to it again") == true);

        // ----- the tools offered to the model -----
        McpTool.Forget();
        var down = new Connector("Down", "http://127.0.0.1:1/mcp", null);
        var (offered, unreachable) = await McpTool.ForAsync(new[] { notes, down }, default);
        check("each app's tools are offered under its name; an app that can't be reached is named, not fatal", offered.Select(t => t.Name).SequenceEqual(new[] { "notes__add_note", "notes__search" }) && unreachable.SequenceEqual(new[] { "Down" }));
        check("they say whose they are and what they need", offered[0].Definition.Description.StartsWith("[Notes]") && offered[0].Definition.Parameters.GetProperty("properties").TryGetProperty("text", out _) && offered[0].Status == "asking Notes…");
        int before = server.Requests.Count;
        await McpTool.ForAsync(new[] { notes }, default);
        check("the tool lists are kept a while, so each answer doesn't start every session over", server.Requests.Count == before);

        // ----- through an answer -----
        await using var api = new FakeCodex { ToolName = "notes__add_note", ToolArguments = """{"text":"bread"}""", FinalText = "HEARD: add bread\n---\nAdded bread to your notes." };
        var statuses = new List<string>();
        var service = new CodexService(api.SignIn, agentTools: offered, baseUrl: api.BaseUrl);
        var result = await service.AskAsync(null, Array.Empty<Turn>(), _ => { }, default, statuses.Add, typed: "add bread");
        var second = api.Requests.Where(r => r.Path == "/responses").Last().Body;
        check("the model's call reaches the app, and its answer goes back", second.Contains("function_call_output") && second.Contains("bread") && server.Requests.Any(r => r.Body.Contains("\"tools/call\"") && r.Body.Contains("bread")));
        check("the page says which app is being asked, and the reply follows", statuses.Contains("asking Notes…") && result.Body == "Added bread to your notes.");
        check("the app's tools are offered as functions, next to web search", api.Requests.First(r => r.Path == "/responses").Body.Contains("\"notes__add_note\""));
    }
}
