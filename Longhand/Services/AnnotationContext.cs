using System;
using System.Collections.Generic;
using System.Linq;

namespace Longhand.Services;

/// <summary>
/// Decides which earlier replies Claude should be shown when the user writes on top of them. Heights only: the page is one
/// vertical strip, so everything is a (top, bottom) pair.
/// </summary>
public static class AnnotationContext
{
    /// <summary>The replies to show (indexes into the input) and the stretch of page that covers them and the writing they answered.</summary>
    public readonly record struct Band(IReadOnlyList<int> Replies, double From, double To);

    /// <param name="replies">Every finished reply on the page as (top, bottom), ordered from the top of the page.</param>
    /// <returns>Null when the new ink doesn't touch any reply, as when it simply follows the last one.</returns>
    public static Band? Find(double inkTop, double inkBottom, IReadOnlyList<(double Top, double Bottom)> replies, double pageTop)
    {
        var hit = Enumerable.Range(0, replies.Count)
            .Where(i => replies[i].Top < inkBottom && replies[i].Bottom > inkTop)
            .ToList();
        if (hit.Count == 0) return null;

        double from = inkTop, to = inkBottom;
        foreach (var i in hit)
        {
            // A reply answers what was written between the reply above it and itself, so include that too.
            from = Math.Min(from, i > 0 ? replies[i - 1].Bottom : pageTop);
            to = Math.Max(to, replies[i].Bottom);
        }
        return new Band(hit, from, to);
    }
}
