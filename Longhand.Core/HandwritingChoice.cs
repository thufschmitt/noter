using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Longhand.Services;

/// <summary>
/// What one handwriting recognizer read: its language (a BCP-47 tag like fr-FR, or <see cref="HandwritingChoice.Auto"/> for a reader
/// that chooses its language itself) and, for each word it found, its guesses, best first.
/// </summary>
public sealed record HandwritingReading(string Language, IReadOnlyList<IReadOnlyList<string>> Words);

/// <summary>A way to read the handwriting: the words, how many of them are real words of its language (0 to 1), and that language.</summary>
public sealed record TitleGuess(string Text, double Score, string Language);

/// <summary>
/// Chooses between what recognizers of several languages read in the same handwriting, since none of them says how sure it is (Windows'
/// don't). Each reading is checked against a dictionary of its language: the one with the most real words wins, the user's main
/// language (the first reading) on a tie, and a word the dictionary doesn't know is swapped for the first of its other guesses that it
/// does. Readings that agree count for more.
/// </summary>
public static class HandwritingChoice
{
    public const int MaxGuesses = 5;
    /// <summary>The language of a reader that doesn't say which it read in (Windows' ink analysis): its words are checked in every language of the other readings.</summary>
    public const string Auto = "auto";

    /// <param name="readings">One per recognizer, the user's main language first.</param>
    /// <param name="known">Whether a word is a real word of a language: true, false, or null when that can't be told (no dictionary).</param>
    /// <returns>
    /// The ways to read it, best first, each once; and whether the best is sure enough to use without asking: every reading agrees, or
    /// only it is made of words the dictionary knows.
    /// </returns>
    public static (List<TitleGuess> Guesses, bool Sure) Rank(IReadOnlyList<HandwritingReading> readings, Func<string, string, bool?> known)
    {
        // A reader that chose its own language is checked against all of the others': a word is real if any of them knows it.
        var languages = readings.Select(r => r.Language).Where(l => l != Auto && l.Length > 0).Distinct().ToList();
        bool? Known(string language, string word)
        {
            if (language != Auto) return known(language, word);
            var answers = languages.Select(l => known(l, word)).ToList();
            return answers.Contains(true) ? true : answers.Contains(false) ? false : null;
        }

        var all = new List<(TitleGuess Guess, int Order, int Variant)>();
        for (int i = 0; i < readings.Count; i++)
        {
            var r = readings[i];
            var words = r.Words.Where(w => w.Count > 0 && w[0].Trim().Length > 0).ToList();
            if (words.Count == 0) continue;
            var top = words.Select(w => w[0].Trim()).ToList();
            var fixedUp = words.Select(w => w.Select(c => c.Trim()).FirstOrDefault(c => c.Length > 0 && Known(r.Language, c) == true) ?? w[0].Trim()).ToList();
            all.Add((new TitleGuess(Join(top), Score(top, r.Language, Known), r.Language), i, 0));
            if (!fixedUp.SequenceEqual(top)) all.Add((new TitleGuess(Join(fixedUp), Score(fixedUp, r.Language, Known), r.Language), i, 1));
        }
        if (all.Count == 0) return (new List<TitleGuess>(), false);

        // The same words read by several recognizers: one guess, which counts each agreement.
        var grouped = all.GroupBy(a => a.Guess.Text, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => (Best: g.OrderByDescending(a => a.Guess.Score).ThenBy(a => a.Order).ThenBy(a => a.Variant).First(), Agreeing: g.Select(a => a.Order).Distinct().Count()))
            .OrderByDescending(g => g.Best.Guess.Score).ThenByDescending(g => g.Agreeing).ThenBy(g => g.Best.Order).ThenBy(g => g.Best.Variant)
            .ToList();

        var guesses = grouped.Select(g => g.Best.Guess).Take(MaxGuesses).ToList();
        bool allAgree = grouped.Count == 1;
        bool onlyRealWords = guesses[0].Score >= 1 && (guesses.Count == 1 || guesses[1].Score < 1);
        return (guesses, allAgree || onlyRealWords);
    }

    static string Join(IEnumerable<string> words) => Regex.Replace(string.Join(" ", words), @"\s+([,.;:!?)])", "$1").Trim();

    /// <summary>The share of words that are real words; one that isn't letters (a time, a number) counts as real, and one that can't be checked as half.</summary>
    static double Score(IReadOnlyList<string> words, string language, Func<string, string, bool?> known)
    {
        double sum = 0;
        foreach (var word in words)
        {
            var letters = word.Trim('.', ',', ';', ':', '!', '?', '(', ')', '"', '\'', '«', '»');
            if (!letters.Any(char.IsLetter)) { sum += 1; continue; }
            sum += known(language, letters) switch { true => 1, false => 0, null => 0.5 };
        }
        return words.Count == 0 ? 0 : sum / words.Count;
    }

    /// <summary>
    /// The language of a handwriting recognizer, from its name as Windows gives it (in English or in its own language, such as
    /// "Microsoft English (US) Handwriting Recognizer" or "Microsoft Reconnaissance d'écriture manuscrite – Français"). Null when unknown.
    /// </summary>
    public static string? LanguageOf(string recognizerName)
    {
        var n = recognizerName ?? "";
        bool Has(params string[] words) => words.Any(w => n.Contains(w, StringComparison.OrdinalIgnoreCase));
        if (Has("English", "Anglais"))
            return Has("United Kingdom", "(UK)", "U.K.", "Royaume-Uni") ? "en-GB" : Has("Australia") ? "en-AU" : Has("Canada") ? "en-CA" : Has("India") ? "en-IN" : "en-US";
        if (Has("Français", "Francais", "French")) return Has("Canada") ? "fr-CA" : "fr-FR";
        if (Has("Deutsch", "German", "Allemand")) return "de-DE";
        if (Has("Español", "Espanol", "Spanish", "Espagnol")) return "es-ES";
        if (Has("Italiano", "Italian", "Italien")) return "it-IT";
        if (Has("Português", "Portugues", "Portuguese", "Portugais")) return Has("Brasil", "Brazil") ? "pt-BR" : "pt-PT";
        if (Has("Nederlands", "Dutch", "Néerlandais")) return "nl-NL";
        return null;
    }
}
