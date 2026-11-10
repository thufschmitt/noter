using System;
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
await using var api = new FakeAnthropic { AskForTool = false };
var app = WebApp.Build(new WebOptions { DataDir = data, Urls = "http://127.0.0.1:0", AnthropicBaseUrl = api.BaseUrl, ApiKey = "sk-ant-test" });
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
    Check("settings say there is a key and list the models", settings.GetProperty("hasKey").GetBoolean() && settings.GetProperty("keySource").GetString() == "environment" && settings.GetProperty("models").GetArrayLength() == 3 && settings.GetProperty("model").GetString() == "claude-haiku-5-5");

    var noHeader = await http.PutAsync("/api/settings", new StringContent("""{"model":"claude-sonnet-5-5"}""", Encoding.UTF8, "application/json"));
    Check("a change without the page's header is refused", noHeader.StatusCode == HttpStatusCode.Forbidden);
    using (var crossSite = new HttpRequestMessage(HttpMethod.Put, "/api/settings") { Content = new StringContent("""{"model":"claude-sonnet-5-5"}""", Encoding.UTF8, "application/json") })
    {
        crossSite.Headers.Add("X-Longhand", "1");
        crossSite.Headers.Add("Origin", "http://evil.example");
        Check("a change from another site is refused", (await http.SendAsync(crossSite)).StatusCode == HttpStatusCode.Forbidden);
    }
    HttpRequestMessage Put(string path, string json) { var r = new HttpRequestMessage(HttpMethod.Put, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") }; r.Headers.Add("X-Longhand", "1"); return r; }
    Check("the model can be changed", (await http.SendAsync(Put("/api/settings", """{"model":"claude-sonnet-5-5"}"""))).IsSuccessStatusCode && JsonDocument.Parse(await http.GetStringAsync("/api/settings")).RootElement.GetProperty("model").GetString() == "claude-sonnet-5-5");
    await http.SendAsync(Put("/api/settings", """{"model":"claude-haiku-5-5"}"""));

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
        await b.SendAsync(new { t = "strokeStarted" });
        await b.SendAsync(new { t = "stroke", stroke = new { id = 1, pts = new[] { new[] { 100.0, 160.0 }, new[] { 140.0, 190.0 }, new[] { 180.0, 160.0 } } } });
        Check("writing makes the page askable and starts the pause", await b.UntilAsync(l => l.Any(m => m.GetProperty("t").GetString() == "state" && m.GetProperty("canAsk").GetBoolean()) && l.Any(m => m.GetProperty("t").GetString() == "idle" && m.GetProperty("progress").ValueKind == JsonValueKind.Number)));

        // ----- asking (the pause does it by itself) -----
        Check("the pause ends in a reply", await b.UntilAsync(l => l.Any(m => m.GetProperty("t").GetString() == "place" && m.GetProperty("y").GetDouble() == 200), 8000));
        Check("the browser was asked for a picture of the ink", await b.UntilAsync(_ => b.Snapshots == 1) && b.LastSnapshotRequest.GetProperty("ids").EnumerateArray().Select(e => e.GetInt32()).SequenceEqual(new[] { 1 }));
        Check("the reply text was sent as it arrived", await b.UntilAsync(l => l.Any(m => m.GetProperty("t").GetString() == "text" && m.GetProperty("text").GetString() == "You have a standup at 9.")));
        Check("the request carried the picture the browser drew", api.Requests.Any(r => r.Path.EndsWith("/messages") && r.Body.Contains(FakeBrowser.Png[..40])));
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
        Check("the next ask tells Claude about the symbol", await b.UntilAsync(_ => api.Requests.Count(r => r.Path.EndsWith("/messages")) == 2, 8000) && api.Requests.Last(r => r.Path.EndsWith("/messages")).Body.Replace("\\u0022", "\"").Contains("Symbol \"envelope\" means: Add what I wrote to my Todoist inbox."));
        Check("it was the second of two snapshots", b.Snapshots == 1);

        await b.SendAsync(new { t = "type", text = "typed hello" });
        Check("a typed note shows on the page as the user's, and is answered", await b.UntilAsync(l => l.Any(m => m.GetProperty("t").GetString() == "note" && m.GetProperty("typed").GetBoolean() && m.GetProperty("text").GetString() == "typed hello") && api.Requests.Count(r => r.Path.EndsWith("/messages")) == 3, 8000));
        Check("what Claude was sent is the text", api.Requests.Last(r => r.Path.EndsWith("/messages")).Body.Contains("typed on a keyboard:\\ntyped hello"));
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
