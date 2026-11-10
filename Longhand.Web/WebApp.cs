using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Longhand.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Longhand.Web;

/// <summary>Builds the server: the page, the WebSocket the notebook talks over, and a small settings API.</summary>
public static class WebApp
{
    static readonly Dictionary<string, string> ContentTypes = new()
    {
        [".html"] = "text/html; charset=utf-8", [".js"] = "text/javascript; charset=utf-8", [".css"] = "text/css; charset=utf-8", [".svg"] = "image/svg+xml",
    };

    public static WebApplication Build(WebOptions options)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(options.Urls);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        // Connections end themselves when the server stops (see /ws); this only bounds anything that doesn't.
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(10));
        var store = new WebStore(options);
        var loopbackOnly = options.Urls.Split(';', StringSplitOptions.RemoveEmptyEntries).All(IsLoopbackUrl);
        var app = builder.Build();
        var apps = new WebApps(store, app.Lifetime.ApplicationStopping);
        DevReload? dev = null;
        if (options.WwwRoot is { Length: > 0 })
        {
            options.WwwRoot = Path.GetFullPath(options.WwwRoot);
            if (!Directory.Exists(options.WwwRoot)) throw new DirectoryNotFoundException("--www folder not found: " + options.WwwRoot);
            dev = new DevReload(options.WwwRoot);
            app.Lifetime.ApplicationStopped.Register(dev.Dispose);
        }
        app.UseWebSockets();

        // No sign-in: on loopback the only risks are other web pages in the same browser. Refuse requests that aren't addressed to
        // this machine by name (DNS rebinding), and WebSockets and changes that come from another site.
        app.Use(async (ctx, next) =>
        {
            if (loopbackOnly && !IsLoopbackHost(ctx.Request.Host.Host)) { ctx.Response.StatusCode = 403; return; }
            bool changes = !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method);
            if ((changes || ctx.WebSockets.IsWebSocketRequest) && ctx.Request.Headers.Origin.Count > 0
                && !SameOrigin(ctx.Request.Headers.Origin.ToString(), ctx.Request.Host))
            {
                ctx.Response.StatusCode = 403;
                return;
            }
            if (changes && ctx.Request.Path.StartsWithSegments("/api") && ctx.Request.Headers["X-Longhand"] != "1")
            {
                ctx.Response.StatusCode = 403; // a plain cross-site form can't set this header
                return;
            }
            await next();
        });

        app.Map("/ws", async ctx =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            // An open tab holds its connection until the tab goes away, so the server's own stop signal has to end it too, or
            // Ctrl+C waits out the whole shutdown grace period.
            var stopping = ctx.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
            using var end = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, stopping);
            await new WebConnection(socket, store, apps, dev).RunAsync(end.Token);
        });

        MapSettings(app, store);
        apps.Map(app);
        MapPage(app, options.WwwRoot);
        return app;
    }

    static bool IsLoopbackHost(string host) => host is "localhost" or "127.0.0.1" or "[::1]" or "::1";

    static bool IsLoopbackUrl(string url) => Uri.TryCreate(url.Replace("*", "0.0.0.0").Replace("+", "0.0.0.0"), UriKind.Absolute, out var u) && IsLoopbackHost(u.Host);

    static bool SameOrigin(string origin, HostString host) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var o) && string.Equals(o.Authority, host.ToString(), StringComparison.OrdinalIgnoreCase)
        || Uri.TryCreate(origin, UriKind.Absolute, out var p) && p.IsDefaultPort && string.Equals(p.Host, host.Host, StringComparison.OrdinalIgnoreCase) && !host.Port.HasValue;

    // ---------- the page ----------

    static void MapPage(WebApplication app, string? folder)
    {
        var assembly = Assembly.GetExecutingAssembly();
        app.MapGet("/{**path}", async (HttpContext ctx, string? path) =>
        {
            var name = string.IsNullOrEmpty(path) ? "index.html" : path;
            Stream? opened = null;
            if (!name.Contains(".."))
            {
                // In development the files come from disk, so an edit shows up without a rebuild.
                var onDisk = folder is null ? null : Path.Combine(folder, name);
                try { opened = onDisk is not null ? (File.Exists(onDisk) ? new FileStream(onDisk, FileMode.Open, FileAccess.Read, FileShare.ReadWrite) : null) : assembly.GetManifestResourceStream("wwwroot/" + name); }
                catch (IOException) { opened = null; }
            }
            if (opened is not { } stream)
            {
                ctx.Response.StatusCode = 404;
                return;
            }
            using (stream)
            {
                ctx.Response.ContentType = ContentTypes.GetValueOrDefault(Path.GetExtension(name), "application/octet-stream");
                ctx.Response.Headers.CacheControl = "no-store";
                await stream.CopyToAsync(ctx.Response.Body);
            }
        });
    }

    // ---------- settings and symbols ----------

    /// <summary>The version this server was built as (set with -p:Version=…; the project says 0.2.0 otherwise).</summary>
    public static string Version { get; } = typeof(WebApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";

    static void MapSettings(WebApplication app, WebStore store)
    {
        app.MapGet("/api/settings", () => new
        {
            hasKey = store.ApiKey is not null,
            keySource = store.KeySource,
            version = Version,
            model = store.Model,
            models = ModelChoice.All.Select(o => new { id = o.Id, label = o.Label }),
        });

        app.MapPut("/api/settings", (SettingsChange change) =>
        {
            if (change.Model is not null) store.Model = change.Model;
            if (change.RemoveKey == true) store.ClearKey();
            if (!string.IsNullOrWhiteSpace(change.ApiKey))
            {
                var key = change.ApiKey.Trim();
                if (key.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) key = key[7..].Trim();
                store.SaveKey(key);
            }
            return Results.Ok(new { hasKey = store.ApiKey is not null, keySource = store.KeySource, model = store.Model });
        });

        app.MapGet("/api/symbols", () => store.Symbols().Select(s => new { id = s.Id, name = s.Name, meaning = s.Meaning, hasDrawing = s.HasDrawing }));

        app.MapGet("/api/symbols/{id}.png", (string id) => store.Drawing(id) is { } png ? Results.File(png, "image/png") : Results.NotFound());

        app.MapPut("/api/symbols", (SymbolChange change) =>
        {
            byte[]? drawing = null;
            if (!string.IsNullOrEmpty(change.Drawing))
            {
                drawing = DecodePng(change.Drawing);
                if (drawing is null) return Results.BadRequest(new { error = "The drawing isn't a PNG picture." });
            }
            var problem = store.SaveSymbol(change.Id, change.Name ?? "", change.Meaning ?? "", drawing, change.RemoveDrawing == true);
            return problem is null ? Results.Ok() : Results.BadRequest(new { error = problem });
        });

        app.MapDelete("/api/symbols/{id}", (string id) =>
        {
            if (id.Any(c => !char.IsAsciiLetterOrDigit(c))) return Results.BadRequest();
            store.DeleteSymbol(id);
            return Results.Ok();
        });
    }

    /// <summary>A PNG sent as a data URL or plain base64, up to a megabyte. Null when it isn't one.</summary>
    static byte[]? DecodePng(string text)
    {
        var comma = text.IndexOf(',');
        var base64 = text.StartsWith("data:", StringComparison.Ordinal) && comma > 0 ? text[(comma + 1)..] : text;
        if (base64.Length > 1_400_000) return null;
        try
        {
            var bytes = Convert.FromBase64String(base64);
            bool png = bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
            return png ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    sealed record SettingsChange(string? Model, string? ApiKey, bool? RemoveKey);
    sealed record SymbolChange(string? Id, string? Name, string? Meaning, string? Drawing, bool? RemoveDrawing);
}
