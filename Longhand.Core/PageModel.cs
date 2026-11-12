using System;
using System.Collections.Generic;
using System.Linq;

namespace Longhand.Services;

/// <summary>The page's geometry, in pixels. The same on every platform, so a page looks alike wherever it is opened.</summary>
public static class PageLayout
{
    public const double PageMaxWidth = 880, LineHeight = 40, Top = 120, MarginX = 76, RightPad = 36, FontPx = 26;
    /// <summary>Nudges Claude's text so its baseline sits on the ruled line.</summary>
    public const double TextNudge = 3;
    /// <summary>The longest note that can be typed.</summary>
    public const int MaxTyped = 4000;

    public const string HintText = "Write anything with your pen: a question, a sum, a sketch. Then tap Ask, and I'll reply right here on the page.";

    /// <summary>The first ruled line at or below <paramref name="y"/>.</summary>
    public static double RuleAtOrBelow(double y) => Top + Math.Max(0, Math.Ceiling((y - Top) / LineHeight)) * LineHeight;
}

/// <summary>What the user did to one part of a saved layout that the model hasn't heard about yet: where it was before the first change.</summary>
/// <param name="Drawing">Index into the note's drawings.</param>
public sealed record SavedEdit(int Drawing, string Id, string? Parent, int Position, double Rot, bool Deleted, string? Label = null);

/// <summary>A note the model wrote on the page, as saved to disk.</summary>
public sealed record SavedNote(double Y, string Text, string Heard, bool IsHint, List<string>? Drawings = null, bool Typed = false, List<SavedEdit>? Edits = null);

/// <summary>Where a stroke sits on the page. The page only ever needs heights: it is one vertical strip.</summary>
public readonly record struct StrokeSpan(uint Id, double Top, double Bottom);

/// <summary>
/// One of the drawings shown under the words of Claude's note: sanitized SVG. <c>View</c> is the platform's picture of it.
/// </summary>
public sealed class DrawingModel
{
    /// <summary>The block (a <see cref="Layout"/> or an <see cref="Svg"/>), from <see cref="Blocks.Sanitize"/>, with the user's edits made in it.</summary>
    public required string Markup;
    /// <summary>Width over height when laid out at the page's text width. Set by the platform when it measures the drawing.</summary>
    public double Aspect = 4.0 / 3.0;
    public string Alt = "";
    /// <summary>The rule lines it takes up. Set by the platform when it measures the note.</summary>
    public int Lines;
    public object? View;
    /// <summary>Where each part the user changed was before the first change, since the model last heard, by part id.</summary>
    public Dictionary<string, Layout.EditEntry> Edits = new();
    public bool Editable => Blocks.IsEditable(Markup);
}

/// <summary>One of Claude's notes on the page (or the hint). <c>View</c> is whatever the platform draws it with.</summary>
public sealed class NoteModel
{
    public double Y;
    public bool IsHint;
    /// <summary>Typed by the user with the keyboard (their own words, not a reply of Claude's).</summary>
    public bool IsTyped;
    public bool Done;
    public string Heard = "";
    /// <summary>The words of the reply: what is saved and what Claude is told in later turns.</summary>
    public string Text = "";
    /// <summary>What is on the page right now. While Claude is still writing this can be a stand-in such as "…" or a tool's status.</summary>
    public string Shown = "";
    /// <summary>Set by the platform when it measures the note.</summary>
    public int Lines = 1;
    public readonly List<DrawingModel> Drawings = new();
    /// <summary>How many of the reply's drawings have been taken in (shown or refused).</summary>
    public int DrawingsSeen;
    public object? View;

    public int DrawLines => Drawings.Sum(d => d.Lines);
    /// <summary>Its text lines and drawings plus one blank rule below as breathing room.</summary>
    public double Span => (Lines + DrawLines + 1) * PageLayout.LineHeight;
    public double Bottom => Y + Span;
    /// <summary>The rule lines its words and drawings cover, without the breathing room.</summary>
    public double Covered => (Lines + DrawLines) * PageLayout.LineHeight;
    public bool HasContent => Text.Length > 0 || Drawings.Count > 0;
}

/// <summary>What Claude is shown of the page around new ink that was written over earlier replies.</summary>
public sealed record SnapshotContext(IReadOnlyList<uint> EarlierInk, IReadOnlyList<NoteModel> Replies);

/// <summary>What the Ask button needs to know.</summary>
public readonly record struct PageState(bool Busy, bool CanAsk);
