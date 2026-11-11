using System;
using System.Linq;
using Longhand.Services;

int failures = 0;
void Check(string name, bool ok) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}"); if (!ok) failures++; }

var p = ReplyParser.Parse("HEARD: what is 2+2\n---\n2 + 2 = 4.", finished: true);
Check("well-formed reply", p.Ready && p.Heard == "what is 2+2" && p.Body == "2 + 2 = 4.");

var partial = ReplyParser.Parse("HEARD: what is", finished: false);
Check("partial header is not shown yet", !partial.Ready);

var streaming = ReplyParser.Parse("HEARD: hi\n---\nHel", finished: false);
Check("body streams after separator", streaming.Ready && streaming.Body == "Hel");

var noFormat = ReplyParser.Parse("Just an answer.", finished: true);
Check("reply without format falls back to whole text", noFormat.Ready && noFormat.Body == "Just an answer.");

var md = ReplyParser.Clean("**Bold** and `code`\n* item");
Check("markdown stripped", md == "Bold and code\n– item");

// Which earlier replies are shown when new ink is written over them.
var replies = new[] { (Top: 160.0, Bottom: 280.0), (Top: 400.0, Bottom: 520.0), (Top: 640.0, Bottom: 760.0) };
Check("ink after the last reply shows no context", AnnotationContext.Find(800, 860, replies, 120) is null);
Check("ink between replies shows no context", AnnotationContext.Find(540, 600, replies, 120) is null);
var over = AnnotationContext.Find(430, 470, replies, 120);
Check("ink over a reply shows it and what it answered", over is { } o1 && o1.Replies.SequenceEqual(new[] { 1 }) && o1.From == 280 && o1.To == 520);
var first = AnnotationContext.Find(200, 220, replies, 120);
Check("the first reply answers what is above it on the page", first is { } o2 && o2.From == 120 && o2.To == 280);
var both = AnnotationContext.Find(250, 700, replies, 120);
Check("ink spanning replies shows all of them", both is { } o3 && o3.Replies.SequenceEqual(new[] { 0, 1, 2 }) && o3.From == 120 && o3.To == 760);

// Resizing a rendered reply to the size it should have, whatever size the renderer produced.
byte[] Px(int w, int h, Func<int, int, byte> alpha) { var b = new byte[w * h * 4]; for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) b[(y * w + x) * 4 + 3] = alpha(x, y); return b; }
var solid = Px(4, 4, (x, y) => 200);
Check("same size keeps the coverage", AlphaMask.Resize(solid, 4, 4, 4, 4).All(a => a == 200));
Check("a bitmap twice too large comes back to the right size", AlphaMask.Resize(solid, 4, 4, 2, 2).Length == 4 && AlphaMask.Resize(solid, 4, 4, 2, 2).All(a => a == 200));
var leftHalf = Px(4, 2, (x, y) => (byte)(x < 2 ? 255 : 0));
var halved = AlphaMask.Resize(leftHalf, 4, 2, 2, 1);
Check("shrinking keeps what is where", halved[0] == 255 && halved[1] == 0);
var smaller = AlphaMask.Resize(leftHalf, 4, 2, 8, 4);
Check("enlarging keeps what is where", smaller[0] == 255 && smaller[7] == 0 && smaller[3] == 255 && smaller[4] == 0);
Check("a blend is averaged", AlphaMask.Resize(Px(2, 1, (x, y) => (byte)(x == 0 ? 100 : 200)), 2, 1, 1, 1)[0] == 150);

Check("default model is Haiku", ModelChoice.Default == "claude-haiku-5-5");
Check("unknown saved model falls back to the default", ModelChoice.Resolve("claude-gone") == ModelChoice.Default && ModelChoice.Resolve(null) == ModelChoice.Default);
Check("a listed model is kept", ModelChoice.Resolve("claude-opus-5-5") == "claude-opus-5-5");
Check("low effort only for the larger models", !ModelChoice.UsesLowEffort("claude-haiku-5-5") && ModelChoice.UsesLowEffort("claude-opus-5-5") && ModelChoice.UsesLowEffort("claude-sonnet-5-5"));

// Conversations: the list, titles, renaming, deleting and cleaning up blanks.
var t0 = new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
var index = new ConversationIndex();
var oldest = index.Create(t0);
var blankOne = index.Create(t0.AddMinutes(5));
Check("a new conversation is open and blank", index.CurrentId == blankOne.Id && blankOne.Blank && blankOne.Title == "New conversation");
index.Touch(oldest.Id, t0.AddMinutes(10), blank: false);
Check("the list is newest oldest", index.Newest().First().Id == oldest.Id);

index.SetAutoTitle(oldest.Id, "Make a list 1-5 in separate lines");
index.SetAutoTitle(oldest.Id, "Something said later");
Check("the title comes from the oldest exchange only", index.Find(oldest.Id)!.Title == "Make a list 1-5 in separate lines");
index.Rename(oldest.Id, "  Shopping   list ");
Check("a given name wins and is tidied", index.Find(oldest.Id)!.Title == "Shopping list");
index.SetAutoTitle(oldest.Id, "Ignored");
Check("a given name is never overwritten", index.Find(oldest.Id)!.Title == "Shopping list");
index.Rename(oldest.Id, "");
Check("an empty name goes back to the automatic title", index.Find(oldest.Id)!.Title == "Make a list 1-5 in separate lines");

Check("a drawing is not a title", ConversationIndex.TitleFrom("[a sketch of a house] what is this") == "what is this");
Check("only a drawing is kept as the title", ConversationIndex.TitleFrom("[a sketch of a house]") == "a sketch of a house");
var longTitle = ConversationIndex.TitleFrom("Please write me a very long and rambling question about the weather in Paris tomorrow");
Check("long titles are cut at a word", longTitle.Length <= ConversationIndex.MaxTitle + 1 && longTitle.EndsWith("…") && !longTitle.Contains("tomorrow"));
Check("nothing heard gives no title", ConversationIndex.TitleFrom("   ") == "");

