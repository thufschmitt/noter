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

var connectors = ConnectorList.Parse("# comment\nfirst https://first.example/mcp tok_abc\ntodo https://example.com/mcp\nbad http://insecure.example/mcp\nfirst https://dup.example/mcp\nlonely");
Check("connectors parsed", connectors.Count == 2 && connectors[0].Name == "first" && connectors[0].Token == "tok_abc" && connectors[1].Token is null);
Check("unusable connector lines reported", ConnectorList.Rejected("ok https://a.example/m\nbad http://a.example/m\nlonely").SequenceEqual(new[] { "bad", "lonely" }));

// Constructing the service exercises the SDK types used to build the request.
_ = new ClaudeService("sk-ant-placeholder", connectors);
Check("service constructs", true);

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
