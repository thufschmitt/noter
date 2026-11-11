using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Input.Inking;

namespace Longhand.Services;

/// <summary>Where the page keeps things on Windows: the app's local folder, Credential Manager and local settings.</summary>
public sealed class WindowsPageData : IPageData
{
    readonly Func<InkStrokeContainer> _ink;
    readonly McpOAuth _oauth;
    readonly Nextcloud _nextcloud;

    /// <param name="ink">The page's ink, which is loaded into and saved from the canvas the user writes on.</param>
    public WindowsPageData(Func<InkStrokeContainer> ink, McpOAuth oauth, Nextcloud nextcloud)
    {
        _ink = ink;
        _oauth = oauth;
        _nextcloud = nextcloud;
    }

    public string? ApiKey => ApiKeyStore.Load();

    public string DefaultModel
    {
        get => ModelStore.Load();
        set => ModelStore.Save(value);
    }

    public Task<ConversationIndex> LoadIndexAsync() => ConversationStore.LoadAsync();
    public Task SaveIndexAsync(ConversationIndex index) => ConversationStore.SaveAsync(index);

    public async Task<List<SavedNote>?> LoadPageAsync(string id) =>
        await PageStore.LoadAsync(await ConversationStore.FolderAsync(id), _ink());

    public async Task SavePageAsync(string id, IReadOnlyList<SavedNote> notes) =>
        await PageStore.SaveAsync(await ConversationStore.FolderAsync(id), _ink(), notes);

    public Task DeletePageAsync(string id) => ConversationStore.DeleteAsync(id);

    /// <summary>Connected apps whose sign-in can't be renewed are left out and named, since Claude won't have them. The calendar is offered only while a Nextcloud account is connected.</summary>
    public async Task<PreparedAsk> PrepareAskAsync(string apiKey, string model, CancellationToken ct)
    {
        var (connectors, expired) = await ConnectorStore.ActiveAsync(_oauth, ct);
        IReadOnlyList<IAgentTool> tools = NextcloudStore.Load() is { } account ? new IAgentTool[] { new CalendarTool(_nextcloud, account) } : Array.Empty<IAgentTool>();
        var legend = await SymbolStore.LegendAsync();
        return new PreparedAsk(new ClaudeService(apiKey, connectors, model, agentTools: tools), legend, expired);
    }
}
