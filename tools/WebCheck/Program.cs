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
await using var mcp = new FakeOAuthServer();
await using var registry = new FakeRegistry();
await using var cloud = new FakeNextcloud();
var app = WebApp.Build(new WebOptions
{
    DataDir = data, Urls = "http://127.0.0.1:0", AnthropicBaseUrl = api.BaseUrl, ApiKey = "sk-ant-test",
    AllowPrivateApps = true, RegistryBaseUrl = registry.BaseUrl, NextcloudPollInterval = TimeSpan.FromMilliseconds(30),
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
        await b.SendAsync(new { t = "stroke", stroke = new { id = 1, pts = new[] { new[] { 100.0, 160.0 }, new[] { 140.0, 190.0 }, new[] { 180.0, 160.0 } } } });
        Check("writing makes the page askable", await b.UntilAsync(l => l.Any(m => m.GetProperty("t").GetString() == "state" && m.GetProperty("canAsk").GetBoolean())));
        await Task.Delay(2600);
        Check("nothing is asked by itself", b.Snapshots == 0 && !api.Requests.Any(r => r.Path.EndsWith("/messages")));

        // ----- asking -----
        await b.SendAsync(new { t = "ask" });
        Check("Ask gets a reply", await b.UntilAsync(l => l.Any(m => m.GetProperty("t").GetString() == "place" && m.GetProperty("y").GetDouble() == 200), 8000));
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

        // The ask above is still being answered when its request reaches the fake API; typing is refused until it is done.
        await b.UntilAsync(l => l.Where(m => m.GetProperty("t").GetString() == "state").LastOrDefault() is { ValueKind: JsonValueKind.Object } st && !st.GetProperty("busy").GetBoolean());
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

    // ----- the registry -----
    var found = await Json(await http.GetAsync("/api/registry?q=todo"));
    var servers = found.GetProperty("servers").EnumerateArray().ToArray();
    Check("registry search lists the servers that can be used, and counts the others", servers.Any(s => s.GetProperty("name").GetString() == "io.github.acme/todo-list" && s.GetProperty("url").GetString() == "https://mcp.acme.example/mcp") && found.GetProperty("hidden").GetInt32() > 0 && found.GetProperty("nextCursor").GetString() == "next-1");
    var tenant = servers.Single(s => s.GetProperty("name").GetString() == "com.example/tenant");
    Check("blanks in an address come with their choices", tenant.GetProperty("variables")[0].GetProperty("name").GetString() == "tenant" && tenant.GetProperty("variables")[0].GetProperty("choices").GetArrayLength() == 2);
    Check("a server needing a token says so", servers.Single(s => s.GetProperty("name").GetString() == "com.example/keyed").GetProperty("tokenRequired").GetBoolean());
    Check("more results follow the cursor", (await Json(await http.GetAsync("/api/registry?cursor=next-1"))).GetProperty("servers")[0].GetProperty("name").GetString() == "com.example/second");

    // ----- the apps reach Claude, in the browser's time zone -----
    await using (var tab = await FakeBrowser.ConnectAsync(url, zone: "Asia/Tokyo"))
    {
        await tab.UntilAsync(l => l.Any(m => m.GetProperty("t").GetString() == "page"));
        await tab.SendAsync(new { t = "type", text = "what is on" });
        Check("the connected apps are sent to Claude with their tokens", await tab.UntilAsync(l => l.Any(m => m.GetProperty("t").GetString() == "text"), 8000));
        var body = api.Requests.Last(r => r.Path.EndsWith("/messages")).Body;
        Check("open, token and signed-in apps all travel (each with what it needs)", body.Contains("mcp_servers") && body.Contains("/open") && body.Contains("secret-token-1") && body.Contains("access-1"));
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
        int before = api.Requests.Count(r => r.Path.EndsWith("/messages"));
        await tab.SendAsync(new { t = "type", text = "and now" });
        await tab.UntilAsync(_ => api.Requests.Count(r => r.Path.EndsWith("/messages")) > before, 8000);
        Check("with Nextcloud connected Claude is offered the calendar", api.Requests.Last(r => r.Path.EndsWith("/messages")).Body.Contains("calendar_events"));
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
    var strict = WebApp.Build(new WebOptions { DataDir = strictData, Urls = "http://127.0.0.1:0", ApiKey = "sk-ant-test" });
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
    var devApp = WebApp.Build(new WebOptions { DataDir = Path.Combine(data, "dev-data"), Urls = "http://127.0.0.1:0", ApiKey = "sk-ant-test", WwwRoot = www });
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