var removed = index.RemoveBlank(keepId: null);
Check("blank conversations are removed", removed.SequenceEqual(new[] { blankOne.Id }) && index.Items.Count == 1);
var newest = index.Create(t0.AddHours(1));
Check("the blank one being used is kept", index.RemoveBlank(keepId: newest.Id).Count == 0);

index.Touch(newest.Id, t0.AddHours(2), blank: false);
Check("deleting another conversation keeps the open one", index.Delete(oldest.Id) == newest.Id && index.CurrentId == newest.Id);
Check("deleting the open one opens the newest left", index.Delete(newest.Id) is null && index.Items.Count == 0);

var saved = new ConversationIndex();
var kept = saved.Create(t0);
saved.Rename(kept.Id, "Kept");
var restored = ConversationIndex.Parse(saved.ToJson());
Check("the list survives saving and loading", restored.Items.Count == 1 && restored.Find(kept.Id)!.Title == "Kept" && restored.CurrentId == kept.Id);
Check("a damaged file gives an empty list", ConversationIndex.Parse("{ not json").Items.Count == 0 && ConversationIndex.Parse(null).CurrentId is null);
var dangling = ConversationIndex.Parse($$"""{"Items":[{"Id":"a","AutoTitle":"A","CustomTitle":"","Updated":"2026-11-10T09:00:00+00:00","Blank":false}],"CurrentId":"gone"}""");
Check("an open conversation that no longer exists falls back to the newest", dangling.CurrentId == "a");

// Constructing the service exercises the SDK types used to build the request.
_ = new ClaudeService("sk-ant-placeholder", new[] { new Connector("x", "https://x.example/mcp", "t") });
Check("service constructs", true);

// MCP sign-in against a small in-process fake of an OAuth-protected server.
await using (var fake = new FakeOAuthServer())
{
    var oauth = new McpOAuth();
    var request = await oauth.BeginAsync(fake.BaseUrl + "/mcp", McpOAuth.RedirectUri, default);
    var q = McpOAuth.ParseQuery(request.AuthorizeUrl.Query);
    Check("sign-in url built from discovery", request.AuthorizeUrl.AbsolutePath == "/authorize" && q["client_id"] == "cid-1" && q["code_challenge_method"] == "S256" && q["redirect_uri"] == McpOAuth.RedirectUri && q["resource"] == fake.BaseUrl + "/mcp");
    Check("scopes asked for, with offline access", q["scope"] == "read write offline_access");
    Check("client registered as a public client with the https redirect", fake.RegisteredRedirect == McpOAuth.RedirectUri && fake.RegisteredAuthMethod == "none");

    var session = await oauth.FinishAsync(request, "?code=abc&state=" + Uri.EscapeDataString(request.State), default);
    Check("code traded for tokens", session.AccessToken == "access-1" && session.RefreshToken == "refresh-1" && !session.NeedsRefresh);
    Check("pkce verifier sent and matches the challenge", fake.LastVerifier == request.Verifier && q["code_challenge"] == fake.Challenge(request.Verifier));

    var refreshed = await oauth.RefreshAsync(session, default);
    Check("refresh gives a new access token and keeps the refresh token", refreshed.AccessToken == "access-2" && refreshed.RefreshToken == "refresh-1");

    var failed = false;
    try { await oauth.FinishAsync(request, "?code=abc&state=wrong", default); } catch (InvalidOperationException) { failed = true; }
    Check("mismatched state is rejected", failed);
    failed = false;
    try { await oauth.FinishAsync(request, "?error=access_denied&error_description=No", default); } catch (InvalidOperationException ex) { failed = ex.Message == "No"; }
    Check("denied sign-in reports why", failed);
}

// Optional: set LONGHAND_MCP_URL to a real MCP server to run discovery and registration against it (this registers a client with it).
if (Environment.GetEnvironmentVariable("LONGHAND_MCP_URL") is { Length: > 0 } mcpUrl)
{
    var real = await new McpOAuth().BeginAsync(mcpUrl, McpOAuth.RedirectUri, default);
    Console.WriteLine($"sign-in page: {real.AuthorizeUrl.GetLeftPart(UriPartial.Path)} (client {real.ClientId}, token endpoint {real.TokenEndpoint})");
    Console.WriteLine("  scope: " + (McpOAuth.ParseQuery(real.AuthorizeUrl.Query).TryGetValue("scope", out var scope) ? scope : "(none)"));
}

// Optional: stream against a local fake of the API (set LONGHAND_FAKE_API to its URL), to exercise the request and event loop.
if (Environment.GetEnvironmentVariable("LONGHAND_FAKE_API") is { Length: > 0 } fakeApi)
{
    var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
    var updates = 0;
    var result = await new ClaudeService("sk-ant-placeholder", baseUrl: fakeApi).AskAsync(png, new[] { new Turn("hello", "Hi there.") }, _ => updates++, default);
    Check("streamed reply parsed", result.Outcome == ReplyOutcome.Done && result.Heard == "what is 2+2" && result.Body == "2 + 2 = 4.");
    Check("progress reported while streaming", updates >= 1);
}

await AgentChecks.RunAsync(Check);
await RegistryChecks.RunAsync(Check);
await SymbolChecks.RunAsync(Check);
await CachingChecks.RunAsync(Check);
await ModelChecks.RunAsync(Check);
await PageChecks.RunAsync(Check);
await BookChecks.RunAsync(Check);

Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} check(s) failed.");
return failures == 0 ? 0 : 1;
