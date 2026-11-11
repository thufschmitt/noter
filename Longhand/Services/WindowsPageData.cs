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

    public CodexSignIn? SignIn => SignInStore.Load();

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

    /// <summary>
    /// The calendar is offered only while a Nextcloud account is connected. A renewed sign-in is saved at once, since the old
    /// refresh token stops working when it is used.
    /// </summary>
    public async Task<PreparedAsk> PrepareAskAsync(CodexSignIn signIn, string model, CancellationToken ct)
    {
        IReadOnlyList<IAgentTool> tools = NextcloudStore.Load() is { } account ? CalendarTools.For(_nextcloud, account) : Array.Empty<IAgentTool>();
        var legend = await SymbolStore.LegendAsync();
        var service = new CodexService(signIn, renewed => { SignInStore.Save(renewed); return Task.CompletedTask; }, model, tools);
        return new PreparedAsk(service, legend);
    }
}
