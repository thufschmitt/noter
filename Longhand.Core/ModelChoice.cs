using System;
using System.Collections.Generic;
using System.Linq;

namespace Longhand.Services;

/// <summary>
/// The models the settings offer: OpenAI models a ChatGPT subscription can use through the Codex backend (from the Codex CLI's
/// own catalog). The first is the default.
/// </summary>
public static class ModelChoice
{
    public sealed record Option(string Id, string Label);

    public static readonly IReadOnlyList<Option> All = new[]
    {
        new Option("gpt-6.1-sol", "GPT-6.1 Sol"),
        new Option("gpt-6.1-terra", "GPT-6.1 Terra"),
    };

    public static string Default => All[0].Id;

    /// <summary>A saved id that is no longer offered (a Claude model, say) falls back to the default.</summary>
    public static string Resolve(string? id) => All.Any(o => o.Id == id) ? id! : Default;
}
