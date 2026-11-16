using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Longhand.Services;

/// <summary>
/// Windows' own spell checking (the same dictionaries as the keyboard's), asked whether a word is a real word of a language. It is a
/// COM service (spellcheck.h), called here through its interfaces' slots, with no wrapper. Anything that fails (no dictionary for
/// the language, the service missing) answers null, "can't tell", and is never asked again for that language.
/// </summary>
public static class SpellCheck
{
    static readonly Guid FactoryClass = new("7AB36653-1796-484B-BDFA-E74F1DB7C1DC");
    static readonly Guid FactoryInterface = new("8E018A9D-2415-4677-BF08-794EA61F94BB");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int IsSupportedFn(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string languageTag, out int supported);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int CreateFn(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string languageTag, out IntPtr checker);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int CheckFn(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string text, out IntPtr errors);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int NextFn(IntPtr self, out IntPtr error);

    [DllImport("ole32.dll")]
    static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);

    const uint InProcServer = 1;
    static IntPtr _factory;
    static bool _factoryFailed;
    /// <summary>A checker for each language asked about, or zero when there is none; kept for the app's life.</summary>
    static readonly Dictionary<string, IntPtr> Checkers = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<(string, string), bool?> Cache = new();

    static T Slot<T>(IntPtr obj, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size));

    /// <summary>Whether <paramref name="word"/> is a real word of <paramref name="language"/> (a tag like fr-FR); null when that can't be told.</summary>
    public static bool? IsWord(string language, string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return null;
        var key = (language, word);
        if (Cache.TryGetValue(key, out var cached)) return cached;
        bool? answer = null;
        try
        {
            var checker = CheckerFor(language);
            if (checker != IntPtr.Zero)
            {
                // ISpellChecker::Check (slot 4) lists the misspellings; ISpellingError enumeration's Next (slot 3) gives S_FALSE when there is none.
                if (Slot<CheckFn>(checker, 4)(checker, word, out var errors) == 0 && errors != IntPtr.Zero)
                {
                    try
                    {
                        int hr = Slot<NextFn>(errors, 3)(errors, out var error);
                        if (error != IntPtr.Zero) Marshal.Release(error);
                        answer = hr switch { 0 => false, 1 => true, _ => null };
                    }
                    finally { Marshal.Release(errors); }
                }
            }
        }
        catch (Exception) { answer = null; }
        if (Cache.Count > 2000) Cache.Clear();
        Cache[key] = answer;
        return answer;
    }

    static IntPtr CheckerFor(string language)
    {
        if (Checkers.TryGetValue(language, out var known)) return known;
        var checker = IntPtr.Zero;
        var factory = Factory();
        if (factory != IntPtr.Zero)
        {
            // The tag itself (fr-FR), else the language alone (fr).
            foreach (var tag in new[] { language, language.Split('-')[0] })
            {
                // ISpellCheckerFactory::IsSupported is slot 4, CreateSpellChecker slot 5.
                if (Slot<IsSupportedFn>(factory, 4)(factory, tag, out int supported) != 0 || supported == 0) continue;
                if (Slot<CreateFn>(factory, 5)(factory, tag, out checker) == 0 && checker != IntPtr.Zero) break;
                checker = IntPtr.Zero;
            }
        }
        Checkers[language] = checker;
        return checker;
    }

    static IntPtr Factory()
    {
        if (_factory != IntPtr.Zero || _factoryFailed) return _factory;
        var clsid = FactoryClass;
        var iid = FactoryInterface;
        if (CoCreateInstance(ref clsid, IntPtr.Zero, InProcServer, ref iid, out _factory) != 0) { _factory = IntPtr.Zero; _factoryFailed = true; }
        return _factory;
    }
}
