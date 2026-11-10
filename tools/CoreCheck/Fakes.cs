using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;

/// <summary>A tiny local HTTP server for the checks: a handler answers each request, and every request is kept for inspection.</summary>
abstract class FakeServer : IAsyncDisposable
{
    public sealed record Request(string Method, string Path, string Query, Dictionary<string, string> Headers, string Body);

    readonly HttpListener _listener = new();
    readonly Task _loop;
    public List<Request> Requests { get; } = new();
    public string BaseUrl { get; }

    protected FakeServer()
    {
        var port = new Random().Next(20000, 40000);
        BaseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _loop = Task.Run(Serve);
    }

    protected abstract Task Handle(Request request, HttpListenerResponse response);

    protected static async Task Write(HttpListenerResponse response, int status, string contentType, string body)
    {
        response.StatusCode = status;
        response.ContentType = contentType;
        var bytes = Encoding.UTF8.GetBytes(body);
        await response.OutputStream.WriteAsync(bytes);
    }

    async Task Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext c;
            try { c = await _listener.GetContextAsync(); } catch { return; }
            try
            {
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string? key in c.Request.Headers.AllKeys) if (key is not null) headers[key] = c.Request.Headers[key] ?? "";
                var body = await new StreamReader(c.Request.InputStream).ReadToEndAsync();
                var request = new Request(c.Request.HttpMethod, c.Request.Url!.AbsolutePath, c.Request.Url.Query, headers, body);
                lock (Requests) Requests.Add(request);
                await Handle(request, c.Response);
            }
            catch (Exception)
            {
                c.Response.StatusCode = 500;
            }
            finally
            {
                c.Response.Close();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Close();
        await _loop;
    }
}

/// <summary>Enough of a Nextcloud server for the sign-in flow and the calendar queries.</summary>
sealed class FakeNextcloud : FakeServer
{
    public const string User = "alice", Password = "app-pass-123";
    int _polls;

    public string Ics { get; set; } = "";

    protected override async Task Handle(Request r, HttpListenerResponse response)
    {
        string basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}"));
        bool authorized = r.Headers.TryGetValue("Authorization", out var auth) && auth == basic;

        if (r.Path == "/index.php/login/v2" && r.Method == "POST")
            await Write(response, 200, "application/json", $$"""{"poll":{"token":"tok123","endpoint":"{{BaseUrl}}/login/v2/poll"},"login":"{{BaseUrl}}/login/v2/flow/tok123"}""");
        else if (r.Path == "/login/v2/poll" && r.Method == "POST")
        {
            if (!r.Body.Contains("token=tok123")) await Write(response, 400, "text/plain", "bad token");
            else if (++_polls < 3) await Write(response, 404, "text/plain", "");
            else await Write(response, 200, "application/json", $$"""{"server":"{{BaseUrl}}","loginName":"{{User}}","appPassword":"{{Password}}"}""");
        }
        else if (r.Path == "/ocs/v2.php/core/apppassword" && r.Method == "DELETE")
            await Write(response, authorized ? 200 : 401, "text/plain", "");
        else if (!authorized)
            await Write(response, 401, "text/plain", "no");
        else if (r.Method == "PROPFIND" && r.Path == $"/remote.php/dav/calendars/{User}/")
            await Write(response, 207, "application/xml", """
                <?xml version="1.0"?>
                <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
                  <d:response><d:href>/remote.php/dav/calendars/alice/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response>
                  <d:response><d:href>/remote.php/dav/calendars/alice/personal/</d:href><d:propstat><d:prop><d:displayname>Personal</d:displayname>
                    <d:resourcetype><d:collection/><c:calendar/></d:resourcetype>
                    <c:supported-calendar-component-set><c:comp name="VEVENT"/><c:comp name="VTODO"/></c:supported-calendar-component-set></d:prop></d:propstat></d:response>
                  <d:response><d:href>/remote.php/dav/calendars/alice/tasks/</d:href><d:propstat><d:prop><d:displayname>Tasks</d:displayname>
                    <d:resourcetype><d:collection/><c:calendar/></d:resourcetype>
                    <c:supported-calendar-component-set><c:comp name="VTODO"/></c:supported-calendar-component-set></d:prop></d:propstat></d:response>
                  <d:response><d:href>/remote.php/dav/calendars/alice/inbox/</d:href><d:propstat><d:prop><d:displayname>Inbox</d:displayname>
                    <d:resourcetype><d:collection/><c:schedule-inbox/></d:resourcetype></d:prop></d:propstat></d:response>
                </d:multistatus>
                """);
        else if (r.Method == "REPORT" && r.Path == $"/remote.php/dav/calendars/{User}/personal/")
            await Write(response, 207, "application/xml", $"""
                <?xml version="1.0"?>
                <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
                  <d:response><d:href>/remote.php/dav/calendars/alice/personal/a.ics</d:href><d:propstat><d:prop>
                    <c:calendar-data>{System.Security.SecurityElement.Escape(Ics)}</c:calendar-data></d:prop></d:propstat></d:response>
                </d:multistatus>
                """);
        else
            await Write(response, 404, "text/plain", "nope");
    }
}

