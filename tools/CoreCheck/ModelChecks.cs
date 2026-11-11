using System;
using System.Linq;
using System.Threading.Tasks;
using Longhand.Services;

/// <summary>Checks that each conversation is answered by its own model, and that new ones start with the last one chosen.</summary>
static class ModelChecks
{
    public static async Task RunAsync(Action<string, bool> check)
    {
        // ----- the list keeps each conversation's model -----
        var index = new ConversationIndex();
        var made = index.Create(DateTimeOffset.UtcNow, "claude-opus-5-5");
        index.SetModel(made.Id, "claude-sonnet-5-5");
        var reread = ConversationIndex.Parse(index.ToJson());
        check("a conversation's model survives saving the list", reread.Find(made.Id)?.Model == "claude-sonnet-5-5");
        index.SetModel(made.Id, "claude-retired-1");
        check("a model that is no longer offered falls back to the default", index.Find(made.Id)?.Model == ModelChoice.Default);
        var old = ConversationIndex.Parse("""{"Items":[{"Id":"a","AutoTitle":"t","CustomTitle":"","Updated":"2026-01-01T00:00:00+00:00","Blank":false}],"CurrentId":"a"}""");
        check("a list saved before conversations had models still loads, with none set", old.Find("a") is { Model: null });

        // ----- on the page -----
        await using var api = new FakeAnthropic { AskForTool = false };
        var view = new FakeView();
        var data = new FakeData(view, api.BaseUrl) { DefaultModel = "claude-haiku-5-5" };
        var session = new PageSession(view, data);
        await session.StartAsync();
        async Task Write()
        {
            double y = session.Notes.Where(n => !n.IsHint).Select(n => n.Bottom).DefaultIfEmpty(160).Max();
            session.StrokesCollected(new[] { view.Draw(y + 4, y + 30) });
            await session.AskAsync();
        }
        string SentModel() => System.Text.Json.JsonDocument.Parse(api.Requests.Last(r => r.Path.EndsWith("/messages")).Body).RootElement.GetProperty("model").GetString()!;

        check("a new conversation starts with the default model", session.CurrentModel == "claude-haiku-5-5");
        await session.SetModelAsync("claude-opus-5-5");
        var first = session.CurrentId;
        check("choosing a model sets this conversation's, and the default for new ones", session.CurrentModel == "claude-opus-5-5" && session.Index.Find(first)?.Model == "claude-opus-5-5" && data.DefaultModel == "claude-opus-5-5");
        await Write();
        check("the conversation is answered by its model", data.AskedModels.LastOrDefault() == "claude-opus-5-5" && SentModel() == "claude-opus-5-5");

        await session.NewConversationAsync();
        var second = session.CurrentId;
        check("the next conversation starts with the model chosen last", second != first && session.CurrentModel == "claude-opus-5-5");
        await session.SetModelAsync("claude-sonnet-5-5");
        await Write();
        check("and can have its own", SentModel() == "claude-sonnet-5-5" && session.Index.Find(first)?.Model == "claude-opus-5-5");

        await session.OpenConversationAsync(first);
        await Write();
        check("going back to a conversation goes back to its model", session.CurrentModel == "claude-opus-5-5" && SentModel() == "claude-opus-5-5");

        // A conversation from before models were per conversation keeps the one it is opened with.
        var i = session.Index.Items.FindIndex(c => c.Id == second);
        session.Index.Items[i] = session.Index.Items[i] with { Model = null };
        data.DefaultModel = "claude-haiku-5-5";
        await session.OpenConversationAsync(second);
        data.DefaultModel = "claude-opus-5-5";
        check("an older conversation is pinned to the model it is opened with", session.Index.Find(second)?.Model == "claude-haiku-5-5" && session.CurrentModel == "claude-haiku-5-5");
    }
}
