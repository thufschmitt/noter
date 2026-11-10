using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.Input.Inking;

namespace Longhand.Services;

/// <summary>Renders a set of strokes as black ink on a white PNG, for Claude to read.</summary>
public static class InkSnapshot
{
    const int Pad = 32;

    public static async Task<byte[]> RenderPngAsync(IReadOnlyList<InkStroke> strokes)
    {
        // Copy the strokes into a scratch container, in black whatever the current theme's ink colour is.
        var container = new InkStrokeContainer();
        foreach (var stroke in strokes)
        {
            var copy = stroke.Clone();
            var attrs = copy.DrawingAttributes;
            attrs.Color = Color.FromArgb(255, 0, 0, 0);
            copy.DrawingAttributes = attrs;
            container.AddStroke(copy);
        }

        // Windows Ink renders the strokes itself when saving as GIF (cropped to the ink, transparent background).
        using var gif = new InMemoryRandomAccessStream();
        await container.SaveAsync(gif, InkPersistenceFormat.GifWithEmbeddedIsf);
        gif.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(gif);

        // Enlarge small writing so it reads clearly; keep the long side within ~1400 px.
        uint w = decoder.PixelWidth, h = decoder.PixelHeight;
        double k = Math.Clamp(1400.0 / Math.Max(1, Math.Max(w, h)), 1.0, 2.5);
        uint sw = (uint)Math.Max(1, Math.Round(w * k)), sh = (uint)Math.Max(1, Math.Round(h * k));
        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
            new BitmapTransform { ScaledWidth = sw, ScaledHeight = sh, InterpolationMode = BitmapInterpolationMode.Fant },
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        byte[] src = pixels.DetachPixelData();

        // Composite onto white with a margin.
        uint ow = sw + 2 * Pad, oh = sh + 2 * Pad;
        var dst = new byte[ow * oh * 4];
        Array.Fill(dst, (byte)255);
        for (uint y = 0; y < sh; y++)
        {
            for (uint x = 0; x < sw; x++)
            {
                long i = (y * sw + x) * 4, j = ((y + Pad) * ow + x + Pad) * 4;
                double a = src[i + 3] / 255.0;
                for (int c = 0; c < 3; c++) dst[j + c] = (byte)Math.Round(src[i + c] * a + 255 * (1 - a));
            }
        }

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
}
