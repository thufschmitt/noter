using Windows.Storage;

namespace Longhand.Services;

/// <summary>
/// Remembers which model the user picked last, in the app's local settings (it isn't secret): new conversations start with it.
/// Each conversation keeps its own in the conversation list.
/// </summary>
public static class ModelStore
{
    const string Key = "model";

    public static string Load() => ModelChoice.Resolve(ApplicationData.Current.LocalSettings.Values[Key] as string);

    public static void Save(string id) => ApplicationData.Current.LocalSettings.Values[Key] = ModelChoice.Resolve(id);
}
