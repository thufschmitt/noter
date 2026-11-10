using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;

namespace Longhand.Services;

/// <summary>
/// Where the conversations live: an index file in the app's local folder and one folder per conversation holding its ink and
/// notes (see PageStore).
/// </summary>
public static class ConversationStore
{
    const string IndexFile = "conversations.json";
    const string Root = "conversations";
    // The single page that earlier versions saved straight into the local folder.
    static readonly string[] LegacyFiles = { "page.isf", "notes.json" };

    public static async Task<ConversationIndex> LoadAsync()
    {
        var local = ApplicationData.Current.LocalFolder;
        try
        {
            if (await local.TryGetItemAsync(IndexFile) is StorageFile file)
                return ConversationIndex.Parse(await FileIO.ReadTextAsync(file));
        }
        catch (Exception)
        {
            // An unreadable index starts a fresh list; the conversation folders are left alone.
        }

        var index = new ConversationIndex();
        await MigrateLegacyPageAsync(local, index);
        return index;
    }

    /// <summary>The page that was there before conversations existed becomes the first one, so nothing written is lost.</summary>
    static async Task MigrateLegacyPageAsync(StorageFolder local, ConversationIndex index)
    {
        var legacy = (await Task.WhenAll(LegacyFiles.Select(async name => await local.TryGetItemAsync(name) as StorageFile)))
            .OfType<StorageFile>().ToList();
        if (legacy.Count == 0) return;

        var conversation = index.Create(DateTimeOffset.UtcNow) with { Blank = false };
        index.Items[^1] = conversation;
        var folder = await FolderAsync(conversation.Id);
        foreach (var file in legacy) await file.MoveAsync(folder);
        await SaveAsync(index);
    }

    public static async Task SaveAsync(ConversationIndex index)
    {
        var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(IndexFile, CreationCollisionOption.ReplaceExisting);
        await FileIO.WriteTextAsync(file, index.ToJson());
    }

    public static async Task<StorageFolder> FolderAsync(string id)
    {
        var root = await ApplicationData.Current.LocalFolder.CreateFolderAsync(Root, CreationCollisionOption.OpenIfExists);
        return await root.CreateFolderAsync(id, CreationCollisionOption.OpenIfExists);
    }

    public static async Task DeleteAsync(string id)
    {
        try
        {
            var root = await ApplicationData.Current.LocalFolder.CreateFolderAsync(Root, CreationCollisionOption.OpenIfExists);
            if (await root.TryGetItemAsync(id) is { } folder) await folder.DeleteAsync();
        }
        catch (Exception)
        {
            // A folder that can't be removed is just left behind; it is no longer in the list.
        }
    }
}
