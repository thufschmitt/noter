using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Longhand.Services;

/// <summary>
/// Which of a provider's calendars the user chose for Longhand to use, kept with the settings as a list of calendar ids. No list
/// at all means no choice was made (each provider then falls back to what it did before: Google's own switches, or every calendar).
/// An empty list is a choice too: none.
/// </summary>
public static class CalendarChoice
{
    public static IReadOnlyCollection<string>? Read(ISettings settings, string key)
    {
        if (settings.Get(key) is not { Length: > 0 } text) return null;
        try { return JsonSerializer.Deserialize<string[]>(text)?.ToHashSet(StringComparer.Ordinal); }
        catch (JsonException) { return null; }
    }

    /// <summary>Keeps the choice; null forgets it.</summary>
    public static void Write(ISettings settings, string key, IEnumerable<string>? ids)
    {
        if (ids is null) settings.Remove(key);
        else settings.Set(key, JsonSerializer.Serialize(ids.Distinct(StringComparer.Ordinal).OrderBy(i => i, StringComparer.Ordinal).ToArray()));
    }

    /// <summary>What the model is told when the user has switched every calendar off.</summary>
    public static InvalidOperationException NoneChosen(string provider) =>
        new($"The user hasn't chosen any {provider} calendar for Longhand to use. They can pick some in the connected apps.");

    /// <summary>What the model is told about an event in a calendar that is switched off.</summary>
    public static InvalidOperationException NotChosen() =>
        new("That event is in a calendar the user hasn't chosen for Longhand to use, so it can't be changed or deleted.");
}
