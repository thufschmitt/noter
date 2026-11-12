using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;

namespace Longhand.Services;

/// <summary>What a layout is built with: the page's ink and paper (they follow the theme), whether it is dark (for the palette), and the handwriting font.</summary>
public sealed record LayoutStyle(Color Ink, bool Dark, FontFamily Font, Color Paper);

/// <summary>A layout built as native controls, with the maps that editing needs: which control is each part, and which grid is each timeline.</summary>
public sealed class BuiltLayout
{
    public FrameworkElement Root = null!;
    public LNode Tree = null!;
    /// <summary>The outermost control of every element of the layout (a bordered drop area is its border).</summary>
    public readonly Dictionary<LNode, FrameworkElement> Elements = new();
    /// <summary>The grid of hours of every timeline column.</summary>
    public readonly Dictionary<LNode, TimelinePanel> Grids = new();
    /// <summary>Boxes of a timeline, which are filled with the paper so the hour lines don't show through; the picture sent to the model leaves the fill out.</summary>
    public readonly List<Border> Opaque = new();
}

/// <summary>
/// The layout language (<see cref="Layout"/>) built as native controls: <c>row</c> a Grid, <c>col</c> a StackPanel, <c>box</c> a Border,
/// <c>text</c> a TextBlock, a timeline column a <see cref="TimelinePanel"/>. XAML lays it out and measures the words, so there is
/// no picture of it: every part is a control that can be found, measured and moved. The same language the web draws as HTML and CSS.
/// </summary>
public static class LayoutXaml
{
    public static BuiltLayout? Build(string markup, LayoutStyle style)
    {
        var tree = Layout.Parse(markup);
        if (tree is null) return null;
        var built = new BuiltLayout { Tree = tree };
        built.Root = Node(tree, style.Ink, style, built, compact: false);
        return built;
    }

    /// <summary>One part of a layout built on its own, as it looks where it is (inheriting the colour of what it is in); the drag preview.</summary>
    public static FrameworkElement BuildPart(LNode node, LayoutStyle style)
    {
        var chain = new List<LNode>();
        for (var up = node.Parent; up is not null; up = up.Parent) chain.Add(up);
        var inherited = style.Ink;
        for (int i = chain.Count - 1; i >= 0; i--) inherited = ColorOf(chain[i], inherited, style);
        return Node(node, inherited, style, new BuiltLayout { Tree = node }, compact: false);
    }

    // ----- colours -----

