using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Longhand.Services;

/// <summary>A block rendered to a picture: a PNG at twice its size, and its size in page units.</summary>
public sealed record RenderedBlock(byte[] Png, double Width, double Height);

/// <summary>
/// Renders Claude's layouts and SVG drawings to pictures, with one hidden WebView2 that runs no scripts.
/// Drawings are static here, so a picture shows them fully, costs no more than a drawing did, and goes into the snapshot Claude reads
/// like anything else on the page. Through the DevTools protocol it lays the block out at the page's text width, reads how tall
/// it is, and captures all of it on a clear background.
/// </summary>
public sealed class BlockRenderer
{
    const double Scale = 2;
    static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(10);

    readonly Panel _host;
    readonly SemaphoreSlim _one = new(1, 1);
    WebView2? _view;

    /// <param name="host">A panel in the page where the renderer's (invisible) view can live: it only draws while it is in the tree.</param>
    public BlockRenderer(Panel host) => _host = host;

    /// <summary>The block at the given width, in the given ink colour (a CSS colour); null when it couldn't be rendered.</summary>
    public async Task<RenderedBlock?> RenderAsync(string markup, double width, string ink)
    {
        await _one.WaitAsync();
        try
        {
            var core = await CoreAsync();
            await Cdp(core, "Emulation.setDeviceMetricsOverride", new { width = (int)Math.Ceiling(width), height = 1, deviceScaleFactor = Scale, mobile = false });

            var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Done(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs e) => loaded.TrySetResult(e.IsSuccess);
            core.NavigationCompleted += Done;
            try
            {
                core.NavigateToString(Blocks.Document(markup).Replace(Blocks.InkToken, ink));
                if (await Task.WhenAny(loaded.Task, Task.Delay(LoadTimeout)) != loaded.Task || !loaded.Task.Result) return null;
            }
            finally
            {
                core.NavigationCompleted -= Done;
            }

            using var metrics = JsonDocument.Parse(await Cdp(core, "Page.getLayoutMetrics", new { }));
            double height = metrics.RootElement.TryGetProperty("cssContentSize", out var size)
                ? size.GetProperty("height").GetDouble()
                : metrics.RootElement.GetProperty("contentSize").GetProperty("height").GetDouble() / Scale;
            height = Math.Max(1, Math.Ceiling(height));

            using var shot = JsonDocument.Parse(await Cdp(core, "Page.captureScreenshot", new
            {
                format = "png",
                captureBeyondViewport = true,
                fromSurface = true,
                clip = new { x = 0, y = 0, width = Math.Ceiling(width), height, scale = 1 },
            }));
            return new RenderedBlock(Convert.FromBase64String(shot.RootElement.GetProperty("data").GetString()!), Math.Ceiling(width), height);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            _one.Release();
        }
    }

    async Task<CoreWebView2> CoreAsync()
    {
        if (_view?.CoreWebView2 is { } ready) return ready;
        _view = new WebView2 { Width = 8, Height = 8, Opacity = 0, IsHitTestVisible = false, IsTabStop = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        // WinUI 2's styles, for this one control only: the rest of the app keeps its own look.
        _view.Resources.MergedDictionaries.Add(new XamlControlsResources());
        _host.Children.Insert(0, _view);
        await _view.EnsureCoreWebView2Async();
        var core = _view.CoreWebView2;
        // Drawings are static: nothing in them runs, and the document's own policy lets nothing load.
        core.Settings.IsScriptEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        await Cdp(core, "Emulation.setDefaultBackgroundColorOverride", new { color = new { r = 0, g = 0, b = 0, a = 0 } });
        return core;
    }

    static async Task<string> Cdp(CoreWebView2 core, string method, object parameters) =>
        await core.CallDevToolsProtocolMethodAsync(method, JsonSerializer.Serialize(parameters));
}
