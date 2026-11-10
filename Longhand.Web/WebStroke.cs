using System;
using System.Linq;
using System.Text.Json.Serialization;

namespace Longhand.Web;

/// <summary>A stroke as the browser drew it: points on the page, as [x, y] pairs.</summary>
public sealed class WebStroke
{
    /// <summary>Half the pen's width, so a stroke's extent matches what is seen.</summary>
    const double Half = 1.2;
    public const int MaxPoints = 20000;

    public uint Id { get; set; }
    public double[][] Pts { get; set; } = Array.Empty<double[]>();

    [JsonIgnore] public double Top => Pts.Min(p => p[1]) - Half;
    [JsonIgnore] public double Bottom => Pts.Max(p => p[1]) + Half;

    public void MoveDown(double dy)
    {
        foreach (var p in Pts) p[1] += dy;
    }

    /// <summary>False for anything a browser shouldn't send: no points, too many, or numbers that aren't finite.</summary>
    public bool IsValid() => Id != 0 && Pts.Length is > 0 and <= MaxPoints && Pts.All(p => p is { Length: 2 } && double.IsFinite(p[0]) && double.IsFinite(p[1]));

    public WebStroke Copy() => new() { Id = Id, Pts = Pts.Select(p => new[] { p[0], p[1] }).ToArray() };
}
