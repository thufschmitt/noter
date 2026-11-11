using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Longhand.Services;

/// <summary>
/// What the model is told about a tool: its name, what it does, and the JSON schema of its input. Sent as a function tool
/// (<c>{"type":"function","name",…,"parameters"}</c>).
/// </summary>
public sealed record ToolSpec(string Name, string Description, JsonElement Parameters)
{
    /// <summary>A tool whose input is an object with these properties (each a JSON schema), the named ones required.</summary>
    public static ToolSpec Object(string name, string description, IReadOnlyDictionary<string, JsonElement> properties, params string[] required)
    {
        var props = new JsonObject();
        foreach (var (key, schema) in properties) props[key] = JsonNode.Parse(schema.GetRawText());
        var schemaObject = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
            ["additionalProperties"] = false,
        };
        return new ToolSpec(name, description, JsonDocument.Parse(schemaObject.ToJsonString()).RootElement.Clone());
    }
}

/// <summary>A tool the app runs for the model (the web search runs on OpenAI's side). Its result goes back to the model as text.</summary>
public interface IAgentTool
{
    string Name { get; }

    /// <summary>What to show on the page while it runs, e.g. "checking your calendar…".</summary>
    string Status { get; }

    ToolSpec Definition { get; }

    /// <summary>An exception's message becomes an error result that the model sees, so it can say what went wrong.</summary>
    Task<string> RunAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct);
}
