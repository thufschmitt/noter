using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Longhand.Services;

/// <summary>Keeps the user's symbols in the app's local folder: one list file, and one PNG per symbol that has an example drawing.</summary>
public static class SymbolStore
{
    const string ListFile = "symbols.json";
    const string Folder = "symbols";

    public static async Task<List<Symbol>> LoadAsync()
    {
        try
        {
            if (await ApplicationData.Current.LocalFolder.TryGetItemAsync(ListFile) is StorageFile file)
                return SymbolList.Parse(await FileIO.ReadTextAsync(file));
        }
        catch (Exception)
        {
            // An unreadable list starts empty; it is the user's to rebuild, and nothing else depends on it.
        }
        return new List<Symbol>();
    }

    public static async Task SaveAsync(IReadOnlyList<Symbol> symbols)
    {
        var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(ListFile, CreationCollisionOption.ReplaceExisting);
        await FileIO.WriteTextAsync(file, SymbolList.ToJson(symbols));
    }

    static async Task<StorageFolder> FolderAsync() =>
        await ApplicationData.Current.LocalFolder.CreateFolderAsync(Folder, CreationCollisionOption.OpenIfExists);

    public static async Task WriteDrawingAsync(string id, byte[] png)
    {
        var file = await (await FolderAsync()).CreateFileAsync(id + ".png", CreationCollisionOption.ReplaceExisting);
        await FileIO.WriteBytesAsync(file, png);
    }

    public static async Task<byte[]?> ReadDrawingAsync(string id)
    {
        try
        {
            if (await (await FolderAsync()).TryGetItemAsync(id + ".png") is not StorageFile file) return null;
            var buffer = await FileIO.ReadBufferAsync(file);
            var bytes = new byte[buffer.Length];
            using var reader = DataReader.FromBuffer(buffer);
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static async Task DeleteDrawingAsync(string id)
    {
        try
        {
            if (await (await FolderAsync()).TryGetItemAsync(id + ".png") is { } file) await file.DeleteAsync();
        }
        catch (Exception)
        {
            // A file that can't be removed is left behind; the symbol no longer points at it.
        }
    }

    /// <summary>What Claude is told. A drawing that can't be read is skipped, so the symbol still counts, by name.</summary>
    public static async Task<List<LegendEntry>> LegendAsync()
    {
        var legend = new List<LegendEntry>();
        foreach (var symbol in await LoadAsync())
            legend.Add(new LegendEntry(symbol.Name, symbol.Meaning, symbol.HasDrawing ? await ReadDrawingAsync(symbol.Id) : null));
        return legend;
    }
}
