using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Windows.Devices.Input;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Input;
using Windows.UI.Input.Inking;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;

namespace Longhand.Services;

/// <summary>
/// Writing, erasing and the lasso from plain pointer input, instead of from the ink presenter of an <c>InkCanvas</c>. The stroke
/// being written ("wet" ink) is drawn here, a little ahead of the pen with <see cref="StrokePredictor"/>, the way Windows Ink's
/// low-latency path draws it; when the pen lifts, the stroke is built from its samples and handed over. Nothing here needs more than
/// pointer events with their intermediate points, a timestamp and the pen's pressure and buttons, which every platform has.
/// <para>
/// The <see cref="InkStrokeContainer"/> the finished strokes go to is only where the page keeps them (and an <c>InkCanvas</c> with
/// its input off draws them); it takes no input itself.
/// </para>
/// </summary>
public sealed class PenInput
{
    /// <summary>How far ahead the wet ink is drawn, in seconds: about the time from a pen sample to the frame that shows it.</summary>
    public const double PredictionHorizon = 0.025;

    readonly FrameworkElement _surface;
    readonly UIElement _space;
    readonly Canvas _wet;
    readonly Func<InkStrokeContainer> _strokes;
    readonly StrokePredictor _predictor = new(PredictionHorizon);

    enum Mode { Ink, Erase, Lasso }

    sealed class Active
    {
        public required uint PointerId;
        public required Mode Mode;
        public required PointerDeviceType Device;
        public readonly List<InkPoint> Points = new();
        public readonly List<InkStroke> Erased = new();
        public SolidColorBrush? Brush;
        public double Size;
        public ulong LastTimestamp;
    }

    Active? _active;
    /// <summary>When a pen was last seen, touching or hovering: a touch then is the hand resting on the screen.</summary>
    DateTimeOffset _penSeen = DateTimeOffset.MinValue;
    static readonly TimeSpan PalmWindow = TimeSpan.FromSeconds(1);
    readonly Polyline _tail;
    int _clearAfterFrames;

    /// <param name="surface">What receives the pointer: it must be hit-testable (have a background) where writing is allowed.</param>
    /// <param name="space">The element whose coordinates the strokes and the lasso are in.</param>
    /// <param name="wet">Where the stroke being written is drawn: above the dry ink, not hit-testable, with the same coordinates as <paramref name="space"/>.</param>
    /// <param name="strokes">Where finished strokes go, and erased ones come from.</param>
    public PenInput(FrameworkElement surface, UIElement space, Canvas wet, Func<InkStrokeContainer> strokes)
    {
        _surface = surface;
        _space = space;
        _wet = wet;
        _strokes = strokes;
        _wet.IsHitTestVisible = false;
        _tail = new Polyline { StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false };
        surface.PointerPressed += OnPressed;
        surface.PointerMoved += OnMoved;
        surface.PointerReleased += OnReleased;
        surface.PointerCanceled += (_, e) => End(e, keep: false);
        // Losing the pointer mid-stroke (another element took it): a pen or mouse stroke is kept as far as it got; a finger's is
        // dropped, since it lost the pointer to a pinch or a scroll.
        surface.PointerCaptureLost += (_, e) => End(e, keep: _active?.Device != PointerDeviceType.Touch);
    }

    /// <summary>What new strokes are drawn with.</summary>
    public InkDrawingAttributes DrawingAttributes { get; set; } = new();

    /// <summary>Whether the pen erases whole strokes instead of writing (the eraser end of a pen always erases).</summary>
    public bool Erasing { get; set; }

    /// <summary>Whether a finger writes. Off, fingers are left to the scroller (scrolling, zooming).</summary>
    public bool FingersWrite
    {
        get => _fingersWrite;
        set
        {
            _fingersWrite = value;
            // In a ScrollViewer, a finger on an element that leaves manipulation to the system pans; one that doesn't, writes.
            _surface.ManipulationMode = value ? ManipulationModes.None : ManipulationModes.System;
        }
    }
    bool _fingersWrite;

    /// <summary>Off, a press starts nothing (the page turns it off while the pen is over something that takes hold, like a knob).</summary>
    public bool IsInputEnabled { get; set; } = true;

    /// <summary>A stroke was written. It is already in the container.</summary>
    public event Action<IReadOnlyList<InkStroke>>? StrokesCollected;
    /// <summary>Strokes were erased, by one pass of the eraser. They are no longer in the container.</summary>
    public event Action<IReadOnlyList<InkStroke>>? StrokesErased;
    /// <summary>The barrel button (or a mouse's right button) was pressed, and moved, and let go: a lasso, in <c>space</c>'s coordinates.</summary>
    public event Action<Point>? LassoPressed, LassoMoved;
    public event Action? LassoReleased, LassoLost;

