using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Longhand.Services;

namespace Longhand.Web;

/// <summary>
/// Everything the server keeps, in one folder: conversations (an index and a folder each), the user's symbols, the chosen model
/// and, when it didn't come from the environment, the API key. Shared by every connection.
/// </summary>
public sealed class WebStore
{
    const string SymbolsFile = "symbols.json", SymbolsFolder = "symbols", SettingsFile = "settings.json", SignInFile = "chatgpt-signin.json";
    readonly WebOptions _options;
    readonly object _lock = new();

    public WebStore(WebOptions options)
    {
        _options = options;
        Directory.CreateDirectory(Dir);
        Settings = new FileSettings(PathOf(SettingsFile));
        Secrets = new FileSecrets(PathOf("secrets.json"));
        Connectors = new ConnectorBook(new FileSettings(PathOf("apps.json")), Secrets);
        Nextcloud = new NextcloudBook(Settings, Secrets);
    }

    public FileSettings Settings { get; }
    public ISecrets Secrets { get; }
    /// <summary>The MCP servers the user added (their tokens are in <see cref="Secrets"/>, never in apps.json).</summary>
    public ConnectorBook Connectors { get; }
    public NextcloudBook Nextcloud { get; }
    public WebOptions Options => _options;

    public string Dir => _options.DataDir;
    public string? CodexBaseUrl => _options.CodexBaseUrl;
    public string? CodexRefreshUrl => _options.CodexRefreshUrl;
    string PathOf(params string[] parts) => Path.Combine(new[] { Dir }.Concat(parts).ToArray());

    // ----- files, written so a crash never leaves half a file -----

