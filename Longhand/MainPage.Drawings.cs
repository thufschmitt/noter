using System;
using System.Linq;
using System.Threading.Tasks;
using Longhand.Services;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Longhand;

/// <summary>
/// The layouts and drawings of Claude's on the page, built as native controls: a layout from the layout language
/// (<see cref="LayoutXaml"/>), a picture from its SVG read into a scene (<see cref="SvgXaml"/>). There are no pictures of them, so
/// XAML measures and draws them, and editing can find every part.
/// </summary>
public sealed partial class MainPage
{
    /// <summary>One block of a note as what is on the page: the control that sits in the note layer, and what it was built from.</summary>
    sealed class DrawingVisual
    {
        public required NoteModel Note;
        public required DrawingModel Drawing;
        public FrameworkElement Host = null!;
        /// <summary>Set for a layout: its parsed tree and the control of each part.</summary>
        public BuiltLayout? Built;
        /// <summary>Set for a picture.</summary>
        public SvgScene? Scene;
    }

    LayoutStyle CurrentLayoutStyle() => new(_claudeInk.Color, ActualTheme == ElementTheme.Dark, _handFont, _paper.Color);

    /// <summary>(Re)builds a block from its markup and puts it where the old one was. A block that can't be built says so, once, in words.</summary>
    void BuildVisual(DrawingVisual v)
    {
        var d = v.Drawing;
        var old = v.Host;
        v.Built = null;
        v.Scene = null;
        FrameworkElement? host = null;
        try
        {
            if (Blocks.IsLayout(d.Markup) && LayoutXaml.Build(d.Markup, CurrentLayoutStyle()) is { } built)
            {
                v.Built = built;
                host = built.Root;
            }
            else if (!Blocks.IsLayout(d.Markup) && SvgScene.Parse(d.Markup) is { } scene)
            {
                v.Scene = scene;
                host = SvgXaml.Build(scene, _claudeInk.Color, _handFont);
            }
        }
        catch (Exception)
        {
            // Building is new code that can't be tried here: a block that can't be built is said so in words, never a crash.
            v.Built = null;
            v.Scene = null;
            host = null;
        }
        if (host is null)
        {
            host = new TextBlock { Text = "(a drawing that couldn't be shown)", FontFamily = _handFont, FontSize = 20, Foreground = _muted };
            ShowToast("A drawing or layout couldn't be shown.");
        }
        Windows.UI.Xaml.Automation.AutomationProperties.SetName(host, d.Alt);
        if (old is not null)
        {
            Canvas.SetLeft(host, Canvas.GetLeft(old));
            Canvas.SetTop(host, Canvas.GetTop(old));
            NoteLayer.Children.Remove(old);
        }
        v.Host = host;
        NoteLayer.Children.Add(host);
        SizeHost(v);
    }

    void RebuildDrawings()
    {
        foreach (var n in _session.Notes)
            foreach (var d in n.Drawings) BuildVisual(VisualOf(d));
        ScheduleFrameUpdate();
    }

    void RemoveDrawing(DrawingModel d)
    {
        var v = VisualOf(d);
        if (_selected == v) ClearSelection();
        NoteLayer.Children.Remove(v.Host);
    }

    /// <summary>Sizes a block's control for the page's text width and returns how tall it is. A layout is as wide as the text and as tall as its content; a picture keeps its shape.</summary>
    double SizeHost(DrawingVisual v)
    {
        double height;
        if (v.Built is not null)
        {
            v.Host.Width = TextWidth;
            v.Host.Measure(new Size(TextWidth, double.PositiveInfinity));
            height = v.Host.DesiredSize.Height;
            v.Drawing.Aspect = TextWidth / Math.Max(1, height);
        }
        else if (v.Scene is { } scene)
        {
            var (w, h) = SvgXaml.DisplaySize(scene, TextWidth);
            v.Host.Width = w;
            v.Host.Height = h;
            height = h;
            v.Drawing.Aspect = w / Math.Max(1, h);
        }
        else
        {
            v.Host.Measure(new Size(TextWidth, double.PositiveInfinity));
            height = v.Host.DesiredSize.Height;
        }
        return height;
    }

    /// <summary>How many rule lines a block takes at the current width.</summary>
    int SizeDrawing(DrawingVisual v) => Math.Max(1, (int)Math.Ceiling(SizeHost(v) / LineHeight));

    /// <summary>
    /// A block as pixels for the picture sent to the model. The fills that only keep a timeline's hour lines from showing through its
    /// items are left out: the page paints a reply by its coverage in one blue, so they would turn whole items solid.
    /// </summary>
    static async Task<RenderedText> RenderDrawingAsync(DrawingVisual v, double scale)
    {
        var fills = v.Built?.Opaque.Select(b => (Border: b, b.Background)).ToList();
        if (fills is not null) foreach (var (border, _) in fills) border.Background = null;
        try
        {
            return await RenderElementAsync(v.Host, scale);
        }
        finally
        {
            if (fills is not null) foreach (var (border, background) in fills) border.Background = background;
        }
    }
}
