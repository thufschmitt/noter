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

Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} check(s) failed.");
return failures == 0 ? 0 : 1;
