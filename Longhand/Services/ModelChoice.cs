using System;
using System.Collections.Generic;
using System.Linq;

namespace Longhand.Services;

/// <summary>The models the settings offer. Haiku is the default: it answers fastest, which suits short notes on a page.</summary>
public static class ModelChoice
{
    public sealed record Option(string Id, string Label);

    public static readonly IReadOnlyList<Option> All = new[]
    {
        new Option("claude-haiku-5-5", "Haiku 5.5 (fastest)"),
        new Option("claude-sonnet-5-5", "Sonnet 5.5"),
        new Option("claude-opus-5-5", "Opus 5.5 (most capable, slowest)"),
    };

    public static string Default => All[0].Id;

    /// <summary>A saved id that is no longer offered falls back to the default.</summary>
    public static string Resolve(string? id) => All.Any(o => o.Id == id) ? id! : Default;

    /// <summary>
    /// Whether to ask for low effort. Opus and Sonnet take it, and it keeps their wait short; Haiku is left at its defaults,
    /// since it is already the quick one and I haven't confirmed it accepts the setting.
    /// </summary>
    public static bool UsesLowEffort(string model) => !model.Contains("haiku", StringComparison.OrdinalIgnoreCase);
}
