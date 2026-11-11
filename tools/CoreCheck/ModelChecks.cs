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
        var made = index.Create(DateTimeOffset.UtcNow, "gpt-6.1-terra");
        index.SetModel(made.Id, "gpt-6.1-sol");
        var reread = ConversationIndex.Parse(index.ToJson());
        check("a conversation's model survives saving the list", reread.Find(made.Id)?.Model == "gpt-6.1-sol");
        index.SetModel(made.Id, "claude-opus-5-5");
        check("a model that is no longer offered (a Claude one) falls back to the default", index.Find(made.Id)?.Model == ModelChoice.Default);
        var old = ConversationIndex.Parse("""{"Items":[{"Id":"a","AutoTitle":"t","CustomTitle":"","Updated":"2026-01-01T00:00:00+00:00","Blank":false}],"CurrentId":"a"}""");
        check("a list saved before conversations had models still loads, with none set", old.Find("a") is { Model: null });

        // ----- on the page -----
        await using var api = new FakeCodex { AskForTool = false };
        var view = new FakeView();
        var data = new FakeData(view, api) { DefaultModel = "gpt-6.1-sol" };
        var session = new PageSession(view, data);
        await session.StartAsync();
        async Task Write()
        {
            double y = session.Notes.Where(n => !n.IsHint).Select(n => n.Bottom).DefaultIfEmpty(160).Max();
            session.StrokesCollected(new[] { view.Draw(y + 4, y + 30) });
            await session.AskAsync();
        }
        string SentModel() => System.Text.Json.JsonDocument.Parse(api.Requests.Last(r => r.Path.EndsWith("/responses")).Body).RootElement.GetProperty("model").GetString()!;

        check("a new conversation starts with the default model", session.CurrentModel == "gpt-6.1-sol");
        await session.SetModelAsync("gpt-6.1-terra");
        var first = session.CurrentId;
        check("choosing a model sets this conversation's, and the default for new ones", session.CurrentModel == "gpt-6.1-terra" && session.Index.Find(first)?.Model == "gpt-6.1-terra" && data.DefaultModel == "gpt-6.1-terra");
        await Write();
        check("the conversation is answered by its model", data.AskedModels.LastOrDefault() == "gpt-6.1-terra" && SentModel() == "gpt-6.1-terra");

        await session.NewConversationAsync();
        var second = session.CurrentId;
        check("the next conversation starts with the model chosen last", second != first && session.CurrentModel == "gpt-6.1-terra");
        await session.SetModelAsync("gpt-6.1-sol");
        await Write();
        check("and can have its own", SentModel() == "gpt-6.1-sol" && session.Index.Find(first)?.Model == "gpt-6.1-terra");

        await session.OpenConversationAsync(first);
        await Write();
        check("going back to a conversation goes back to its model", session.CurrentModel == "gpt-6.1-terra" && SentModel() == "gpt-6.1-terra");

        // A conversation from before models were per conversation keeps the one it is opened with.
        var i = session.Index.Items.FindIndex(c => c.Id == second);
        session.Index.Items[i] = session.Index.Items[i] with { Model = null };
        data.DefaultModel = "gpt-6.1-sol";
        await session.OpenConversationAsync(second);
        data.DefaultModel = "gpt-6.1-terra";
        check("an older conversation is pinned to the model it is opened with", session.Index.Find(second)?.Model == "gpt-6.1-sol" && session.CurrentModel == "gpt-6.1-sol");
    }
}