    void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_active is not null || !IsInputEnabled) return;
        var device = e.Pointer.PointerDeviceType;
        if (device == PointerDeviceType.Pen) _penSeen = DateTimeOffset.UtcNow;
        // Palm rejection: fingers write only when no pen is about.
        if (device == PointerDeviceType.Touch && (!_fingersWrite || DateTimeOffset.UtcNow - _penSeen < PalmWindow)) return;
        var point = e.GetCurrentPoint(_space);
        var props = point.Properties;
        Mode mode;
        if (props.IsBarrelButtonPressed || props.IsRightButtonPressed) mode = Mode.Lasso;
        else if (device == PointerDeviceType.Mouse && !props.IsLeftButtonPressed) return;
        else if (props.IsEraser || Erasing) mode = Mode.Erase;
        else mode = Mode.Ink;

        if (!_surface.CapturePointer(e.Pointer)) return;
        e.Handled = true;
        ClearWet();
        var attrs = DrawingAttributes;
        _active = new Active { PointerId = e.Pointer.PointerId, Mode = mode, Device = device, Size = attrs.Size.Width, Brush = mode == Mode.Ink ? new SolidColorBrush(attrs.Color) : null };
        _predictor.Reset();
        if (mode == Mode.Lasso) LassoPressed?.Invoke(point.Position);
        Take(_active, new[] { point });
    }

    void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Pen) _penSeen = DateTimeOffset.UtcNow; // hovering counts
        if (_active is not { } a || e.Pointer.PointerId != a.PointerId) return;
        e.Handled = true;
        // Every sample since the last event, not only the latest: the system batches them, and they are the stroke's shape. They
        // come newest first.
        var points = e.GetIntermediatePoints(_space).Reverse().ToList();
        if (points.Count == 0) points.Add(e.GetCurrentPoint(_space));
        Take(a, points);
    }

    void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_active is not { } a || e.Pointer.PointerId != a.PointerId) return;
        Take(a, e.GetIntermediatePoints(_space).Reverse().ToList());
        End(e, keep: true);
    }

    /// <summary>Takes the new samples of the stroke under way: drawn, erased with, or added to the lasso.</summary>
    void Take(Active a, IEnumerable<PointerPoint> points)
    {
        int before = a.Points.Count;
        foreach (var p in points)
        {
            if (a.Points.Count > 0 && p.Timestamp <= a.LastTimestamp) continue; // already taken
            if (!p.IsInContact && a.Points.Count > 0) continue; // the pen lifting (no pressure left): not part of the stroke
            a.LastTimestamp = p.Timestamp;
            var props = p.Properties;
            float pressure = a.Device == PointerDeviceType.Pen ? props.Pressure : 0.5f;
            var q = new InkPoint(p.Position, pressure, props.XTilt, props.YTilt, p.Timestamp);
            a.Points.Add(q);
            switch (a.Mode)
            {
                case Mode.Ink:
                    _predictor.Add(new PenSample(p.Position.X, p.Position.Y, pressure, p.Timestamp / 1e6));
                    break;
                case Mode.Lasso:
                    if (a.Points.Count > 1) LassoMoved?.Invoke(p.Position);
                    break;
            }
        }
        if (a.Points.Count == before) return;
        if (a.Mode == Mode.Ink) DrawWet(a, before);
        else if (a.Mode == Mode.Erase) Erase(a, Math.Max(0, before - 1));
    }

    // ---------- wet ink ----------

    /// <summary>The width of the line at a pressure, close to how Windows Ink draws a pressure-sensitive stroke of that size.</summary>
    static double Width(double size, double pressure) => size * (0.25 + 1.5 * Math.Clamp(pressure, 0, 1));

    /// <summary>Draws the stroke's new segments, and the guess of where it goes next in place of the last guess.</summary>
    void DrawWet(Active a, int from)
    {
        _wet.Children.Remove(_tail);
        var pts = a.Points;
        if (pts.Count == 1)
        {
            double w = Width(a.Size, pts[0].Pressure);
            var dot = new Ellipse { Width = w, Height = w, Fill = a.Brush, IsHitTestVisible = false };
            Canvas.SetLeft(dot, pts[0].Position.X - w / 2);
            Canvas.SetTop(dot, pts[0].Position.Y - w / 2);
            _wet.Children.Add(dot);
        }
        for (int i = Math.Max(1, from); i < pts.Count; i++)
        {
            var p0 = pts[i - 1].Position;
            var p1 = pts[i].Position;
            _wet.Children.Add(new Line
            {
                X1 = p0.X, Y1 = p0.Y, X2 = p1.X, Y2 = p1.Y,
                Stroke = a.Brush,
                StrokeThickness = Width(a.Size, (pts[i - 1].Pressure + pts[i].Pressure) / 2),
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false,
            });
        }

        var guess = _predictor.Predict();
        if (guess.Count == 0) return;
        var last = pts[^1];
        _tail.Points.Clear();
        _tail.Points.Add(last.Position);
        foreach (var g in guess) _tail.Points.Add(new Point(g.X, g.Y));
        _tail.Stroke = a.Brush;
        _tail.StrokeThickness = Width(a.Size, last.Pressure);
        _wet.Children.Add(_tail);
    }

    void ClearWet()
    {
        if (_clearAfterFrames > 0)
        {
            CompositionTarget.Rendering -= OnRendering;
            _clearAfterFrames = 0;
        }
        _tail.Points.Clear();
        _wet.Children.Clear();
    }

    /// <summary>
    /// Takes the wet ink away once the finished stroke has had time to be drawn by the ink canvas, a couple of frames later, so the
    /// stroke doesn't blink out in between.
    /// </summary>
    void ClearWetSoon()
    {
        _wet.Children.Remove(_tail);
        _tail.Points.Clear();
        if (_clearAfterFrames == 0) CompositionTarget.Rendering += OnRendering;
        _clearAfterFrames = 2;
    }

    void OnRendering(object? sender, object e)
    {
        if (--_clearAfterFrames > 0) return;
        CompositionTarget.Rendering -= OnRendering;
        _wet.Children.Clear();
    }

    // ---------- erasing ----------

    /// <summary>Takes away every stroke that the eraser's path, from sample <paramref name="from"/> on, comes close to.</summary>
    void Erase(Active a, int from)
    {
        var container = _strokes();
        var hits = new List<InkStroke>();
        for (int i = from; i < a.Points.Count; i++)
        {
            var p0 = a.Points[Math.Max(0, i - 1)].Position;
            var p1 = a.Points[i].Position;
            var reach = new Rect(p0, p1);
            reach = new Rect(reach.X - EraserHit.Reach - 4, reach.Y - EraserHit.Reach - 4, reach.Width + 2 * EraserHit.Reach + 8, reach.Height + 2 * EraserHit.Reach + 8);
            foreach (var stroke in container.GetStrokes())
            {
                if (hits.Contains(stroke)) continue;
                var box = stroke.BoundingRect;
                box.Intersect(reach);
                if (box.IsEmpty) continue;
                var line = stroke.GetInkPoints().Select(q => ((double)q.Position.X, (double)q.Position.Y)).ToList();
                if (EraserHit.Touches(line, (p0.X, p0.Y), (p1.X, p1.Y))) hits.Add(stroke);
            }
        }
        if (hits.Count == 0) return;
        foreach (var stroke in container.GetStrokes()) stroke.Selected = false;
        foreach (var stroke in hits) stroke.Selected = true;
        container.DeleteSelected();
        a.Erased.AddRange(hits);
    }

    // ---------- the end of a stroke ----------

    void End(PointerRoutedEventArgs e, bool keep)
    {
        if (_active is not { } a || e.Pointer.PointerId != a.PointerId) return;
        _active = null;
        _surface.ReleasePointerCapture(e.Pointer);
        switch (a.Mode)
        {
            case Mode.Ink:
                if (keep && a.Points.Count > 0 && Build(a) is { } stroke)
                {
                    _strokes().AddStroke(stroke);
                    ClearWetSoon();
                    StrokesCollected?.Invoke(new[] { stroke });
                }
                else ClearWet();
                break;
            case Mode.Erase:
                // The strokes are gone whether or not the pass ended well, so it is told either way.
                if (a.Erased.Count > 0) StrokesErased?.Invoke(a.Erased);
                break;
            case Mode.Lasso:
                if (keep) LassoReleased?.Invoke();
                else LassoLost?.Invoke();
                break;
        }
    }

    /// <summary>The finished stroke, from its samples (not the guesses), with the drawing attributes it was begun with.</summary>
    InkStroke? Build(Active a)
    {
        var builder = new InkStrokeBuilder();
        var attrs = DrawingAttributes;
        builder.SetDefaultDrawingAttributes(attrs);
        var points = a.Points;
        // One sample is a dot: Windows draws a stroke of one point as nothing, so it gets a second point on top of the first.
        if (points.Count == 1) points = new List<InkPoint> { points[0], points[0] };
        var duration = TimeSpan.FromTicks((long)((points[^1].Timestamp - points[0].Timestamp) * 10));
        return builder.CreateStrokeFromInkPoints(points, Matrix3x2.Identity, DateTimeOffset.Now - duration, duration);
    }
}
