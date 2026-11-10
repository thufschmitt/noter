using System;

namespace Longhand.Services;

/// <summary>Resizes the coverage (alpha) of a rendered bitmap to an exact size, whatever size the renderer actually produced.</summary>
public static class AlphaMask
{
    /// <summary>
    /// Takes premultiplied BGRA pixels and returns <paramref name="targetWidth"/> × <paramref name="targetHeight"/> alpha values,
    /// each the average over the part of the source it covers.
    /// </summary>
    public static byte[] Resize(byte[] bgra, int width, int height, int targetWidth, int targetHeight)
    {
        var result = new byte[Math.Max(0, targetWidth) * Math.Max(0, targetHeight)];
        if (width <= 0 || height <= 0 || targetWidth <= 0 || targetHeight <= 0) return result;

        double stepX = (double)width / targetWidth, stepY = (double)height / targetHeight;
        for (int ty = 0; ty < targetHeight; ty++)
        {
            double y0 = ty * stepY, y1 = y0 + stepY;
            for (int tx = 0; tx < targetWidth; tx++)
            {
                double x0 = tx * stepX, x1 = x0 + stepX;
                double sum = 0, weight = 0;
                for (int sy = (int)Math.Floor(y0); sy < Math.Min(height, (int)Math.Ceiling(y1)); sy++)
                {
                    double wy = Math.Min(y1, sy + 1) - Math.Max(y0, sy);
                    for (int sx = (int)Math.Floor(x0); sx < Math.Min(width, (int)Math.Ceiling(x1)); sx++)
                    {
                        double w = wy * (Math.Min(x1, sx + 1) - Math.Max(x0, sx));
                        sum += bgra[(sy * width + sx) * 4 + 3] * w;
                        weight += w;
                    }
                }
                result[ty * targetWidth + tx] = weight == 0 ? (byte)0 : (byte)Math.Round(sum / weight);
            }
        }
        return result;
    }
}
