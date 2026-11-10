using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.Input.Inking;

namespace Longhand.Services;

/// <summary>A reply drawn by the page (not ink), as the pixels it was rendered to: premultiplied BGRA.</summary>
public sealed record RenderedText(byte[] Bgra, int Width, int Height);

/// <summary>A reply Claude wrote earlier. <c>Render</c> draws it at the given scale (the page draws it, since it is a XAML text block).</summary>
public sealed record PageReply(Rect Bounds, Func<double, Task<RenderedText>> Render);

/// <summary>What was already on the page around new ink: the writing from before, and Claude's replies.</summary>
public sealed record PageContext(IReadOnlyList<InkStroke> EarlierInk, IReadOnlyList<PageReply> Replies);

/// <summary>
/// Renders new strokes as black ink on a white PNG, for Claude to read. With a context the picture also shows what the ink was
/// written over: earlier writing in grey and Claude's replies in blue, so black marks on top read as annotations.
/// </summary>
public static class InkSnapshot
{
    const int Pad = 32;
    const double MaxSide = 7000; // the API refuses larger images

    static readonly Color Black = Color.FromArgb(255, 0, 0, 0);
    static readonly Color Grey = Color.FromArgb(255, 140, 144, 152);
    static readonly byte[] ReplyBlue = { 0xC4, 0x4F, 0x2C }; // B, G, R of the blue Claude writes in

    public static async Task<byte[]> RenderPngAsync(IReadOnlyList<InkStroke> strokes, PageContext? context = null)
    {
        // Copy the strokes into a scratch container, in fixed colours whatever the current theme's ink colour is.
        // Earlier writing goes in first so the new ink is drawn over it.
        var container = new InkStrokeContainer();
        var earlier = context?.EarlierInk ?? Array.Empty<InkStroke>();
        foreach (var stroke in earlier) container.AddStroke(Recolored(stroke, Grey));
        foreach (var stroke in strokes) container.AddStroke(Recolored(stroke, Black));

        // The stretch of page the picture covers, in page units: all the ink, plus the replies.
        Rect area = Union(earlier.Concat(strokes).Select(s => s.BoundingRect));
        Rect inkArea = area;
        foreach (var reply in context?.Replies ?? Array.Empty<PageReply>()) area = Union(new[] { area, reply.Bounds });

        // Enlarge small writing so it reads clearly; keep the long side within the API's limit.
        double longSide = Math.Max(1, Math.Max(area.Width, area.Height));
        double k = Math.Min(Math.Clamp(1400.0 / longSide, 1.0, 2.5), MaxSide / longSide);

        // Windows Ink renders the strokes itself when saving as GIF (cropped to the ink, transparent background).
        using var gif = new InMemoryRandomAccessStream();
        await container.SaveAsync(gif, InkPersistenceFormat.GifWithEmbeddedIsf);
        gif.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(gif);
        uint sw = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * k)), sh = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * k));
        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
            new BitmapTransform { ScaledWidth = sw, ScaledHeight = sh, InterpolationMode = BitmapInterpolationMode.Fant },
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        byte[] ink = pixels.DetachPixelData();

        // The ink picture starts where the ink does, give or take the pen's width; the rest of the canvas is the replies' area.
        uint inkX = (uint)Math.Max(0, Math.Round((inkArea.X - area.X) * k)), inkY = (uint)Math.Max(0, Math.Round((inkArea.Y - area.Y) * k));
        uint ow = Math.Max((uint)Math.Ceiling(area.Width * k), inkX + sw) + 2 * Pad, oh = Math.Max((uint)Math.Ceiling(area.Height * k), inkY + sh) + 2 * Pad;
        var dst = new byte[ow * oh * 4];
        Array.Fill(dst, (byte)255);

        foreach (var reply in context?.Replies ?? Array.Empty<PageReply>())
        {
            var text = await reply.Render(k);
            // The renderer may not honour the size asked for (on a scaled display it can return a larger bitmap), so fit the
            // reply to the size it should have on the page; that is what keeps it lined up with the ink.
            int tw = (int)Math.Round(reply.Bounds.Width * k), th = (int)Math.Round(reply.Bounds.Height * k);
            var coverage = AlphaMask.Resize(text.Bgra, text.Width, text.Height, tw, th);
            DrawText(dst, ow, oh, coverage, tw, th, (long)Math.Round((reply.Bounds.X - area.X) * k) + Pad, (long)Math.Round((reply.Bounds.Y - area.Y) * k) + Pad);
        }
        DrawInk(dst, ow, ink, sw, sh, inkX + Pad, inkY + Pad);

        using var png = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, png);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, ow, oh, 96, 96, dst);
        await encoder.FlushAsync();

        var bytes = new byte[png.Size];
        using var reader = new DataReader(png.GetInputStreamAt(0));
        await reader.LoadAsync((uint)png.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    static InkStroke Recolored(InkStroke stroke, Color color)
    {
        var copy = stroke.Clone();
        var attrs = copy.DrawingAttributes;
        attrs.Color = color;
        copy.DrawingAttributes = attrs;
        return copy;
    }

    static Rect Union(IEnumerable<Rect> rects)
    {
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var r in rects)
        {
            left = Math.Min(left, r.X);
            top = Math.Min(top, r.Y);
            right = Math.Max(right, r.X + r.Width);
            bottom = Math.Max(bottom, r.Y + r.Height);
        }
        return new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    /// <summary>Paints a reply in blue using only its coverage, so the colour doesn't depend on the theme it was rendered in.</summary>
    static void DrawText(byte[] dst, uint dstWidth, uint dstHeight, byte[] coverage, int width, int height, long x0, long y0)
    {
        for (int y = 0; y < height; y++)
        {
            long dy = y0 + y;
            if (dy < 0 || dy >= dstHeight) continue;
            for (int x = 0; x < width; x++)
            {
                long dx = x0 + x;
                if (dx < 0 || dx >= dstWidth) continue;
                double a = coverage[y * width + x] / 255.0;
                if (a == 0) continue;
                long j = (dy * dstWidth + dx) * 4;
                for (int c = 0; c < 3; c++) dst[j + c] = (byte)Math.Round(ReplyBlue[c] * a + dst[j + c] * (1 - a));
            }
        }
    }

    static void DrawInk(byte[] dst, uint dstWidth, byte[] src, uint sw, uint sh, uint x0, uint y0)
    {
        for (uint y = 0; y < sh; y++)
        {
            for (uint x = 0; x < sw; x++)
            {
                long i = (y * sw + x) * 4, j = ((y + y0) * dstWidth + x + x0) * 4;
                double a = src[i + 3] / 255.0;
                for (int c = 0; c < 3; c++) dst[j + c] = (byte)Math.Round(src[i + c] * a + dst[j + c] * (1 - a));
            }
        }
    }
}
