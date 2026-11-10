using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Longhand.Services;

/// <summary>A shortcut the user defined: a symbol they draw, and what it means. <c>HasDrawing</c> says an example drawing is saved for it.</summary>
public sealed record UserSymbol(string Id, string Name, string Meaning, bool HasDrawing);

/// <summary>What Claude is told about one symbol: its name and meaning, and the example drawing (a PNG) when there is one.</summary>
public sealed record LegendEntry(string Name, string Meaning, byte[]? Png);

/// <summary>
/// The user's symbols. Pure bookkeeping, so it can be checked without Windows; the list and the example drawings are stored
/// by SymbolStore. There are no built-in symbols: the list starts empty.
/// </summary>
public static class SymbolList
{
    public const int MaxSymbols = 20;
    public const int MaxName = 40;
    public const int MaxMeaning = 400;

    public static List<UserSymbol> Parse(string? json)
    {
        List<UserSymbol>? items;
        try { items = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<List<UserSymbol>>(json); }
        catch (JsonException) { items = null; }

        // A hand-edited or damaged file shouldn't be able to break the list.
        var seen = new HashSet<string>();
        return (items ?? new())
            .Where(s => s is not null && !string.IsNullOrEmpty(s.Id) && seen.Add(s.Id))
            .Select(s => s with { Name = Tidy(s.Name ?? ""), Meaning = Tidy(s.Meaning ?? "") })
            .Where(s => s.Name.Length > 0 && s.Meaning.Length > 0)
            .Take(MaxSymbols)
            .ToList();
    }

    public static string ToJson(IReadOnlyList<UserSymbol> symbols) => JsonSerializer.Serialize(symbols);

    /// <summary>Why a symbol can't be saved, or null when it can. <c>editingId</c> is the symbol being changed, so it doesn't clash with itself.</summary>
    public static string? Problem(IReadOnlyList<UserSymbol> symbols, string? editingId, string name, string meaning)
    {
        name = Tidy(name);
        meaning = Tidy(meaning);
        if (name.Length == 0) return "Give the symbol a name, like \"envelope\".";
        if (name.Length > MaxName) return $"Keep the name to {MaxName} characters.";
        if (meaning.Length == 0) return "Say what the symbol means.";
        if (meaning.Length > MaxMeaning) return $"Keep the meaning to {MaxMeaning} characters.";
        if (symbols.Any(s => s.Id != editingId && s.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase))) return "There is already a symbol with that name.";
        if (editingId is null && symbols.Count >= MaxSymbols) return $"That is the most symbols there can be ({MaxSymbols}). Delete one first.";
        return null;
    }

    /// <summary>Adds the symbol, or replaces the one with the same id. The caller has checked <see cref="Problem"/>.</summary>
    public static List<UserSymbol> Upsert(IReadOnlyList<UserSymbol> symbols, UserSymbol symbol)
    {
        var tidy = symbol with { Name = Tidy(symbol.Name), Meaning = Tidy(symbol.Meaning) };
        var list = symbols.ToList();
        var i = list.FindIndex(s => s.Id == symbol.Id);
        if (i >= 0) list[i] = tidy;
        else list.Add(tidy);
        return list;
    }

    public static List<UserSymbol> Remove(IReadOnlyList<UserSymbol> symbols, string id) => symbols.Where(s => s.Id != id).ToList();

    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>Single line, no runs of spaces: these go into a prompt and a list row.</summary>
    public static string Tidy(string text) => Regex.Replace(text ?? "", @"\s+", " ").Trim();
}
