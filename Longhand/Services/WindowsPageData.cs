using System;
using System.Collections.Generic;
using System.Linq;
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
    /// The model gets the calendar while a Nextcloud account is connected, and the tools of the connected apps (Longhand calls
    /// them itself); apps whose sign-in can't be renewed or that can't be reached are left out and named. A renewed ChatGPT
    /// sign-in is saved at once, since the old refresh token stops working when it is used.
    /// </summary>
    public async Task<PreparedAsk> PrepareAskAsync(CodexSignIn signIn, string model, CancellationToken ct)
    {
        var tools = new List<IAgentTool>();
        if (NextcloudStore.Load() is { } account) tools.AddRange(CalendarTools.For(_nextcloud, account));
        if (GoogleStore.Book.Connected) tools.AddRange(GoogleCalendarTools.For(new GoogleCalendar(ct2 => GoogleStore.Book.AccessTokenAsync(_oauth, ct2)), GoogleStore.Book.CanWrite));
        var (connectors, expired) = await ConnectorStore.ActiveAsync(_oauth, ct);
        var (appTools, failures) = await McpTool.ForWithReasonsAsync(connectors, ct);
        var unreachable = failures.Select(f => f.Reason.Length > 0 ? $"{f.Name} ({f.Reason})" : f.Name);
        tools.AddRange(appTools);
        var legend = await SymbolStore.LegendAsync();
        var service = new CodexService(signIn, renewed => { SignInStore.Save(renewed); return Task.CompletedTask; }, model, tools);
        return new PreparedAsk(service, legend, expired.Concat(unreachable).ToList());
    }
}
