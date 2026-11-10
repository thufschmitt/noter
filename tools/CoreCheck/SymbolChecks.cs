using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Longhand.Services;

/// <summary>Checks for the user's symbols: the list rules, and what Claude is sent about them.</summary>
static class SymbolChecks
{
    public static async Task RunAsync(Action<string, bool> check)
    {
        check("there are no symbols until the user makes some", SymbolList.Parse(null).Count == 0 && SymbolList.Parse("").Count == 0 && SymbolList.Parse("[]").Count == 0);

        var envelope = new Symbol("a", "envelope", "Add what I wrote to my Todoist inbox.", HasDrawing: true);
        var star = new Symbol("b", "star", "Remind me about this tomorrow.", HasDrawing: false);
        var list = SymbolList.Upsert(SymbolList.Upsert(new List<Symbol>(), envelope), star);
        check("symbols are added in order", list.Select(s => s.Name).SequenceEqual(new[] { "envelope", "star" }));

        var changed = SymbolList.Upsert(list, envelope with { Meaning = "  Put it in   Todoist.\n" });
        check("changing a symbol replaces it and tidies the text", changed.Count == 2 && changed[0].Meaning == "Put it in Todoist." && changed[0].HasDrawing);
        check("removing a symbol leaves the others", SymbolList.Remove(list, "a").Select(s => s.Id).SequenceEqual(new[] { "b" }));

        var restored = SymbolList.Parse(SymbolList.ToJson(list));
        check("the list survives saving and loading", restored.Count == 2 && restored[0] == envelope && restored[1] == star);
        check("a damaged file gives an empty list", SymbolList.Parse("{ nope").Count == 0);
        var odd = SymbolList.Parse("""[{"Id":"x","Name":"  ","Meaning":"m","HasDrawing":false},{"Id":"y","Name":"ok","Meaning":"fine","HasDrawing":false},{"Id":"y","Name":"dup","Meaning":"fine","HasDrawing":false},{"Id":"","Name":"noid","Meaning":"m","HasDrawing":false}]""");
        check("unusable entries are dropped when loading", odd.Count == 1 && odd[0].Name == "ok");

        check("a name and a meaning are needed", SymbolList.Problem(list, null, "  ", "m") is not null && SymbolList.Problem(list, null, "heart", "") is not null && SymbolList.Problem(list, null, "heart", "Mark as important.") is null);
        check("names can't repeat, but a symbol can keep its own", SymbolList.Problem(list, null, "ENVELOPE", "x") is not null && SymbolList.Problem(list, "a", "Envelope", "x") is null);
        check("long names and meanings are refused", SymbolList.Problem(list, null, new string('n', SymbolList.MaxName + 1), "m") is not null && SymbolList.Problem(list, null, "ok", new string('m', SymbolList.MaxMeaning + 1)) is not null);
        var full = Enumerable.Range(0, SymbolList.MaxSymbols).Select(i => new Symbol("id" + i, "s" + i, "m", false)).ToList();
        check("there is a limit on how many", SymbolList.Problem(full, null, "extra", "m") is not null && SymbolList.Problem(full, "id3", "s3", "new meaning") is null);

        // ----- what Claude is sent -----
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
        var drawing = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3 };
        async Task<(string Body, ReplyResult Result)> Ask(IReadOnlyList<LegendEntry>? legend)
        {
            await using var api = new FakeAnthropic { AskForTool = false };
            var service = new ClaudeService("sk-ant-placeholder", model: "claude-haiku-5-5", baseUrl: api.BaseUrl);
            var result = await service.AskAsync(png, Array.Empty<Turn>(), _ => { }, default, legend: legend);
            // The JSON writer escapes quotes and plus signs as \u0022 and \u002B; read them back as the characters they are.
            var body = api.Requests.First(r => r.Path.EndsWith("/messages")).Body.Replace("\\u0022", "\"").Replace("\\u002B", "+");
            return (body, result);
        }

        var (none, _) = await Ask(null);
        check("without symbols the request is as before", !none.Contains("My personal symbols.") && !none.Contains("The next picture"));

        var (withLegend, answer) = await Ask(new[] { new LegendEntry("envelope", "Add what I wrote to my Todoist inbox.", drawing), new LegendEntry("star", "Remind me about this tomorrow.", null) });
        check("the symbols and what they mean are sent", withLegend.Contains("Symbol \"envelope\" means: Add what I wrote to my Todoist inbox.") && withLegend.Contains("Symbol \"star\" means: Remind me about this tomorrow."));
        check("an example drawing is sent only for the symbol that has one", withLegend.Contains(Convert.ToBase64String(drawing)) && withLegend.Contains("recognise it from its name") && withLegend.Split("\"type\":\"image\"").Length - 1 == 2);
        check("the symbols come before the handwriting, which is named as such", withLegend.IndexOf("personal symbols", StringComparison.Ordinal) < withLegend.IndexOf(Convert.ToBase64String(png), StringComparison.Ordinal) && withLegend.Contains("The next picture is my newest handwriting."));
        check("a reply with symbols still comes back", answer.Outcome == ReplyOutcome.Done && answer.Body.Length > 0);
        check("Claude is told how to treat the symbols", withLegend.Contains("personal symbols") && withLegend.Contains("[envelope symbol]") && withLegend.Contains("reasonably sure"));
    }
}
