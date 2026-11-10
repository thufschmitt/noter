using System;
using System.Collections.Generic;
using System.Linq;

namespace Longhand.Services;

/// <summary>A remote MCP server Claude may call, reached by Anthropic's servers: <c>Token</c> is sent as its bearer token.</summary>
public sealed record Connector(string Name, string Url, string? Token);

/// <summary>The connectors as the settings box shows them: one per line, <c>name url [token]</c>. Lines starting with # are ignored.</summary>
public static class ConnectorList
{
    static IEnumerable<string> Lines(string? text) =>
        (text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && l[0] != '#');

    static Connector? ParseLine(string line)
    {
        var parts = line.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !Uri.TryCreate(parts[1], UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        return new Connector(parts[0], parts[1], parts.Length > 2 ? parts[2].Trim() : null);
    }

    public static List<Connector> Parse(string? text) =>
        // Server names must be unique within a request.
        Lines(text).Select(ParseLine).OfType<Connector>()
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();

    /// <summary>The names of lines that can't be used (no https URL), so the settings box can say so instead of silently ignoring them.</summary>
    public static List<string> Rejected(string? text) =>
        Lines(text).Where(l => ParseLine(l) is null).Select(l => l.Split((char[]?)null, 2)[0]).ToList();
}