/// <summary>Enough of the Anthropic messages API to check the tool loop: it asks for the calendar once, then answers.</summary>
sealed class FakeAnthropic : FakeServer
{
    public string FinalText { get; set; } = "HEARD: what is on tomorrow\n---\nYou have a standup at 9.";

    static string Sse(string type, string json) => $"event: {type}\ndata: {json}\n\n";

    static string Message(string id) =>
        Sse("message_start", """{"type":"message_start","message":{"id":"@ID@","type":"message","role":"assistant","model":"claude-haiku-5-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":1}}}""".Replace("@ID@", id));

    protected override async Task Handle(Request r, HttpListenerResponse response)
    {
        if (r.Method != "POST" || !r.Path.EndsWith("/messages")) { await Write(response, 404, "text/plain", ""); return; }

        int calls;
        lock (Requests) calls = Requests.FindAll(q => q.Path.EndsWith("/messages")).Count;
        var sb = new StringBuilder();
        sb.Append(Message("msg_" + calls));
        if (!r.Body.Contains("\"tool_result\""))
        {
            // First call: a little thinking, a few words, then the tool call.
            sb.Append(Sse("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}"""));
            sb.Append(Sse("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Need the calendar."}}"""));
            sb.Append(Sse("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"sig-abc"}}"""));
            sb.Append(Sse("content_block_stop", """{"type":"content_block_stop","index":0}"""));
            sb.Append(Sse("content_block_start", """{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"calendar_events","input":{}}}"""));
            sb.Append(Sse("content_block_delta", """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"start\":\"2026-11-11\","}}"""));
            sb.Append(Sse("content_block_delta", """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"\"end\":\"2026-11-11\"}"}}"""));
            sb.Append(Sse("content_block_stop", """{"type":"content_block_stop","index":1}"""));
            sb.Append(Sse("message_delta", """{"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":20}}"""));
        }
        else
        {
            sb.Append(Sse("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""));
            foreach (var piece in Chunks(FinalText))
                sb.Append(Sse("content_block_delta", "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":" + System.Text.Json.JsonSerializer.Serialize(piece) + "}}"));
            sb.Append(Sse("content_block_stop", """{"type":"content_block_stop","index":0}"""));
            sb.Append(Sse("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":30}}"""));
        }
        sb.Append(Sse("message_stop", """{"type":"message_stop"}"""));
        await Write(response, 200, "text/event-stream", sb.ToString());
    }

    static IEnumerable<string> Chunks(string text)
    {
        for (int i = 0; i < text.Length; i += 7) yield return text.Substring(i, Math.Min(7, text.Length - i));
    }
}