    static Color Rgb((byte R, byte G, byte B) c, double alpha = 1) => Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), c.R, c.G, c.B);

    static Color WithAlpha(Color c, double alpha) => Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), c.R, c.G, c.B);

    static Color ColorOf(LNode n, Color inherited, LayoutStyle style) =>
        !n.IsText && Layout.ResolveColor(n.Attrs.GetValueOrDefault("color"), style.Dark) is { } c ? Rgb(c) : inherited;

    /// <summary>A soft tint for <c>bg</c>, about a fifth of the colour over what is behind (as on the web).</summary>
    static Brush? Tint(LNode n, LayoutStyle style) =>
        Layout.ResolveColor(n.Attrs.GetValueOrDefault("bg"), style.Dark) is { } c ? new SolidColorBrush(Rgb(c, 0.22)) : null;

    static SolidColorBrush Brush(Color c) => new(c);

    // ----- the parts -----

    static FrameworkElement Node(LNode n, Color inherited, LayoutStyle style, BuiltLayout built, bool compact)
    {
        if (n.IsText) return Words(n.Text, inherited, style, compact);
        var color = ColorOf(n, inherited, style);
        FrameworkElement element = n.Tag switch
        {
            "layout" => Root(n, color, style, built),
            "row" => Row(n, color, style, built),
            "col" => Layout.IsTimeline(n) ? Timeline(n, color, style, built) : Column(n, color, style, built),
            "box" => Box(n, color, style, built),
            "text" => TextPart(n, color, style, built),
            "rule" => Rule(color),
            _ => new Border(),
        };
        if (n.Rot != 0)
        {
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            element.RenderTransform = new RotateTransform { Angle = n.Rot };
        }
        built.Elements[n] = element;
        return element;
    }

    static TextBlock Words(string text, Color color, LayoutStyle style, bool compact) => new()
    {
        Text = text,
        FontFamily = style.Font,
        FontSize = compact ? 18 : 24,
        LineHeight = compact ? 20 : 32,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush(color),
        IsTextScaleFactorEnabled = false,
        IsHitTestVisible = false,
    };

    static TextBlock Title(string text, Color color, LayoutStyle style, double size, double below) => new()
    {
        Text = text,
        FontFamily = style.Font,
        FontSize = size,
        LineHeight = Math.Round(size * 32 / 24),
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        FontWeight = Windows.UI.Text.FontWeights.Bold,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush(color),
        Margin = new Thickness(0, 0, 0, below),
        IsTextScaleFactorEnabled = false,
        IsHitTestVisible = false,
    };

    static FrameworkElement Root(LNode n, Color color, LayoutStyle style, BuiltLayout built)
    {
        var stack = new StackPanel();
        if (n.Attrs.TryGetValue("title", out var title)) stack.Children.Add(Title(title, color, style, 26.4, 8));
        foreach (var k in n.Kids) stack.Children.Add(Node(k, color, style, built, compact: false));
        return new Border { Padding = new Thickness(3), Child = stack, Background = Tint(n, style) };
    }

    /// <summary>A row or a column, or a timeline: a bordered, dashed-edge area when it accepts drops, with a minimum height so an empty one can be dropped on.</summary>
    static FrameworkElement Container(LNode n, Panel body, Color color, LayoutStyle style)
    {
        var tint = Tint(n, style);
        if (!n.Has("drop"))
        {
            if (tint is not null) body.Background = tint;
            return body;
        }
        return new Border
        {
            BorderBrush = Brush(WithAlpha(color, 0.45)),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8),
            MinHeight = 56,
            Background = tint,
            Child = body,
        };
    }

    static FrameworkElement Row(LNode n, Color color, LayoutStyle style, BuiltLayout built)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        int column = 0;
        foreach (var k in n.Kids)
        {
            var child = Node(k, color, style, built, compact: false);
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = !k.IsText && k.Has("fit") ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(child, column++);
            grid.Children.Add(child);
        }
        if (!n.Attrs.TryGetValue("title", out var title)) return Container(n, grid, color, style);
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(Title(title, color, style, 24, 0));
        stack.Children.Add(grid);
        return Container(n, stack, color, style);
    }

    static FrameworkElement Column(LNode n, Color color, LayoutStyle style, BuiltLayout built)
    {
        var stack = new StackPanel { Spacing = 8 };
        if (n.Attrs.TryGetValue("title", out var title)) stack.Children.Add(Title(title, color, style, 24, 0));
        foreach (var k in n.Kids) stack.Children.Add(Node(k, color, style, built, compact: false));
        return Container(n, stack, color, style);
    }

    static FrameworkElement Box(LNode n, Color color, LayoutStyle style, BuiltLayout built)
    {
        // An item of a timeline is a compact box: smaller words, a thinner border, clipped to the time it lasts.
        bool compact = n.Parent is { } parent && Layout.IsTimeline(parent) && Layout.TimesOf(n) is not null;
        var content = new StackPanel { Spacing = compact ? 0 : 6 };
        if (n.Attrs.TryGetValue("title", out var title)) content.Children.Add(Title(title, color, style, compact ? 18 : 24, 0));
        foreach (var k in n.Kids) content.Children.Add(Node(k, color, style, built, compact));
        var tint = Tint(n, style);

        if (n.Has("drop") && !compact)
        {
            // A box that takes drops has a dashed edge, which a Border can't draw.
            content.Margin = new Thickness(12, 4, 12, 4);
            var grid = new Grid { MinHeight = 56 };
            grid.Children.Add(new Rectangle { Stroke = Brush(color), StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 4, 3 }, RadiusX = 8, RadiusY = 8, Fill = tint, IsHitTestVisible = false });
            grid.Children.Add(content);
            return grid;
        }

        var border = new Border
        {
            BorderBrush = Brush(color),
            BorderThickness = new Thickness(compact ? 1.5 : 2),
            CornerRadius = new CornerRadius(compact ? 6 : 8),
            Padding = compact ? new Thickness(6, 0, 6, 0) : new Thickness(12, 4, 12, 4),
            Child = content,
            Background = tint,
        };
        if (compact)
        {
            if (tint is null)
            {
                border.Background = Brush(style.Paper);
                built.Opaque.Add(border);
            }
            border.SizeChanged += (_, e) => border.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
        }
        return border;
    }

    static FrameworkElement TextPart(LNode n, Color color, LayoutStyle style, BuiltLayout built)
    {
        var stack = new StackPanel();
        foreach (var k in n.Kids) stack.Children.Add(Node(k, color, style, built, compact: false));
        if (Tint(n, style) is { } tint) stack.Background = tint;
        return stack;
    }

    static FrameworkElement Rule(Color color) => new Border
    {
        BorderBrush = Brush(color),
        BorderThickness = new Thickness(0, 1.5, 0, 0),
        Opacity = 0.6,
        Margin = new Thickness(0, 6, 0, 6),
        IsHitTestVisible = false,
    };

    /// <summary>
    /// A timeline column: its title, what has no time, then the hours with each timed item at its start, as tall as it is long and side
    /// by side with the items it overlaps. The first of a row of days also writes the hours, in a gutter on its left.
    /// </summary>
    static FrameworkElement Timeline(LNode col, Color color, LayoutStyle style, BuiltLayout built)
    {
        var (from, to) = Layout.RangeOf(col);
        bool axis = col.Parent is { } parent && parent.Elements.First(Layout.IsTimeline) == col;
        var panel = new TimelinePanel(from, to, axis ? 52 : 0);

        for (int minute = (from + 29) / 30 * 30; minute <= to; minute += 30)
            panel.AddLine(new Rectangle { Height = 1, Fill = Brush(WithAlpha(color, minute % 60 == 0 ? 0.30 : 0.12)), IsHitTestVisible = false }, minute);
        if (axis)
            for (int hour = (from + 59) / 60 * 60; hour <= to; hour += 60)
                panel.AddLabel(new TextBlock { Text = Layout.Hhmm(hour), FontFamily = style.Font, FontSize = 13, Opacity = 0.6, TextAlignment = TextAlignment.Right, Foreground = Brush(color), IsHitTestVisible = false }, hour);

        var timed = col.Elements.Select(k => (Item: k, Times: Layout.TimesOf(k))).Where(x => x.Times is not null).Select(x => (x.Item, x.Times!.Value.Start, x.Times.Value.End)).ToList();
        var lanes = Layout.LanesOf(timed);
        foreach (var (item, start, end) in timed)
        {
            var (lane, count) = lanes[item];
            panel.AddItem(Node(item, color, style, built, compact: false), start, end, lane, count);
        }

        var stack = new StackPanel { Spacing = 8 };
        if (col.Attrs.TryGetValue("title", out var title)) stack.Children.Add(Title(title, color, style, 24, 0));
        foreach (var k in col.Kids.Where(k => Layout.TimesOf(k) is null)) stack.Children.Add(Node(k, color, style, built, compact: false));
        stack.Children.Add(panel);
        built.Grids[col] = panel;
        return Container(col, stack, color, style);
    }
}

