using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI.Input.Inking;

namespace Longhand.Services;

/// <summary>A note Claude wrote on the page, as saved to disk.</summary>
public sealed record SavedNote(double Y, string Text, string Heard, bool IsHint);

/// <summary>Saves the page (ink as ISF, Claude's notes as JSON) to the app's local folder.</summary>
public static class PageStore
{
    const string InkFile = "page.isf";
    const string NotesFile = "notes.json";

    public static async Task SaveAsync(InkStrokeContainer strokes, IReadOnlyList<SavedNote> notes)
    {
        var folder = ApplicationData.Current.LocalFolder;
        var ink = await folder.CreateFileAsync(InkFile, CreationCollisionOption.ReplaceExisting);
        using (var stream = await ink.OpenAsync(FileAccessMode.ReadWrite))
        {
            if (strokes.GetStrokes().Count > 0) await strokes.SaveAsync(stream);
        }
        var json = await folder.CreateFileAsync(NotesFile, CreationCollisionOption.ReplaceExisting);
        await FileIO.WriteTextAsync(json, JsonSerializer.Serialize(notes));
    }

    /// <returns>The saved notes, or null when there is no saved page yet.</returns>
    public static async Task<List<SavedNote>?> LoadAsync(InkStrokeContainer strokes)
    {
        var folder = ApplicationData.Current.LocalFolder;
        try
        {
            if (await folder.TryGetItemAsync(InkFile) is StorageFile ink)
            {
                using var stream = await ink.OpenSequentialReadAsync();
                var props = await ink.GetBasicPropertiesAsync();
                if (props.Size > 0) await strokes.LoadAsync(stream);
            }
            if (await folder.TryGetItemAsync(NotesFile) is StorageFile json)
                return JsonSerializer.Deserialize<List<SavedNote>>(await FileIO.ReadTextAsync(json));
        }
        catch (Exception)
        {
            // A damaged save starts a fresh page rather than blocking the app.
        }
        return null;
    }
}
