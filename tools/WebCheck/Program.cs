using System;
using System.Collections.Generic;
using Longhand.Services;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Longhand.Web;

int failures = 0;
void Check(string name, bool ok) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}"); if (!ok) failures++; }

var data = Path.Combine(Path.GetTempPath(), "longhand-web-check-" + Guid.NewGuid().ToString("N"));
await using var api = new FakeCodex { AskForTool = false };
await using var mcp = new FakeOAuthServer();
await using var registry = new FakeRegistry();
await using var cloud = new FakeNextcloud();
await using var googleApi = new FakeGoogle();
var app = WebApp.Build(new WebOptions
{
    DataDir = data, Urls = "http://127.0.0.1:0", CodexBaseUrl = api.BaseUrl, CodexRefreshUrl = api.BaseUrl + "/oauth/token", SignIn = api.SignIn.ToJson(),
    AllowPrivateApps = true, RegistryBaseUrl = registry.BaseUrl, NextcloudPollInterval = TimeSpan.FromMilliseconds(30), GoogleEndpoints = googleApi.Endpoints,
}); 
await app.StartAsync();
var url = app.Urls.First().Replace("[::1]", "127.0.0.1");
using var http = new HttpClient { BaseAddress = new Uri(url) };

try
{
    // ----- the page and the settings API -----
    var home = await http.GetAsync("/");
    var html = await home.Content.ReadAsStringAsync();
    Check("the page is served", home.StatusCode == HttpStatusCode.OK && html.Contains("<canvas") && home.Content.Headers.ContentType!.MediaType == "text/html");
    var script = await http.GetAsync("/app.js");
    Check("its script is served", script.StatusCode == HttpStatusCode.OK && script.Content.Headers.ContentType!.MediaType == "text/javascript");
    Check("other paths are not found", (await http.GetAsync("/nothing")).StatusCode == HttpStatusCode.NotFound && (await http.GetAsync("/..%2fsecret")).StatusCode == HttpStatusCode.NotFound);

    using (var other = new HttpRequestMessage(HttpMethod.Get, "/api/settings")) { other.Headers.Host = "evil.example"; Check("a request addressed to another name is refused", (await http.SendAsync(other)).StatusCode == HttpStatusCode.Forbidden); }
    var settings = JsonDocument.Parse(await http.GetStringAsync("/api/settings")).RootElement;
    Check("settings carry the version", settings.GetProperty("version").GetString() is { Length: > 0 } ver && ver != "unknown" && char.IsDigit(ver[0]));
    Check("settings say there is a sign-in and list the models", settings.GetProperty("hasSignIn").GetBoolean() && settings.GetProperty("signInSource").GetString() == "environment" && settings.GetProperty("models").GetArrayLength() == ModelChoice.All.Count && settings.GetProperty("model").GetString() == ModelChoice.Default);

    var noHeader = await http.PutAsync("/api/settings", new StringContent("""{"model":"claude-sonnet-5-5"}""", Encoding.UTF8, "application/json"));
    Check("a change without the page's header is refused", noHeader.StatusCode == HttpStatusCode.Forbidden);
    using (var crossSite = new HttpRequestMessage(HttpMethod.Put, "/api/settings") { Content = new StringContent("""{"model":"claude-sonnet-5-5"}""", Encoding.UTF8, "application/json") })
    {
        crossSite.Headers.Add("X-Longhand", "1");
        crossSite.Headers.Add("Origin", "http://evil.example");
        Check("a change from another site is refused", (await http.SendAsync(crossSite)).StatusCode == HttpStatusCode.Forbidden);
    }
    HttpRequestMessage Put(string path, string json) { var r = new HttpRequestMessage(HttpMethod.Put, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") }; r.Headers.Add("X-Longhand", "1"); return r; }
    Check("the model can be changed", (await http.SendAsync(Put("/api/settings", """{"model":"gpt-6.1-terra"}"""))).IsSuccessStatusCode && JsonDocument.Parse(await http.GetStringAsync("/api/settings")).RootElement.GetProperty("model").GetString() == "gpt-6.1-terra");
    await http.SendAsync(Put("/api/settings", """{"model":"gpt-6.1-sol"}"""));
    var notSignIn = await http.SendAsync(Put("/api/settings", """{"signIn":"sk-ant-not-a-sign-in"}"""));
    Check("something that isn't a ChatGPT sign-in is refused, saying what to paste", notSignIn.StatusCode == HttpStatusCode.BadRequest && (await notSignIn.Content.ReadAsStringAsync()).Contains("auth.json"));
    var pasted = await http.SendAsync(Put("/api/settings", JsonSerializer.Serialize(new { signIn = JsonSerializer.Serialize(new Dictionary<string, object?> { ["OPENAI_API_KEY"] = null, ["tokens"] = new { access_token = api.AccessToken, refresh_token = api.RefreshToken, account_id = "acct-1" } }) })));
    var afterPaste = JsonDocument.Parse(await http.GetStringAsync("/api/settings")).RootElement;
    Check("a pasted auth.json is kept, readable only by this user", pasted.IsSuccessStatusCode && afterPaste.GetProperty("signInSource").GetString() == "saved"
        && File.Exists(Path.Combine(data, "chatgpt-signin.json")) && (OperatingSystem.IsWindows() || (File.GetUnixFileMode(Path.Combine(data, "chatgpt-signin.json")) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0));

    var wrongOrigin = new ClientWebSocket();
    wrongOrigin.Options.SetRequestHeader("Origin", "http://evil.example");
    bool refused = false;
    try { await wrongOrigin.ConnectAsync(new Uri(url.Replace("http://", "ws://") + "/ws"), CancellationToken.None); }
    catch (WebSocketException) { refused = true; }
    Check("a page from another site can't open the notebook", refused);

    // ----- opening the notebook -----
    string conversation;
    await using (var b = await FakeBrowser.ConnectAsync(url))
    {
        Check("opening shows the hint, the page and the conversation list", await b.UntilAsync(l => l.Any(m => m.GetProperty("t").GetString() == "conversations")));
        var hint = b.Of("note").First();
        Check("the hint is a note on the first rule", hint.GetProperty("hint").GetBoolean() && hint.GetProperty("y").GetDouble() == 120 && b.Of("place").Any(p => p.GetProperty("id").GetInt32() == hint.GetProperty("id").GetInt32() && p.GetProperty("lines").GetInt32() >= 2));
        Check("the page comes after the clear and the notes", b.Log.Select(m => m.GetProperty("t").GetString()).ToList() is var order && order.IndexOf("clear") < order.IndexOf("note") && order.IndexOf("note") < order.IndexOf("page"));
        conversation = b.Last("conversations")!.Value.GetProperty("current").GetString()!;
        Check("one conversation, open", b.Last("conversations")!.Value.GetProperty("items").GetArrayLength() == 1 && conversation.Length > 0);

        // ----- writing -----
        await b.SendAsync(new { t = "stroke", stroke = new { id = 1, pts = new[] { new[] { 100.0, 160.0 }, new[] { 140.0, 190.0 }, new[] { 180.0, 160.0 } } } });
        Check("writing makes the page askable", await b.UntilAsync(l => l.Any(m => m.GetProperty("t").GetString() == "state" && m.GetProperty("canAsk").GetBoolean())));
        await Task.Delay(2600);
        Check("nothing is asked by itself", b.Snapshots == 0 && !api.Requests.Any(r => r.Path.EndsWith("/responses")));

        // ----- asking -----
        await b.SendAsync(new { t = "ask" });
        Check("Ask gets a reply", await b.UntilAsync(l => l.Any(m => m.GetProperty("t").GetString() == "place" && m.GetProperty("y").GetDouble() == 200), 8000));
        Check("the browser was asked for a picture of the ink", await b.UntilAsync(_ => b.Snapshots == 1) && b.LastSnapshotRequest.GetProperty("ids").EnumerateArray().Select(e => e.GetInt32()).SequenceEqual(new[] { 1 }));
        Check("the reply text was sent as it arrived", await b.UntilAsync(l => l.Any(m => m.GetProperty("t").GetString() == "text" && m.GetProperty("text").GetString() == "You have a standup at 9.")));
        Check("the request carried the picture the browser drew", api.Requests.Any(r => r.Path.EndsWith("/responses") && r.Body.Contains(FakeBrowser.Png[..40])));
        Check("the button goes back to idle", await b.UntilAsync(l => l.Where(m => m.GetProperty("t").GetString() == "state").LastOrDefault() is { ValueKind: JsonValueKind.Object } s && !s.GetProperty("busy").GetBoolean() && !s.GetProperty("canAsk").GetBoolean()));

        // ----- erasing and undo -----
        await b.SendAsync(new { t = "stroke", stroke = new { id = 2, pts = new[] { new[] { 100.0, 500.0 }, new[] { 200.0, 520.0 } } } });
        await b.SendAsync(new { t = "erase", ids = new[] { 2 } });
        await b.SendAsync(new { t = "undo" });
        Check("undoing an erase sends the stroke back", await b.UntilAsync(l => l.Any(m => m.GetProperty("t").GetString() == "add" && m.GetProperty("strokes")[0].GetProperty("id").GetInt32() == 2)));
        await b.SendAsync(new { t = "stroke", stroke = new { id = 1, pts = new[] { new[] { 1.0, 1.0 } } } });
        await b.SendAsync(new { t = "stroke", stroke = new { id = 9, pts = new[] { new[] { double.MaxValue, 1.0 }, new[] { 1.0, 1.0 } } } });
        await b.SendAsync(new { t = "stroke", stroke = new { id = 10, pts = Array.Empty<double[]>() } });
        await b.SendAsync(new { t = "undo" });
        Check("duplicate or empty strokes are ignored", await b.UntilAsync(l => l.Count(m => m.GetProperty("t").GetString() == "delete") >= 1));
    }
    await Task.Delay(300);
    Check("the page was saved to the data folder", File.Exists(Path.Combine(data, "conversations", conversation, "notes.json")) && File.Exists(Path.Combine(data, "conversations", conversation, "ink.json")) && File.Exists(Path.Combine(data, "conversations.json")));

    // ----- coming back -----
    await using (var b = await FakeBrowser.ConnectAsync(url))
    {
        Check("reopening restores the reply and the ink", await b.UntilAsync(l => l.Any(m => m.GetProperty("t").GetString() == "page")) && b.Of("note").Any(n => !n.GetProperty("hint").GetBoolean() && n.GetProperty("text").GetString() == "You have a standup at 9.") && b.Last("page")!.Value.GetProperty("strokes").EnumerateArray().Any(s => s.GetProperty("id").GetInt32() == 1));
        Check("new strokes carry on from the saved ones", b.Last("page")!.Value.GetProperty("maxId").GetInt32() >= 1);

        // ----- symbols reach Claude -----
        HttpRequestMessage Req(HttpMethod m, string path, string? json = null) { var r = new HttpRequestMessage(m, path); r.Headers.Add("X-Longhand", "1"); if (json is not null) r.Content = new StringContent(json, Encoding.UTF8, "application/json"); return r; }
        var empty = JsonDocument.Parse(await http.GetStringAsync("/api/symbols")).RootElement;
        Check("there are no symbols until some are made", empty.GetArrayLength() == 0);
        var bad = await http.SendAsync(Req(HttpMethod.Put, "/api/symbols", """{"name":"envelope","meaning":"x","drawing":"data:image/png;base64,AAAA"}"""));
        Check("a drawing that isn't a PNG is refused", bad.StatusCode == HttpStatusCode.BadRequest);
        var noName = await http.SendAsync(Req(HttpMethod.Put, "/api/symbols", """{"name":" ","meaning":"x"}"""));
        Check("a symbol needs a name, with the reason given", noName.StatusCode == HttpStatusCode.BadRequest && (await noName.Content.ReadAsStringAsync()).Contains("name"));
        var saved = await http.SendAsync(Req(HttpMethod.Put, "/api/symbols", JsonSerializer.Serialize(new { name = "envelope", meaning = "Add what I wrote to my Todoist inbox.", drawing = "data:image/png;base64," + FakeBrowser.Png })));
        var list = JsonDocument.Parse(await http.GetStringAsync("/api/symbols")).RootElement;
        Check("a symbol with a drawing is saved", saved.IsSuccessStatusCode && list.GetArrayLength() == 1 && list[0].GetProperty("hasDrawing").GetBoolean());
        var id = list[0].GetProperty("id").GetString()!;
        var picture = await http.GetAsync($"/api/symbols/{id}.png");
        Check("its drawing can be fetched back", picture.IsSuccessStatusCode && (await picture.Content.ReadAsByteArrayAsync()).Length > 8 && (await http.GetAsync("/api/symbols/..%2fapi-key.png")).StatusCode == HttpStatusCode.NotFound);
        var dup = await http.SendAsync(Req(HttpMethod.Put, "/api/symbols", """{"name":"Envelope","meaning":"again"}"""));
        Check("names can't repeat", dup.StatusCode == HttpStatusCode.BadRequest);

        await b.SendAsync(new { t = "stroke", stroke = new { id = 20, pts = new[] { new[] { 100.0, 700.0 }, new[] { 160.0, 720.0 } } } });
        await b.SendAsync(new { t = "ask" });
        Check("the next ask tells Claude about the symbol", await b.UntilAsync(_ => api.Requests.Count(r => r.Path.EndsWith("/responses")) == 2, 8000) && api.Requests.Last(r => r.Path.EndsWith("/responses")).Body.Replace("\\u0022", "\"").Contains("Symbol \"envelope\" means: Add what I wrote to my Todoist inbox."));
        Check("it was the second of two snapshots", b.Snapshots == 1);

        // The ask above is still being answered when its request reaches the fake API; typing is refused until it is done.
        await b.UntilAsync(l => l.Where(m => m.GetProperty("t").GetString() == "state").LastOrDefault() is { ValueKind: JsonValueKind.Object } st && !st.GetProperty("busy").GetBoolean());
        await b.SendAsync(new { t = "type", text = "typed hello" });
        Check("a typed note shows on the page as the user's, and is answered", await b.UntilAsync(l => l.Any(m => m.GetProperty("t").GetString() == "note" && m.GetProperty("typed").GetBoolean() && m.GetProperty("text").GetString() == "typed hello") && api.Requests.Count(r => r.Path.EndsWith("/responses")) == 3, 8000));
        Check("what Claude was sent is the text", api.Requests.Last(r => r.Path.EndsWith("/responses")).Body.Contains("typed on a keyboard:\\ntyped hello"));
        await b.SendAsync(new { t = "type", text = new string('x', 5000) });
        Check("a note that is too long comes back to the box", await b.UntilAsync(l => l.Any(m => m.GetProperty("t").GetString() == "typed" && m.GetProperty("text").GetString()!.Length == 5000)));
        await http.SendAsync(Req(HttpMethod.Delete, "/api/symbols/" + id));

        Check("a symbol can be deleted", JsonDocument.Parse(await http.GetStringAsync("/api/symbols")).RootElement.GetArrayLength() == 0 && !File.Exists(Path.Combine(data, "symbols", id + ".png")));

        // ----- conversations -----
        await b.SendAsync(new { t = "new" });
        Check("a new conversation lists two", await b.UntilAsync(l => l.Last(m => m.GetProperty("t").GetString() == "conversations").GetProperty("items").GetArrayLength() == 2));
        var second = b.Last("conversations")!.Value.GetProperty("current").GetString()!;
        Check("the new conversation is the open one", second != conversation);
        await b.SendAsync(new { t = "rename", id = conversation, name = "Standups" });
        Check("renaming shows in the list", await b.UntilAsync(l => l.Last(m => m.GetProperty("t").GetString() == "conversations").GetProperty("items").EnumerateArray().Any(i => i.GetProperty("title").GetString() == "Standups")));
        await b.SendAsync(new { t = "archive", id = conversation, archived = true });
        Check("archiving keeps a conversation, marked archived", await b.UntilAsync(l => l.Last(m => m.GetProperty("t").GetString() == "conversations").GetProperty("items").EnumerateArray().Any(i => i.GetProperty("id").GetString() == conversation && i.GetProperty("archived").GetBoolean()))
            && Directory.Exists(Path.Combine(data, "conversations", conversation)) && b.Last("conversations")!.Value.GetProperty("current").GetString() == second);
        await b.SendAsync(new { t = "archive", id = conversation, archived = false });
        Check("an archived conversation can come back", await b.UntilAsync(l => l.Last(m => m.GetProperty("t").GetString() == "conversations").GetProperty("items").EnumerateArray().All(i => !i.GetProperty("archived").GetBoolean())));
        await b.SendAsync(new { t = "delete", id = "../../etc" });
        await b.SendAsync(new { t = "delete", id = conversation });
        Check("deleting a conversation removes its folder", await b.UntilAsync(l => l.Last(m => m.GetProperty("t").GetString() == "conversations").GetProperty("items").GetArrayLength() == 1) && !Directory.Exists(Path.Combine(data, "conversations", conversation)));

        // ----- the tab closing while Claude is answering -----
        api.ReplyDelayMs = 1500;
        await b.SendAsync(new { t = "stroke", stroke = new { id = 30, pts = new[] { new[] { 100.0, 160.0 }, new[] { 160.0, 180.0 } } } });
        await b.SendAsync(new { t = "ask" });
        Check("the page is busy while Claude answers", await b.UntilAsync(l => l.Any(m => m.GetProperty("t").GetString() == "state" && m.GetProperty("busy").GetBoolean())));
    }
    api.ReplyDelayMs = 0;
    await Task.Delay(500);
    await using (var b = await FakeBrowser.ConnectAsync(url))
    {
        Check("closing mid-answer drops the half answer and keeps the writing", await b.UntilAsync(l => l.Any(m => m.GetProperty("t").GetString() == "page")) && !b.Of("note").Any(n => !n.GetProperty("hint").GetBoolean()) && b.Last("page")!.Value.GetProperty("strokes").GetArrayLength() == 1);
        Check("and the writing is waiting to be answered", await b.UntilAsync(l => l.Any(m => m.GetProperty("t").GetString() == "state" && m.GetProperty("canAsk").GetBoolean())));
    }

    // ----- editing a layout -----
    api.FinalText = "HEARD: board\n---\nHere.\n<layout id=\"board\"><row><col id=\"todo\" drop><box id=\"c1\" edit>Call Anna</box></col><col id=\"doing\" drop/></row></layout>";
    await using (var b = await FakeBrowser.ConnectAsync(url))
    {
        await b.UntilAsync(l => l.Any(m => m.GetProperty("t").GetString() == "page"));
        await b.SendAsync(new { t = "ask" });
        Check("a layout reaches the browser as a document, marked editable, with the parts it can act on", await b.UntilAsync(l => l.Any(m => m.GetProperty("t").GetString() == "drawing" && m.GetProperty("edit").GetBoolean() && m.GetProperty("kind").GetString() == "layout"
            && m.GetProperty("doc").GetString()!.Contains("id=\"c1\" data-edit>Call Anna</div>") && m.GetProperty("doc").GetString()!.Contains("data-drop") && m.GetProperty("doc").GetString()!.Contains("default-src 'none'")), 8000));
        Check("and the reply is done", await b.UntilAsync(l => l.Where(m => m.GetProperty("t").GetString() == "state").LastOrDefault() is { ValueKind: JsonValueKind.Object } s && !s.GetProperty("busy").GetBoolean() && !s.GetProperty("canAsk").GetBoolean(), 8000));
        var shown = b.Of("drawing").Last();
        int noteId = shown.GetProperty("note").GetInt32(), drawingId = shown.GetProperty("id").GetInt32();
        int states = b.Of("state").Count(), changed = b.Of("drawingChanged").Count();
        await b.SendAsync(new { t = "edit", note = noteId, drawing = drawingId, changes = new[] { new { id = "c1", op = "move", to = "nowhere", index = 0 } } });
        Check("a drop where nothing accepts it is refused, and the browser is told to show the layout as it is", await b.UntilAsync(l => l.Count(m => m.GetProperty("t").GetString() == "drawingChanged") == changed + 1)
            && b.Of("drawingChanged").Last().GetProperty("doc").GetString()!.Contains("id=\"todo\" data-drop><div class=\"n box edit\" id=\"c1\""));
        await b.SendAsync(new { t = "edit", note = 999, drawing = drawingId, changes = new[] { new { id = "c1", op = "delete" } } });
        await b.SendAsync(new { t = "edit", note = noteId, drawing = drawingId, changes = new[] { new { id = "c1", op = "move", to = "doing", index = 0 } } });
        Check("a move is made in the layout, and the browser is sent it back", await b.UntilAsync(l => l.Count(m => m.GetProperty("t").GetString() == "drawingChanged") == changed + 2)
            && b.Of("drawingChanged").Last().GetProperty("doc").GetString()!.Contains("id=\"doing\" data-drop><div class=\"n box edit\" id=\"c1\""));
        Check("it makes the page askable", await b.UntilAsync(l => l.Where(m => m.GetProperty("t").GetString() == "state").Skip(states).Any(s => s.GetProperty("canAsk").GetBoolean())));
        int asked = api.Requests.Count(r => r.Path.EndsWith("/responses"));
        await Task.Delay(300);
        Check("and nothing is sent until it is asked", api.Requests.Count(r => r.Path.EndsWith("/responses")) == asked);
        api.FinalText = "HEARD: [moved the card]\n---\nDone.";
        await b.SendAsync(new { t = "ask" });
        Check("asking sends what changed, by id", await b.UntilAsync(_ => api.Requests.Count(r => r.Path.EndsWith("/responses")) == asked + 1, 8000)
            && System.Text.RegularExpressions.Regex.Unescape(api.Requests.Last(r => r.Path.EndsWith("/responses")).Body) is var told && told.Contains("c1 \"Call Anna\": moved from #todo (position 1) to #doing (position 1 of 1)"));
    }
    await Task.Delay(300);
    Check("the layout is in the saved page", Directory.GetFiles(Path.Combine(data, "conversations"), "notes.json", SearchOption.AllDirectories).Any(f => System.Text.RegularExpressions.Regex.Unescape(File.ReadAllText(f)).Contains("<col id=\"doing\" drop>")));

    // ----- a timeline -----
    api.FinalText = "HEARD: day\n---\nThere.\n<layout id=\"day\"><row><col id=\"thu\" timeline=\"8-18\" drop><box id=\"e1\" edit start=\"9:00\" end=\"9:30\">Standup</box><box id=\"e2\" start=\"9:15\" end=\"10:00\">Call</box></col><col id=\"fri\" timeline=\"8-18\" drop/></row></layout>";
    await using (var b = await FakeBrowser.ConnectAsync(url))
    {
        await b.UntilAsync(l => l.Any(m => m.GetProperty("t").GetString() == "page"));
        await b.SendAsync(new { t = "type", text = "show my day" });
        Check("a timeline reaches the browser as a grid of hours, with each item at its time and overlapping ones side by side", await b.UntilAsync(l => l.Any(m => m.GetProperty("t").GetString() == "drawing" && m.GetProperty("edit").GetBoolean()
            && m.GetProperty("doc").GetString()!.Contains("<div class=\"tl\" data-from=\"480\" data-to=\"1080\" data-ph=\"64\"") && m.GetProperty("doc").GetString()!.Contains("data-start=\"540\" data-end=\"570\"") && m.GetProperty("doc").GetString()!.Contains("width:calc(50% - 3px)")), 8000));
        await b.UntilAsync(l => l.Where(m => m.GetProperty("t").GetString() == "state").LastOrDefault() is { ValueKind: JsonValueKind.Object } s && !s.GetProperty("busy").GetBoolean(), 8000);
        var shown = b.Of("drawing").Last();
        int noteId = shown.GetProperty("note").GetInt32(), drawingId = shown.GetProperty("id").GetInt32(), changed = b.Of("drawingChanged").Count();
        await b.SendAsync(new { t = "edit", note = noteId, drawing = drawingId, changes = new[] { new { id = "e1", op = "move", to = "fri", index = 0, at = "10:15" } } });
        Check("moving an item to another day with a time makes the move in the layout and sends it back", await b.UntilAsync(l => l.Count(m => m.GetProperty("t").GetString() == "drawingChanged") == changed + 1)
            && b.Of("drawingChanged").Last().GetProperty("doc").GetString() is { } moved && moved.Contains("id=\"fri\"") && moved.IndexOf("id=\"e1\"") > moved.IndexOf("id=\"fri\"") && moved.Contains("data-start=\"615\" data-end=\"645\""));
        await b.SendAsync(new { t = "edit", note = noteId, drawing = drawingId, changes = new[] { new { id = "e1", op = "move", to = "fri" } } });
        await b.SendAsync(new { t = "edit", note = noteId, drawing = drawingId, changes = new[] { new { id = "e2", op = "resize", end = "11:00" } } });
        Check("a move with no time, and a resize of something that isn't editable, are refused and the layout is shown as it is", await b.UntilAsync(l => l.Count(m => m.GetProperty("t").GetString() == "drawingChanged") == changed + 3));
        await b.SendAsync(new { t = "edit", note = noteId, drawing = drawingId, changes = new[] { new { id = "e1", op = "resize", end = "11:00" } } });
        Check("dragging the end sets it", await b.UntilAsync(l => l.Count(m => m.GetProperty("t").GetString() == "drawingChanged") == changed + 4) && b.Of("drawingChanged").Last().GetProperty("doc").GetString()!.Contains("data-start=\"615\" data-end=\"660\""));
        int asked = api.Requests.Count(r => r.Path.EndsWith("/responses"));
        api.FinalText = "HEARD: [rescheduled]\n---\nDone.";
        await b.SendAsync(new { t = "ask" });
        Check("asking tells the model what changed, in times", await b.UntilAsync(_ => api.Requests.Count(r => r.Path.EndsWith("/responses")) == asked + 1, 8000)
            && System.Text.RegularExpressions.Regex.Unescape(api.Requests.Last(r => r.Path.EndsWith("/responses")).Body) is var timeline && timeline.Contains("e1 \"Standup\": moved from #thu 09:00–09:30 to #fri 10:15–11:00"));
    }

    // ----- connected apps -----
    HttpRequestMessage ApiReq(HttpMethod m, string path, object? json = null)
    {
        var r = new HttpRequestMessage(m, path);
        r.Headers.Add("X-Longhand", "1");
        if (json is not null) r.Content = new StringContent(JsonSerializer.Serialize(json), Encoding.UTF8, "application/json");
        return r;
    }
    async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    JsonElement[] Apps(JsonElement all) => all.GetProperty("apps").EnumerateArray().ToArray();

    var none = await Json(await http.GetAsync("/api/apps"));
    Check("there are no apps and no Nextcloud account to begin with", Apps(none).Length == 0 && !none.GetProperty("nextcloud").GetProperty("connected").GetBoolean());
    Check("an app needs a name and an address", (await http.SendAsync(ApiReq(HttpMethod.Post, "/api/apps", new { name = " ", url = mcp.BaseUrl + "/open" }))).StatusCode == HttpStatusCode.BadRequest);

    var added = await Json(await http.SendAsync(ApiReq(HttpMethod.Post, "/api/apps", new { name = "Notes App", url = mcp.BaseUrl + "/open" })));
    Check("a server that asks for nothing is added as open and ready, with a safe name", added.GetProperty("app").GetProperty("name").GetString() == "notes-app" && added.GetProperty("app").GetProperty("access").GetString() == "open" && added.GetProperty("app").GetProperty("ready").GetBoolean());
    var again = await Json(await http.SendAsync(ApiReq(HttpMethod.Post, "/api/apps", new { name = "Notes App", url = mcp.BaseUrl + "/open" })));
    Check("the same name again is made unique", again.GetProperty("app").GetProperty("name").GetString() == "notes-app-2");
    await http.SendAsync(ApiReq(HttpMethod.Delete, "/api/apps/notes-app-2"));

    var keyed = await Json(await http.SendAsync(ApiReq(HttpMethod.Post, "/api/apps", new { name = "keyed", url = mcp.BaseUrl + "/closed" })));
    Check("a server that wants a token waits for one", keyed.GetProperty("app").GetProperty("access").GetString() == "token" && !keyed.GetProperty("app").GetProperty("ready").GetBoolean());
    Check("a token can't be given to an app that doesn't take one", (await http.SendAsync(ApiReq(HttpMethod.Post, "/api/apps/notes-app/token", new { token = "x" }))).StatusCode == HttpStatusCode.BadRequest);
    Check("a token is accepted (with or without Bearer in front)", (await http.SendAsync(ApiReq(HttpMethod.Post, "/api/apps/keyed/token", new { token = "Bearer secret-token-1" }))).IsSuccessStatusCode && Apps(await Json(await http.GetAsync("/api/apps"))).Single(a => a.GetProperty("name").GetString() == "keyed").GetProperty("ready").GetBoolean());

    var signin = await Json(await http.SendAsync(ApiReq(HttpMethod.Post, "/api/apps", new { name = "mcp", url = mcp.BaseUrl + "/mcp" })));
    Check("a server behind OAuth is added as a sign-in", signin.GetProperty("app").GetProperty("access").GetString() == "oauth" && !signin.GetProperty("app").GetProperty("ready").GetBoolean());
    var begin = await Json(await http.SendAsync(ApiReq(HttpMethod.Post, "/api/apps/mcp/signin")));
    var authorize = new Uri(begin.GetProperty("url").GetString()!);
    var authQuery = System.Web.HttpUtility.ParseQueryString(authorize.Query);
    Check("starting a sign-in gives the service's page, returning to this server", authorize.AbsolutePath == "/authorize" && authQuery["redirect_uri"] == url + "/oauth/callback" && mcp.RegisteredRedirect == url + "/oauth/callback");
    var wrong = await http.GetAsync("/oauth/callback?code=abc&state=nope");
    Check("a return with an unknown state is refused", wrong.StatusCode == HttpStatusCode.BadRequest && (await wrong.Content.ReadAsStringAsync()).Contains("Couldn&#39;t sign in"));
    var done = await http.GetAsync("/oauth/callback?code=abc&state=" + Uri.EscapeDataString(authQuery["state"]!));
    Check("the browser coming back completes the sign-in", done.StatusCode == HttpStatusCode.OK && (await done.Content.ReadAsStringAsync()).Contains("Signed in") && Apps(await Json(await http.GetAsync("/api/apps"))).Single(a => a.GetProperty("name").GetString() == "mcp").GetProperty("ready").GetBoolean());
    Check("a return can be used only once", (await http.GetAsync("/oauth/callback?code=abc&state=" + Uri.EscapeDataString(authQuery["state"]!))).StatusCode == HttpStatusCode.BadRequest);
    Check("tokens are kept in the private file, not with the apps or settings", File.ReadAllText(Path.Combine(data, "secrets.json")).Contains("secret-token-1") && File.ReadAllText(Path.Combine(data, "secrets.json")).Contains("access-1") && !File.ReadAllText(Path.Combine(data, "apps.json")).Contains("secret-token-1") && !File.ReadAllText(Path.Combine(data, "apps.json")).Contains("access-1"));
    if (!OperatingSystem.IsWindows())
        Check("and only the user can read that file", File.GetUnixFileMode(Path.Combine(data, "secrets.json")) == (UnixFileMode.UserRead | UnixFileMode.UserWrite));

    // ----- a server that doesn't register clients: the user's own, with its secret -----
    Check("a client ID is of no use to a server that wants a token", (await http.SendAsync(ApiReq(HttpMethod.Post, "/api/apps", new { name = "nope", url = mcp.BaseUrl + "/closed", clientId = "gcid" }))).StatusCode == HttpStatusCode.BadRequest);
    var forced = await Json(await http.SendAsync(ApiReq(HttpMethod.Post, "/api/apps", new { name = "forced", url = mcp.BaseUrl + "/open", clientId = "gcid", clientSecret = "gsecret" })));
    Check("a client ID says the server signs in, even when it let the probe in", forced.GetProperty("app").GetProperty("access").GetString() == "oauth" && forced.GetProperty("app").GetProperty("ownClient").GetBoolean());
    await http.SendAsync(ApiReq(HttpMethod.Delete, "/api/apps/forced"));
    Check("a client ID with blanks is refused", (await http.SendAsync(ApiReq(HttpMethod.Post, "/api/apps", new { name = "nope", url = mcp.BaseUrl + "/gcal", clientId = "g cid" }))).StatusCode == HttpStatusCode.BadRequest);
    var plainGoogle = await http.SendAsync(ApiReq(HttpMethod.Post, "/api/apps", new { name = "gcal-plain", url = mcp.BaseUrl + "/gcal" }));
    var plainBegin = await http.SendAsync(ApiReq(HttpMethod.Post, "/api/apps/gcal-plain/signin"));
    Check("without a client, signing in to it says what to add", plainGoogle.IsSuccessStatusCode && plainBegin.StatusCode == HttpStatusCode.BadRequest && (await plainBegin.Content.ReadAsStringAsync()).Contains("client ID"));
    await http.SendAsync(ApiReq(HttpMethod.Delete, "/api/apps/gcal-plain"));
    var gadded = await Json(await http.SendAsync(ApiReq(HttpMethod.Post, "/api/apps", new { name = "google-calendar", url = mcp.BaseUrl + "/gcal", clientId = "gcid", clientSecret = "gsecret", scope = "scope-a scope-b" })));
    Check("an app added with a client says so, and its secret is not given back", gadded.GetProperty("app").GetProperty("ownClient").GetBoolean() && gadded.GetProperty("app").GetProperty("access").GetString() == "oauth" && !gadded.ToString().Contains("gsecret"));
    var gbegin = await Json(await http.SendAsync(ApiReq(HttpMethod.Post, "/api/apps/google-calendar/signin")));
    var gauth = new Uri(gbegin.GetProperty("url").GetString()!);
    var gquery = System.Web.HttpUtility.ParseQueryString(gauth.Query);
    Check("signing in goes to the service with that client, its scopes and offline access", gauth.AbsolutePath == "/gauthorize" && gquery["client_id"] == "gcid" && gquery["scope"] == "scope-a scope-b" && gquery["access_type"] == "offline" && gquery["redirect_uri"] == url + "/oauth/callback");
    var gdone = await http.GetAsync("/oauth/callback?code=abc&state=" + Uri.EscapeDataString(gquery["state"]!));
    Check("the browser coming back completes it, with the secret", gdone.StatusCode == HttpStatusCode.OK && mcp.LastGoogleForm["client_secret"] == "gsecret" && Apps(await Json(await http.GetAsync("/api/apps"))).Single(a => a.GetProperty("name").GetString() == "google-calendar").GetProperty("ready").GetBoolean());
    Check("the client's secret is in the private file, not with the apps", File.ReadAllText(Path.Combine(data, "secrets.json")).Contains("gsecret") && !File.ReadAllText(Path.Combine(data, "apps.json")).Contains("gsecret") && File.ReadAllText(Path.Combine(data, "apps.json")).Contains("gcid"));
    // The tools of the signed-in app reach the model on the next ask.
    api.FinalText = "HEARD: calendar\n---\nOk.";
    await using (var gb = await FakeBrowser.ConnectAsync(url))
    {
        await gb.UntilAsync(l => l.Any(m => m.GetProperty("t").GetString() == "page"));
        int before = api.Requests.Count(r => r.Path.EndsWith("/responses"));
        await gb.SendAsync(new { t = "type", text = "what is on my calendar?" });
        await gb.UntilAsync(_ => api.Requests.Count(r => r.Path.EndsWith("/responses")) > before, 10000);
        var sent = api.Requests.Where(r => r.Path.EndsWith("/responses")).Skip(before).FirstOrDefault()?.Body ?? "";
        Check("the signed-in Google-like app's tools are offered to the model", sent.Contains("google-calendar__list_events") && sent.Contains("\"type\":\"function\""));
        Check("and the user was not told it is unavailable", !gb.Of("toast").Any(t => t.GetProperty("message").GetString()!.Contains("Not available")));
    }
    await http.SendAsync(ApiReq(HttpMethod.Delete, "/api/apps/google-calendar"));
    Check("removing the app removes the secret", !File.ReadAllText(Path.Combine(data, "secrets.json")).Contains("gsecret"));

    // ----- Google Calendar, through Google's own API -----
    var g0 = await Json(await http.GetAsync("/api/google"));
    Check("Google Calendar isn't connected to begin with", !g0.GetProperty("connected").GetBoolean() && g0.GetProperty("clientId").ValueKind == JsonValueKind.Null && (await Json(await http.GetAsync("/api/apps"))).GetProperty("google").GetProperty("connected").ValueKind == JsonValueKind.False);
    Check("connecting needs the client first", (await http.SendAsync(ApiReq(HttpMethod.Post, "/api/google/connect"))).StatusCode == HttpStatusCode.BadRequest);
    Check("a client ID with blanks, or none, is refused", (await http.SendAsync(ApiReq(HttpMethod.Put, "/api/google/client", new { clientId = "g cid", clientSecret = "x" }))).StatusCode == HttpStatusCode.BadRequest && (await http.SendAsync(ApiReq(HttpMethod.Put, "/api/google/client", new { clientId = " " }))).StatusCode == HttpStatusCode.BadRequest);
    var g1 = await Json(await http.SendAsync(ApiReq(HttpMethod.Put, "/api/google/client", new { clientId = "gcid", clientSecret = "gsecret", write = true })));
    Check("the client is saved, and its secret is not given back", g1.GetProperty("clientId").GetString() == "gcid" && g1.GetProperty("hasSecret").GetBoolean() && g1.GetProperty("canWrite").GetBoolean() && !g1.ToString().Contains("gsecret"));
    var gcBegin = await Json(await http.SendAsync(ApiReq(HttpMethod.Post, "/api/google/connect")));
    var gcAuth = new Uri(gcBegin.GetProperty("url").GetString()!);
    var gq = System.Web.HttpUtility.ParseQueryString(gcAuth.Query);
    Check("connecting sends the browser to Google with the client, offline access, the scopes that allow changes, and back to this server", gcAuth.AbsolutePath == "/authorize" && gq["client_id"] == "gcid" && gq["access_type"] == "offline" && gq["scope"] == GoogleSignIn.WriteScopes && gq["redirect_uri"] == url + "/oauth/callback");
    var gback = await http.GetAsync("/oauth/callback?code=abc&state=" + Uri.EscapeDataString(gq["state"]!));
    Check("the browser coming back completes it, with the secret", gback.StatusCode == HttpStatusCode.OK && (await gback.Content.ReadAsStringAsync()).Contains("Google Calendar") && googleApi.LastTokenForm["client_secret"] == "gsecret" && (await Json(await http.GetAsync("/api/google"))).GetProperty("connected").GetBoolean());
    var gcheck = await Json(await http.SendAsync(ApiReq(HttpMethod.Post, "/api/google/check")));
    Check("checking lists the calendars, marking the ones that can't be changed", gcheck.GetProperty("calendars").EnumerateArray().Select(c => c.GetString()).SequenceEqual(new[] { "Personal", "Team", "Holidays (read only)" }));
    Check("the tokens are in the private file, not with the settings", File.ReadAllText(Path.Combine(data, "secrets.json")).Contains("access-g1") && File.ReadAllText(Path.Combine(data, "secrets.json")).Contains("gsecret") && !File.ReadAllText(Path.Combine(data, "settings.json")).Contains("access-g1") && !File.ReadAllText(Path.Combine(data, "settings.json")).Contains("gsecret"));
    api.FinalText = "HEARD: calendar\n---\nOk.";
    await using (var gb = await FakeBrowser.ConnectAsync(url))
    {
        await gb.UntilAsync(l => l.Any(m => m.GetProperty("t").GetString() == "page"));
        int beforeAsk = api.Requests.Count(r => r.Path.EndsWith("/responses"));
        await gb.SendAsync(new { t = "type", text = "what is on my calendar?" });
        await gb.UntilAsync(_ => api.Requests.Count(r => r.Path.EndsWith("/responses")) > beforeAsk, 10000);
        var sentAsk = api.Requests.Where(r => r.Path.EndsWith("/responses")).Skip(beforeAsk).FirstOrDefault()?.Body ?? "";
        Check("the model is offered Google Calendar, with the tools that change events because that was allowed", sentAsk.Contains("google_calendar_events") && sentAsk.Contains("google_calendar_add_event") && sentAsk.Contains("google_calendar_delete_event"));
    }
    var gsame = await Json(await http.SendAsync(ApiReq(HttpMethod.Put, "/api/google/client", new { clientId = "gcid", write = true })));
    Check("saving the same client again (secret left empty) keeps the secret and the sign-in", gsame.GetProperty("hasSecret").GetBoolean() && gsame.GetProperty("connected").GetBoolean());
    var gread = await Json(await http.SendAsync(ApiReq(HttpMethod.Put, "/api/google/client", new { clientId = "gcid", write = false })));
    Check("allowing less forgets the sign-in, which no longer fits", !gread.GetProperty("connected").GetBoolean() && !gread.GetProperty("canWrite").GetBoolean() && gread.GetProperty("hasSecret").GetBoolean());
    var gagain = System.Web.HttpUtility.ParseQueryString(new Uri((await Json(await http.SendAsync(ApiReq(HttpMethod.Post, "/api/google/connect")))).GetProperty("url").GetString()!).Query);
    await http.GetAsync("/oauth/callback?code=abc&state=" + Uri.EscapeDataString(gagain["state"]!));
    await using (var gb = await FakeBrowser.ConnectAsync(url))
    {
        await gb.UntilAsync(l => l.Any(m => m.GetProperty("t").GetString() == "page"));
        int beforeAsk = api.Requests.Count(r => r.Path.EndsWith("/responses"));
        await gb.SendAsync(new { t = "type", text = "and now?" });
        await gb.UntilAsync(_ => api.Requests.Count(r => r.Path.EndsWith("/responses")) > beforeAsk, 10000);
        var sentRead = api.Requests.Where(r => r.Path.EndsWith("/responses")).Skip(beforeAsk).FirstOrDefault()?.Body ?? "";
        Check("read-only, only reading is offered", sentRead.Contains("google_calendar_events") && !sentRead.Contains("google_calendar_add_event") && !sentRead.Contains("google_calendar_delete_event"));
    }
    await http.SendAsync(ApiReq(HttpMethod.Post, "/api/google/disconnect", new { }));
    Check("disconnecting revokes the token with Google and keeps the client", googleApi.Revoked is not null && googleApi.Revoked.StartsWith("token=") && !(await Json(await http.GetAsync("/api/google"))).GetProperty("connected").GetBoolean() && (await Json(await http.GetAsync("/api/google"))).GetProperty("clientId").GetString() == "gcid");
    var gforget = await Json(await http.SendAsync(ApiReq(HttpMethod.Post, "/api/google/disconnect", new { forget = true })));
    Check("forgetting removes the client and its secret too", gforget.GetProperty("clientId").ValueKind == JsonValueKind.Null && !File.ReadAllText(Path.Combine(data, "secrets.json")).Contains("gsecret") && !File.ReadAllText(Path.Combine(data, "secrets.json")).Contains("access-g"));

    // ----- the registry -----
    var found = await Json(await http.GetAsync("/api/registry?q=todo"));
    var servers = found.GetProperty("servers").EnumerateArray().ToArray();
    Check("registry search lists the servers that can be used, and counts the others", servers.Any(s => s.GetProperty("name").GetString() == "io.github.acme/todo-list" && s.GetProperty("url").GetString() == "https://mcp.acme.example/mcp") && found.GetProperty("hidden").GetInt32() > 0 && found.GetProperty("nextCursor").GetString() == "next-1");
    var tenant = servers.Single(s => s.GetProperty("name").GetString() == "com.example/tenant");
    Check("blanks in an address come with their choices", tenant.GetProperty("variables")[0].GetProperty("name").GetString() == "tenant" && tenant.GetProperty("variables")[0].GetProperty("choices").GetArrayLength() == 2);
    Check("a server needing a token says so", servers.Single(s => s.GetProperty("name").GetString() == "com.example/keyed").GetProperty("tokenRequired").GetBoolean());
    Check("more results follow the cursor", (await Json(await http.GetAsync("/api/registry?cursor=next-1"))).GetProperty("servers")[0].GetProperty("name").GetString() == "com.example/second");

    // ----- what the model gets, in the browser's time zone -----
    await using (var tab = await FakeBrowser.ConnectAsync(url, zone: "Asia/Tokyo"))
    {
        await tab.UntilAsync(l => l.Any(m => m.GetProperty("t").GetString() == "page"));
        await tab.SendAsync(new { t = "type", text = "what is on" });
        Check("a typed note is answered", await tab.UntilAsync(l => l.Any(m => m.GetProperty("t").GetString() == "text"), 8000));
        var body = api.Requests.Last(r => r.Path.EndsWith("/responses")).Body;
        // Longhand calls the connected apps itself: the model gets their tools, and the apps' tokens never leave this server.
        Check("open, token and signed-in apps' tools are all offered to the model", body.Contains("\"notes-app__add_note\"") && body.Contains("\"keyed__lookup\"") && body.Contains("\"mcp__search\""));
        Check("and their tokens aren't sent to the model", !body.Contains("secret-token-1") && !body.Contains("access-1"));
        Check("and the date is in the browser's time zone", body.Contains("time zone Asia/Tokyo"));
        Check("no calendar is offered until Nextcloud is connected", !body.Contains("calendar_events"));
    }

    // ----- Nextcloud -----
    Check("a plain http server on another machine is refused", (await http.SendAsync(ApiReq(HttpMethod.Post, "/api/nextcloud/connect", new { server = "http://cloud.example.com" }))).StatusCode == HttpStatusCode.BadRequest);
    var login = await Json(await http.SendAsync(ApiReq(HttpMethod.Post, "/api/nextcloud/connect", new { server = cloud.BaseUrl })));
    Check("connecting gives Nextcloud's own sign-in page to open", login.GetProperty("url").GetString() == cloud.BaseUrl + "/login/v2/flow/tok123");
    JsonElement status = default;
    for (int i = 0; i < 100; i++)
    {
        status = (await Json(await http.GetAsync("/api/apps"))).GetProperty("nextcloud");
        if (status.GetProperty("connected").GetBoolean()) break;
        await Task.Delay(50);
    }
    Check("once approved there the account shows as connected, without a password in sight", status.GetProperty("connected").GetBoolean() && status.GetProperty("user").GetString() == FakeNextcloud.User && !status.ToString().Contains(FakeNextcloud.Password));
    Check("the app password is kept with the other secrets only", File.ReadAllText(Path.Combine(data, "secrets.json")).Contains(FakeNextcloud.Password) && !File.ReadAllText(Path.Combine(data, "settings.json")).Contains(FakeNextcloud.Password));
    var calendars = await Json(await http.SendAsync(ApiReq(HttpMethod.Post, "/api/nextcloud/check")));
    Check("checking lists the calendars Claude can use, marking those it may only read", calendars.GetProperty("calendars").EnumerateArray().Select(c => c.GetString()).SequenceEqual(new[] { "Personal", "Team (read only)" }));
    await using (var tab = await FakeBrowser.ConnectAsync(url))
    {
        await tab.UntilAsync(l => l.Any(m => m.GetProperty("t").GetString() == "page"));
        int before = api.Requests.Count(r => r.Path.EndsWith("/responses"));
        await tab.SendAsync(new { t = "type", text = "and now" });
        await tab.UntilAsync(_ => api.Requests.Count(r => r.Path.EndsWith("/responses")) > before, 8000);
        Check("with Nextcloud connected Claude is offered the calendar", api.Requests.Last(r => r.Path.EndsWith("/responses")).Body.Contains("calendar_events"));
        await tab.UntilAsync(l => l.Where(m => m.GetProperty("t").GetString() == "state").LastOrDefault() is { ValueKind: JsonValueKind.Object } st && !st.GetProperty("busy").GetBoolean());
    }
    await http.SendAsync(ApiReq(HttpMethod.Post, "/api/nextcloud/disconnect"));
    Check("disconnecting revokes the app password and forgets the account", cloud.Requests.Any(q => q.Method == "DELETE") && !(await Json(await http.GetAsync("/api/apps"))).GetProperty("nextcloud").GetProperty("connected").GetBoolean() && !File.ReadAllText(Path.Combine(data, "secrets.json")).Contains(FakeNextcloud.Password));

    await http.SendAsync(ApiReq(HttpMethod.Post, "/api/apps/mcp/signout"));
    Check("signing out of an app keeps it but takes its sign-in away", !Apps(await Json(await http.GetAsync("/api/apps"))).Single(a => a.GetProperty("name").GetString() == "mcp").GetProperty("ready").GetBoolean());
    await http.SendAsync(ApiReq(HttpMethod.Delete, "/api/apps/mcp"));
    Check("removing an app forgets it", Apps(await Json(await http.GetAsync("/api/apps"))).All(a => a.GetProperty("name").GetString() != "mcp"));

    // A server run with the normal rules refuses addresses that Anthropic's servers couldn't reach.
    var strictData = Path.Combine(data, "strict");
    var strict = WebApp.Build(new WebOptions { DataDir = strictData, Urls = "http://127.0.0.1:0", SignIn = api.SignIn.ToJson() });
    await strict.StartAsync();
    using (var strictHttp = new HttpClient { BaseAddress = new Uri(strict.Urls.First().Replace("[::1]", "127.0.0.1")) })
    {
        foreach (var bad in new[] { mcp.BaseUrl + "/open", "https://192.168.1.5/mcp", "http://example.com/mcp", "https://localhost/mcp" })
        {
            var r = new HttpRequestMessage(HttpMethod.Post, "/api/apps") { Content = new StringContent(JsonSerializer.Serialize(new { name = "x", url = bad }), Encoding.UTF8, "application/json") };
            r.Headers.Add("X-Longhand", "1");
            Check("an address Anthropic can't reach is refused: " + bad, (await strictHttp.SendAsync(r)).StatusCode == HttpStatusCode.BadRequest);
        }
    }
    await strict.StopAsync();

    // ----- development mode: the page comes from a folder, and tabs are told when it changes -----
    var www = Path.Combine(data, "www");
    Directory.CreateDirectory(www);
    File.WriteAllText(Path.Combine(www, "index.html"), "<!doctype html><canvas></canvas><p>version one</p>");
    var devApp = WebApp.Build(new WebOptions { DataDir = Path.Combine(data, "dev-data"), Urls = "http://127.0.0.1:0", SignIn = api.SignIn.ToJson(), WwwRoot = www });
    await devApp.StartAsync();
    var devUrl = devApp.Urls.First().Replace("[::1]", "127.0.0.1");
    using (var devHttp = new HttpClient { BaseAddress = new Uri(devUrl) })
    {
        Check("in development the page is served from the folder", (await devHttp.GetStringAsync("/")).Contains("version one"));
        await using var tab = await FakeBrowser.ConnectAsync(devUrl);
        Check("a tab is told it is in development mode", await tab.UntilAsync(l => l.Any(m => m.GetProperty("t").GetString() == "dev")));
        File.WriteAllText(Path.Combine(www, "index.html"), "<!doctype html><canvas></canvas><p>version two</p>");
        Check("editing a file tells the tab to reload", await tab.UntilAsync(l => l.Any(m => m.GetProperty("t").GetString() == "reload")));
        Check("and the new content is served without a rebuild", (await devHttp.GetStringAsync("/")).Contains("version two"));
        Check("paths outside the folder are still refused", (await devHttp.GetAsync("/..%2f..%2fetc/passwd")).StatusCode == HttpStatusCode.NotFound);
    }
    await devApp.StopAsync();
    await using (var plain = await FakeBrowser.ConnectAsync(url))
    {
        await plain.UntilAsync(l => l.Any(m => m.GetProperty("t").GetString() == "page"));
        Check("without --www no tab is told it is in development mode", !plain.Of("dev").Any());
    }

    // ----- stopping the server with a tab still open (Ctrl+C) -----
    await using (var open = await FakeBrowser.ConnectAsync(url))
    {
        await open.UntilAsync(l => l.Any(m => m.GetProperty("t").GetString() == "page"));
        await open.SendAsync(new { t = "stroke", stroke = new { id = 77, pts = new[] { new[] { 100.0, 160.0 }, new[] { 150.0, 180.0 } } } });
        await Task.Delay(200);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await app.StopAsync();
        Check($"the server stops within a few seconds with a tab open ({stopwatch.ElapsedMilliseconds} ms)", stopwatch.Elapsed < TimeSpan.FromSeconds(8));
        var saved = Directory.GetFiles(data, "ink.json", SearchOption.AllDirectories).Select(File.ReadAllText).Any(t => t.Contains("\"Id\":77"));
        Check("and what was on the page is saved on the way out", saved);
    }
}
catch (Exception ex)
{
    Console.WriteLine("FAIL  unexpected error: " + ex);
    failures++;
}
finally
{
    await app.StopAsync();
    try { Directory.Delete(data, recursive: true); } catch (IOException) { }
}

Console.WriteLine(failures == 0 ? "All web checks passed." : $"{failures} web check(s) failed.");
return failures == 0 ? 0 : 1;