    public static async Task WriteAtomicAsync(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, text);
        File.Move(temp, path, overwrite: true);
    }

    static async Task<string?> ReadOrNullAsync(string path)
    {
        try { return File.Exists(path) ? await File.ReadAllTextAsync(path) : null; }
        catch (IOException) { return null; }
    }

    // ----- the ChatGPT sign-in -----

    /// <summary>"saved" when one was pasted into the page (it is the one used), "environment" when it comes from LONGHAND_CODEX_SIGNIN (or the options), else null.</summary>
    public string? SignInSource => SavedSignIn() is not null ? "saved" : FromEnvironment() is not null ? "environment" : null;

    CodexSignIn? FromEnvironment() => CodexSignIn.Parse(_options.SignIn ?? Environment.GetEnvironmentVariable("LONGHAND_CODEX_SIGNIN"));

    CodexSignIn? SavedSignIn()
    {
        try { return File.Exists(PathOf(SignInFile)) ? CodexSignIn.Parse(File.ReadAllText(PathOf(SignInFile))) : null; }
        catch (IOException) { return null; }
    }

    /// <summary>The sign-in in use: a saved one (which may have been renewed since) wins over the environment's.</summary>
    public CodexSignIn? SignIn => SavedSignIn() ?? FromEnvironment();

    /// <summary>Keeps the sign-in (also a renewed one) in a file only this user can read.</summary>
    public void SaveSignIn(CodexSignIn signIn) => PrivateFile.Write(PathOf(SignInFile), signIn.ToJson(), priv: true);

    public void ClearSignIn()
    {
        try { File.Delete(PathOf(SignInFile)); } catch (IOException) { }
    }

    // ----- the model -----

    public string Model
    {
        get => ModelChoice.Resolve(Settings.Get("model"));
        set => Settings.Set("model", ModelChoice.Resolve(value));
    }

    // ----- conversations -----

    public string FolderOf(string id)
    {
        // Ids are made by the index (hex), but a message from a browser must never reach outside the data folder.
        if (id.Length == 0 || id.Any(c => !char.IsAsciiLetterOrDigit(c))) throw new ArgumentException("bad conversation id");
        return PathOf("conversations", id);
    }

    public async Task<ConversationIndex> LoadIndexAsync() => ConversationIndex.Parse(await ReadOrNullAsync(PathOf("conversations.json")));
    public Task SaveIndexAsync(ConversationIndex index) => WriteAtomicAsync(PathOf("conversations.json"), index.ToJson());

    public async Task<(List<WebStroke>? Ink, List<SavedNote>? Notes)> LoadPageAsync(string id)
    {
        var folder = FolderOf(id);
        List<WebStroke>? ink = null;
        List<SavedNote>? notes = null;
        try
        {
            if (await ReadOrNullAsync(Path.Combine(folder, "ink.json")) is { } inkJson) ink = JsonSerializer.Deserialize<List<WebStroke>>(inkJson);
            if (await ReadOrNullAsync(Path.Combine(folder, "notes.json")) is { } notesJson) notes = JsonSerializer.Deserialize<List<SavedNote>>(notesJson);
        }
        catch (JsonException)
        {
            // A damaged save starts a fresh page rather than blocking the app.
        }
        return (ink, notes);
    }

    public async Task SavePageAsync(string id, IReadOnlyList<WebStroke> ink, IReadOnlyList<SavedNote> notes)
    {
        var folder = FolderOf(id);
        await WriteAtomicAsync(Path.Combine(folder, "ink.json"), JsonSerializer.Serialize(ink));
        await WriteAtomicAsync(Path.Combine(folder, "notes.json"), JsonSerializer.Serialize(notes));
    }

    public Task DeletePageAsync(string id)
    {
        try { Directory.Delete(FolderOf(id), recursive: true); }
        catch (Exception ex) when (ex is IOException or ArgumentException) { /* left behind; it is no longer in the list */ }
        return Task.CompletedTask;
    }

    // ----- symbols -----

    public List<UserSymbol> Symbols()
    {
        try { return SymbolList.Parse(File.Exists(PathOf(SymbolsFile)) ? File.ReadAllText(PathOf(SymbolsFile)) : null); }
        catch (IOException) { return new(); }
    }

    /// <summary>Adds or changes a symbol. <paramref name="drawing"/> is a PNG to save, <paramref name="removeDrawing"/> drops the saved one.</summary>
    /// <returns>Why it can't be saved, or null.</returns>
    public string? SaveSymbol(string? id, string name, string meaning, byte[]? drawing, bool removeDrawing)
    {
        lock (_lock)
        {
            var symbols = Symbols();
            var editing = id is not null && symbols.Any(s => s.Id == id) ? id : null;
            var problem = SymbolList.Problem(symbols, editing, name, meaning);
            if (problem is not null) return problem;
            var final = editing ?? SymbolList.NewId();
            bool has = symbols.FirstOrDefault(s => s.Id == final)?.HasDrawing ?? false;
            if (drawing is not null)
            {
                Directory.CreateDirectory(PathOf(SymbolsFolder));
                File.WriteAllBytes(PathOf(SymbolsFolder, final + ".png"), drawing);
                has = true;
            }
            else if (removeDrawing && has)
            {
                DeleteDrawingFile(final);
                has = false;
            }
            File.WriteAllText(PathOf(SymbolsFile), SymbolList.ToJson(SymbolList.Upsert(symbols, new UserSymbol(final, name, meaning, has))));
            return null;
        }
    }

    public void DeleteSymbol(string id)
    {
        lock (_lock)
        {
            File.WriteAllText(PathOf(SymbolsFile), SymbolList.ToJson(SymbolList.Remove(Symbols(), id)));
            DeleteDrawingFile(id);
        }
    }

    void DeleteDrawingFile(string id)
    {
        try { File.Delete(PathOf(SymbolsFolder, id + ".png")); } catch (IOException) { }
    }

    public byte[]? Drawing(string id)
    {
        if (id.Any(c => !char.IsAsciiLetterOrDigit(c))) return null;
        try { return File.Exists(PathOf(SymbolsFolder, id + ".png")) ? File.ReadAllBytes(PathOf(SymbolsFolder, id + ".png")) : null; }
        catch (IOException) { return null; }
    }

    /// <summary>What Claude is told about the symbols; a drawing that can't be read is skipped, so the symbol still counts, by name.</summary>
    public List<LegendEntry> Legend() =>
        Symbols().Select(s => new LegendEntry(s.Name, s.Meaning, s.HasDrawing ? Drawing(s.Id) : null)).ToList();
}
