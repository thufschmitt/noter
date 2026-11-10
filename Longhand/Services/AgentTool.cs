using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anthropic.Models.Beta.Messages;

namespace Longhand.Services;

/// <summary>A tool the app runs for Claude (as opposed to the web tools, which Anthropic runs). Its result goes back to Claude as text.</summary>
public interface IAgentTool
{
    string Name { get; }

    /// <summary>What to show on the page while it runs, e.g. "checking your calendar…".</summary>
    string Status { get; }

    BetaToolUnion Definition { get; }

    /// <summary>An exception's message becomes an error result that Claude sees, so it can say what went wrong.</summary>
    Task<string> RunAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct);
}
