using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>Plays the part of the page in a browser: answers the server's requests to measure text and draw pictures, and keeps what it was sent.</summary>
sealed class FakeBrowser : IAsyncDisposable
{
    // A 1x1 PNG, standing in for the picture a browser would draw of the ink.
    public const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    readonly ClientWebSocket _ws = new();
    readonly List<JsonElement> _log = new();
    Task _loop = Task.CompletedTask;
    public int Snapshots;
    /// <summary>Where the browser says the parts of a drawing are (id to [x, y, w, h]), when it measures one.</summary>
    public Dictionary<string, double[]>? Boxes;
    public JsonElement LastSnapshotRequest;

    public static async Task<FakeBrowser> ConnectAsync(string httpUrl, double width = 800, string? zone = null)
    {
        var b = new FakeBrowser();
        await b._ws.ConnectAsync(new Uri(httpUrl.Replace("http://", "ws://") + "/ws"), CancellationToken.None);
        b._loop = Task.Run(b.ReadAsync);
        await b.SendAsync(new { t = "hello", width, zone });
        return b;
    }

    public List<JsonElement> Log { get { lock (_log) return _log.ToList(); } }
    public IEnumerable<JsonElement> Of(string type) => Log.Where(m => m.GetProperty("t").GetString() == type);

    async Task ReadAsync()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (_ws.State == WebSocketState.Open)
            {
                using var ms = new System.IO.MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(buffer, CancellationToken.None);
                    ms.Write(buffer, 0, r.Count);
                } while (!r.EndOfMessage);
                if (r.MessageType == WebSocketMessageType.Close) break;
                var m = JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
                lock (_log) _log.Add(m);
                switch (m.GetProperty("t").GetString())
                {
                    case "measure":
                        var lines = (int)Math.Ceiling(m.GetProperty("text").GetString()!.Length / 30.0);
                        if (Boxes is not null && m.TryGetProperty("blocks", out var blocks) && blocks.GetArrayLength() > 0)
                            await SendAsync(new { t = "reply", req = m.GetProperty("req").GetInt32(), value = new { lines, aspects = new Dictionary<string, double>(), boxes = blocks.EnumerateArray().ToDictionary(b => b.GetInt32().ToString(), _ => Boxes) } });
                        else
                            await SendAsync(new { t = "reply", req = m.GetProperty("req").GetInt32(), value = lines });
                        break;
                    case "snapshot":
                        Snapshots++;
                        LastSnapshotRequest = m;
                        await SendAsync(new { t = "reply", req = m.GetProperty("req").GetInt32(), value = Png });
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
    }

    public Task SendAsync(object message) =>
        _ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)), WebSocketMessageType.Text, true, CancellationToken.None);

    public async Task<bool> UntilAsync(Func<List<JsonElement>, bool> condition, int ms = 5000)
    {
        for (int waited = 0; waited < ms; waited += 20)
        {
            if (condition(Log)) return true;
            await Task.Delay(20);
        }
        return condition(Log);
    }

    /// <summary>The newest message of this type, or null.</summary>
    public JsonElement? Last(string type) => Of(type).Cast<JsonElement?>().LastOrDefault();

    public async Task CloseAsync()
    {
        try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
        await Task.WhenAny(_loop, Task.Delay(3000));
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        _ws.Dispose();
    }
}