/// <summary>
/// The hours of a timeline column: horizontal lines at the half hours, labels in a gutter on the left (for the first day of a row), and
/// items placed by their time, the items that overlap sharing the width. The arithmetic is <see cref="LayoutHit.TimelineSlot"/>.
/// </summary>
public sealed class TimelinePanel : Panel
{
    sealed class Slot
    {
        public required UIElement Element;
        public int Start, End, Lane, Lanes;
    }

    readonly List<Slot> _slots = new();
    readonly List<(UIElement Element, int Minute)> _lines = new(), _labels = new();

    public TimelinePanel(int fromMinutes, int toMinutes, double gutter)
    {
        FromMinutes = fromMinutes;
        ToMinutes = toMinutes;
        Gutter = gutter;
    }

    /// <summary>The time at the top of the grid, in minutes since midnight.</summary>
    public int FromMinutes { get; }
    public int ToMinutes { get; }
    /// <summary>The room on the left for the hours' labels; the grid starts after it.</summary>
    public double Gutter { get; }

    public void AddLine(UIElement line, int minute)
    {
        _lines.Add((line, minute));
        Children.Add(line);
    }

    public void AddLabel(UIElement label, int minute)
    {
        _labels.Add((label, minute));
        Children.Add(label);
    }

    public void AddItem(UIElement element, int start, int end, int lane, int lanes)
    {
        _slots.Add(new Slot { Element = element, Start = start, End = end, Lane = lane, Lanes = lanes });
        Children.Add(element);
    }

    /// <summary>Moves the end of an item (while its end is being dragged); it keeps its start and its lane.</summary>
    public void SetEnd(UIElement element, int end)
    {
        foreach (var slot in _slots)
            if (slot.Element == element)
            {
                slot.End = Math.Max(slot.Start + Layout.SnapMinutes, Math.Min(Layout.DayMinutes, end));
                InvalidateMeasure();
            }
    }

    double GridWidth(double width) => Math.Max(0, width - Gutter);

    protected override Size MeasureOverride(Size available)
    {
        double width = double.IsInfinity(available.Width) ? 300 : available.Width, grid = GridWidth(width);
        foreach (var slot in _slots)
        {
            var r = LayoutHit.TimelineSlot(slot.Start, slot.End, slot.Lane, slot.Lanes, grid, FromMinutes);
            slot.Element.Measure(new Size(r.Width, r.Height));
        }
        foreach (var (line, _) in _lines) line.Measure(new Size(grid, 1));
        foreach (var (label, _) in _labels) label.Measure(new Size(Math.Max(0, Gutter - 6), 16));
        return new Size(width, LayoutHit.TimelineY(ToMinutes, FromMinutes));
    }

    protected override Size ArrangeOverride(Size final)
    {
        double grid = GridWidth(final.Width);
        foreach (var (line, minute) in _lines) line.Arrange(new Rect(Gutter, LayoutHit.TimelineY(minute, FromMinutes), grid, 1));
        foreach (var (label, minute) in _labels) label.Arrange(new Rect(0, LayoutHit.TimelineY(minute, FromMinutes) - 8, Math.Max(0, Gutter - 6), 16));
        foreach (var slot in _slots)
        {
            var r = LayoutHit.TimelineSlot(slot.Start, slot.End, slot.Lane, slot.Lanes, grid, FromMinutes);
            slot.Element.Arrange(new Rect(Gutter + r.Left, r.Top, Math.Max(0, r.Width), Math.Max(0, r.Height)));
        }
        return final;
    }
}
