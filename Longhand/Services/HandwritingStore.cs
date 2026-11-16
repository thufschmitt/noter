using System;
using System.Collections.Generic;
using System.Linq;
using Windows.UI.Input.Inking;

namespace Longhand.Services;

/// <summary>
/// The languages handwriting is read in: a main one and, optionally, a second, by the name of Windows' recognizer for each. Until
/// the user chooses, French then English, when they are installed (else Windows' own default).
/// </summary>
public static class HandwritingStore
{
    const string MainKey = "handwriting-main", SecondKey = "handwriting-second";
    /// <summary>Kept as the second language when the user wants only one.</summary>
    public const string None = "-";

    /// <summary>The recognizers installed on this PC; empty when there are none (or they can't be listed).</summary>
    public static IReadOnlyList<InkRecognizer> Installed()
    {
        try { return new InkRecognizerContainer().GetRecognizers().ToList(); }
        catch (Exception) { return Array.Empty<InkRecognizer>(); }
    }

    static InkRecognizer? Find(IReadOnlyList<InkRecognizer> installed, string? name) => name is null ? null : installed.FirstOrDefault(r => r.Name == name);
    static InkRecognizer? ByLanguage(IReadOnlyList<InkRecognizer> installed, string prefix) =>
        installed.FirstOrDefault(r => HandwritingChoice.LanguageOf(r.Name)?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>The recognizers to read with, the main language first: one or two.</summary>
    public static List<InkRecognizer> Chosen()
    {
        var installed = Installed();
        if (installed.Count == 0) return new();
        var settings = WindowsSettings.Local;
        var main = Find(installed, settings.Get(MainKey)) ?? ByLanguage(installed, "fr") ?? installed[0];
        var secondName = settings.Get(SecondKey);
        var second = secondName == None ? null : Find(installed, secondName) ?? (secondName is null ? ByLanguage(installed, "en") : null);
        var list = new List<InkRecognizer> { main };
        if (second is not null && second.Name != main.Name) list.Add(second);
        return list;
    }

    public static void Save(string main, string? second)
    {
        var settings = WindowsSettings.Local;
        settings.Set(MainKey, main);
        settings.Set(SecondKey, second ?? None);
    }
}
