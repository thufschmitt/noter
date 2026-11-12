using System;
using System.Collections.Generic;
using System.Linq;
using Longhand.Services;
using Windows.Devices.Input;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Input.Inking;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;

namespace Longhand;

/// <summary>
/// Selecting, moving, turning, resizing and deleting the parts of the model's layouts (the parts it marked <c>edit</c>), the same as the
/// web page does, over the native controls the layout is built from.
///
/// Tap a part with the pen (or click, or tap with a finger) to select it; hold the barrel button (or the right mouse button) and circle
/// parts to select several. A dashed frame appears with a knob above it to turn the part, a cross to delete it, and, on a part of a
/// timeline, a handle below it to change its end; dragging inside the frame moves the part to another container, or to another time.
/// Nothing is sent to the model: <see cref="PageSession.EditDrawing"/> changes the layout, which is built again, and the next Ask
/// reports it. What the pointer means in numbers (which container, which place, which time) is <see cref="LayoutHit"/>.
/// </summary>
public sealed partial class MainPage
{
    enum DragKind { Move, Rotate, Resize, Delete }

    sealed class DragItem
    {
        public required LNode Node;
        public required FrameworkElement Element;
        public required HitRect Rect;
        public FrameworkElement? Ghost;
    }

    /// <summary>Where a dragged part would land: a container and a place in it, or a time on a timeline.</summary>
    sealed class DropSpot
    {
        public required LNode Target;
        public bool Timeline, Row;
        public TimelinePanel? Grid;
        public HitRect Rect;
        public int Index;
        public List<LNode> Kids = new();
        public List<HitRect> KidRects = new();
        /// <summary>Where each dragged part would start, on a timeline.</summary>
        public List<int> Starts = new();
    }

    sealed class Drag
    {
        public required DragKind Kind;
        public required Pointer Pointer;
        public required UIElement Source;
        public required Point Start;
        public required List<DragItem> Items;
        public HitRect Frame;
        public bool Moved;
        public double Degrees, Angle;
        public Point Center;
        public DropSpot? Drop;
        /// <summary>The end an item is being dragged to, and the end it had.</summary>
        public int End, OriginalEnd;
    }

    const double Pad = 6, KnobSize = 26;

    Rectangle _frame = null!, _dropMark = null!;
    Border _knobRotate = null!, _knobDelete = null!, _knobResize = null!;
    Border _dropLabel = null!;
    TextBlock _dropLabelText = null!;
    Polyline _lasso = null!;

    DrawingVisual? _selected;
    readonly List<string> _selectedIds = new();
    Drag? _drag;
    bool _frameQueued;
    List<Point>? _lassoPoints;
    /// <summary>The last stroke written and when: a dot soon after it and near it is part of the writing (the dot of an i), not a tap.</summary>
    (DateTimeOffset At, List<(double X, double Y)> Points)? _lastInk;

    // ---------- keeping writing safe ----------

    int _overlayCount;
    bool _editingToasted;

    /// <summary>
    /// Editing is on top of writing, so if it ever fails (this code was written without being able to run it), writing goes on: the
    /// selection and anything half done are dropped, the blocks are built again from their markup, and the problem is said once.
    /// </summary>
    void Safe(Action work)
    {
        try
        {
            work();
        }
        catch (Exception ex)
        {
            _drag = null;
            try
            {
                while (EditLayer.Children.Count > _overlayCount) EditLayer.Children.RemoveAt(EditLayer.Children.Count - 1);
                ClearSelection();
                RebuildDrawings();
            }
            catch (Exception)
            {
                // nothing more can be done
            }
            if (!_editingToasted)
            {
                _editingToasted = true;
                ShowToast("Editing a layout hit a problem: " + ex.Message, 8);
            }
        }
    }

    T Safe<T>(Func<T> work, T fallback)
    {
        var result = fallback;
        Safe(() => result = work());
        return result;
    }

    // ---------- the overlay ----------

    Border Knob(string glyph, string tip, DragKind kind)
    {
        var knob = new Border
        {
            Width = KnobSize,
            Height = KnobSize,
            CornerRadius = new CornerRadius(KnobSize / 2),
            Background = _paper,
            BorderBrush = _claudeInk,
            BorderThickness = new Thickness(1.5),
            Visibility = Visibility.Collapsed,
            ManipulationMode = ManipulationModes.None,
            Child = new TextBlock { Text = glyph, Foreground = _claudeInk, FontSize = 16, FontWeight = Windows.UI.Text.FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        ToolTipService.SetToolTip(knob, tip);
        Wire(knob, kind);
        return knob;
    }

    /// <summary>
    /// While the pointer is over the frame or a knob the ink presenter is switched off, so that a pen pressed there grabs the part
    /// instead of writing (whether or not the presenter would have let a control above it have the input).
    /// </summary>
    void UpdateInkEnabled(Point? pointer)
    {
        bool over = pointer is { } p && _frame.Visibility == Visibility.Visible
            && (OverElement(_frame, p, 0) || OverElement(_knobRotate, p, 4) || OverElement(_knobDelete, p, 4) || OverElement(_knobResize, p, 4));
        Ink.InkPresenter.IsInputEnabled = !over;
    }

    static bool OverElement(FrameworkElement e, Point p, double tolerance)
    {
        if (e.Visibility != Visibility.Visible) return false;
        double x = Canvas.GetLeft(e), y = Canvas.GetTop(e);
        return new HitRect(x, y, x + e.Width, y + e.Height).Contains(p.X, p.Y, tolerance);
    }

    void Wire(UIElement element, DragKind kind)
    {
        element.PointerEntered += (_, e) => Safe(() => UpdateInkEnabled(e.GetCurrentPoint(Sheet).Position));
        element.PointerExited += (_, e) => Safe(() => { if (_drag is null) UpdateInkEnabled(e.GetCurrentPoint(Sheet).Position); });
        element.PointerPressed += (_, e) => Safe(() => BeginDrag(e, kind, element));
        element.PointerMoved += (s, e) => Safe(() => Drag_PointerMoved(s, e));
        element.PointerReleased += (_, e) => Safe(() => EndDrag(e, cancelled: false));
        element.PointerCanceled += (_, e) => Safe(() => EndDrag(e, cancelled: true));
        element.PointerCaptureLost += (_, e) => Safe(() => EndDrag(e, cancelled: true));
    }

    void BuildEditOverlay()
    {
        _lasso = new Polyline { Stroke = _claudeInk, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 6, 5 }, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        _dropMark = new Rectangle { Fill = _claudeInk, RadiusX = 2, RadiusY = 2, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        _dropLabelText = new TextBlock { Foreground = _claudeInk, FontSize = 12 };
        _dropLabel = new Border { Background = _paper, Padding = new Thickness(4, 0, 4, 0), CornerRadius = new CornerRadius(3), Child = _dropLabelText, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        // The frame is filled (with nothing) so that the pen lands on it and grabs the part instead of writing.
        _frame = new Rectangle
        {
            Stroke = _claudeInk,
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            RadiusX = 4,
            RadiusY = 4,
            Fill = new SolidColorBrush(Colors.Transparent),
            Visibility = Visibility.Collapsed,
            ManipulationMode = ManipulationModes.None,
        };
        Wire(_frame, DragKind.Move);
        _knobRotate = Knob("\u21BB", "Turn", DragKind.Rotate);
        _knobDelete = Knob("\u00D7", "Delete", DragKind.Delete);
        _knobResize = Knob("\u2195", "Change the end", DragKind.Resize);
        foreach (var e in new UIElement[] { _lasso, _dropMark, _dropLabel, _frame, _knobRotate, _knobDelete, _knobResize }) EditLayer.Children.Add(e);
        _overlayCount = EditLayer.Children.Count;
    }

    static void PlaceAt(FrameworkElement element, double x, double y, double width, double height)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        element.Width = Math.Max(1, width);
        element.Height = Math.Max(1, height);
    }

    // ---------- geometry ----------

    /// <summary>Where a control is on the page (the sheet's coordinates, which the ink shares); an empty rectangle for one that isn't on it.</summary>
    HitRect PageRect(FrameworkElement element)
    {
        try
        {
            var r = element.TransformToVisual(Sheet).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            return new HitRect(r.Left, r.Top, r.Right, r.Bottom);
        }
        catch (ArgumentException)
        {
            return new HitRect(0, 0, 0, 0);
        }
    }

    IEnumerable<DrawingVisual> LayoutVisuals() =>
        _session.Notes.SelectMany(n => n.Drawings).Select(VisualOf).Where(v => v.Built is not null);

    static IEnumerable<LNode> EditableParts(BuiltLayout built) =>
        built.Tree.Descendants().Where(n => !n.IsText && n.Has("edit") && built.Elements.ContainsKey(n));

    /// <summary>The smallest editable part under a point of the page.</summary>
    (DrawingVisual Visual, LNode Node)? EditableAt(double x, double y)
    {
        (DrawingVisual, LNode)? best = null;
        double bestArea = double.MaxValue;
        foreach (var v in LayoutVisuals())
        {
            if (!PageRect(v.Host).Contains(x, y, 8)) continue;
            foreach (var node in EditableParts(v.Built!))
            {
                var r = PageRect(v.Built!.Elements[node]);
                if (r.Contains(x, y, 4) && r.Area < bestArea)
                {
                    best = (v, node);
                    bestArea = r.Area;
                }
            }
        }
        return best;
    }

    // ---------- the selection ----------

    List<LNode> SelectedNodes() =>
        _selected?.Built is { } built
            ? _selectedIds.Select(id => Layout.Find(built.Tree, id)).Where(n => n is not null && built.Elements.ContainsKey(n)).Select(n => n!).ToList()
            : new List<LNode>();

    void Select(DrawingVisual visual, IEnumerable<LNode> nodes)
    {
        var parts = nodes.Where(n => n.Id is not null).ToList();
        // A part inside another selected part moves with it already.
        parts = parts.Where(a => !parts.Any(b => b != a && b.Descendants().Contains(a))).ToList();
        if (parts.Count == 0)
        {
            ClearSelection();
            return;
        }
        if (_drag is not null) AbortDrag();
        _selected = visual;
        _selectedIds.Clear();
        _selectedIds.AddRange(parts.Select(n => n.Id!));
        UpdateSelectionFrame();
    }

    void ClearSelection()
    {
        if (_drag is not null) AbortDrag();
        _selected = null;
        _selectedIds.Clear();
        foreach (var e in new UIElement[] { _frame, _knobRotate, _knobDelete, _knobResize }) e.Visibility = Visibility.Collapsed;
        Ink.InkPresenter.IsInputEnabled = true;
    }

    /// <summary>A block was built again or moved: the frame is put round the selected parts once it has been laid out.</summary>
    void ScheduleFrameUpdate()
    {
        if (_selected is null || _frameQueued) return;
        _frameQueued = true;
        _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () => Safe(UpdateSelectionFrame));
    }

    void UpdateSelectionFrame()
    {
        _frameQueued = false;
        if (_selected is null || _drag is { Kind: DragKind.Move }) return;
        var nodes = SelectedNodes();
        if (nodes.Count == 0)
        {
            ClearSelection(); // what was selected is gone (deleted, or the model redrew the layout without it)
            return;
        }
        _selected.Host.UpdateLayout();
        var rects = nodes.Select(n => PageRect(_selected.Built!.Elements[n])).ToList();
        PlaceFrame(new HitRect(rects.Min(r => r.Left) - Pad, rects.Min(r => r.Top) - Pad, rects.Max(r => r.Right) + Pad, rects.Max(r => r.Bottom) + Pad));
    }

    bool CanRotateSelection(List<LNode> nodes) => nodes.All(n => n.Parent is null || !Layout.IsTimeline(n.Parent));

    bool CanResizeSelection(List<LNode> nodes) => nodes.Count == 1 && Layout.TimesOf(nodes[0]) is not null;

    /// <summary>Puts the frame, and the knobs that fit what is selected, round a rectangle.</summary>
    void PlaceFrame(HitRect r)
    {
        var nodes = SelectedNodes();
        PlaceAt(_frame, r.Left, r.Top, r.Width, r.Height);
        _frame.Visibility = Visibility.Visible;
        PlaceKnob(_knobRotate, r.CenterX, r.Top - 30, CanRotateSelection(nodes));
        PlaceKnob(_knobDelete, r.Right, r.Top, true);
        PlaceKnob(_knobResize, r.CenterX, r.Bottom, CanResizeSelection(nodes));
    }

    static void PlaceKnob(Border knob, double centreX, double centreY, bool shown)
    {
        Canvas.SetLeft(knob, centreX - KnobSize / 2);
        Canvas.SetTop(knob, centreY - KnobSize / 2);
        knob.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
    }

    bool SelectAt(double x, double y)
    {
        if (EditableAt(x, y) is not { } hit) return false;
        Select(hit.Visual, new[] { hit.Node });
        return true;
    }

    // ---------- tapping ----------

    /// <summary>
    /// A stroke that is only a tap, on a part of a layout, selects the part and is taken off the page (so it leaves no dot). A dot soon
    /// after other ink and near it is writing (the dot of an i, a full stop) and stays.
    /// </summary>
    bool TapSelects(IReadOnlyList<InkStroke> strokes)
    {
        if (strokes.Count != 1 || !LayoutVisuals().Any(v => EditableParts(v.Built!).Any())) return false;
        var stroke = strokes[0];
        var points = stroke.GetInkPoints().Select(q => (X: (double)q.Position.X, Y: (double)q.Position.Y)).ToList();
        if (!LayoutHit.IsTap(points)) return false;
        if (stroke.StrokeDuration is { } lasted && lasted > TimeSpan.FromMilliseconds(400)) return false;
        if (_lastInk is { } last && DateTimeOffset.UtcNow - last.At < TimeSpan.FromSeconds(1)
            && last.Points.Any(q => double.Hypot(q.X - points[0].X, q.Y - points[0].Y) < 70)) return false;
        if (EditableAt(points[0].X, points[0].Y) is not { } hit) return false;
        stroke.Selected = true;
        Strokes.DeleteSelected();
        Select(hit.Visual, new[] { hit.Node });
        return true;
    }

    void NoteInk(IReadOnlyList<InkStroke> strokes)
    {
        if (strokes.Count == 0) return;
        _lastInk = (DateTimeOffset.UtcNow, strokes[^1].GetInkPoints().TakeLast(40).Select(q => ((double)q.Position.X, (double)q.Position.Y)).ToList());
    }

    /// <summary>A finger tap selects too (a finger otherwise scrolls the page).</summary>
    void Sheet_Tapped(object sender, TappedRoutedEventArgs e) => Safe(() =>
    {
        if (e.PointerDeviceType != PointerDeviceType.Touch) return;
        var p = e.GetPosition(Sheet);
        if (!SelectAt(p.X, p.Y)) ClearSelection();
    });

    void OnWindowKeyDown(CoreWindow sender, KeyEventArgs args) => Safe(() =>
    {
        if (_selected is null || FocusManager.GetFocusedElement() is TextBox or PasswordBox) return;
        if (args.VirtualKey is Windows.System.VirtualKey.Delete or Windows.System.VirtualKey.Back)
        {
            args.Handled = true;
            DeleteSelection();
        }
        else if (args.VirtualKey == Windows.System.VirtualKey.Escape)
        {
            args.Handled = true;
            ClearSelection();
        }
    });

    // ---------- the lasso ----------

    // The ink presenter reports unprocessed input in the window's coordinates; the page's are the sheet's.
    Point ToSheet(Point windowPoint) => Sheet.TransformToVisual(Window.Current.Content).Inverse.TransformPoint(windowPoint);

    void OnLassoPressed(InkUnprocessedInput sender, PointerEventArgs args) => Safe(() =>
    {
        var p = ToSheet(args.CurrentPoint.Position);
        _lassoPoints = new List<Point> { p };
        _lasso.Points.Clear();
        _lasso.Points.Add(p);
        _lasso.Visibility = Visibility.Visible;
    });

    void OnLassoMoved(InkUnprocessedInput sender, PointerEventArgs args) => Safe(() =>
    {
        if (_lassoPoints is null) return;
        var p = ToSheet(args.CurrentPoint.Position);
        _lassoPoints.Add(p);
        _lasso.Points.Add(p);
    });

    void OnLassoReleased(InkUnprocessedInput sender, PointerEventArgs args) => Safe(EndLasso);

    void EndLasso()
    {
        var points = _lassoPoints;
        _lassoPoints = null;
        _lasso.Visibility = Visibility.Collapsed;
        _lasso.Points.Clear();
        if (points is null) return;
        var polygon = points.Select(q => (X: q.X, Y: q.Y)).ToList();
        if (polygon.Count < 3 || LayoutHit.IsTap(polygon))
        {
            // A barrel press without a loop selects what is under it.
            if (!SelectAt(points[0].X, points[0].Y)) ClearSelection();
            return;
        }
        DrawingVisual? best = null;
        var chosen = new List<LNode>();
        foreach (var v in LayoutVisuals())
        {
            var inside = EditableParts(v.Built!).Where(n => LayoutHit.MostlyInside(PageRect(v.Built!.Elements[n]), polygon)).ToList();
            if (inside.Count > chosen.Count)
            {
                best = v;
                chosen = inside;
            }
        }
        if (best is not null) Select(best, chosen);
        else ClearSelection();
    }

    // ---------- dragging ----------

    void BeginDrag(PointerRoutedEventArgs e, DragKind kind, UIElement source)
    {
        // Only a plain press grabs: the pen's barrel button and a mouse's right button are for the lasso, and the eraser end is for erasing.
        var props = e.GetCurrentPoint(Sheet).Properties;
        if (!props.IsLeftButtonPressed || props.IsRightButtonPressed || props.IsBarrelButtonPressed || props.IsEraser) return;
        if (_drag is not null || _selected?.Built is not { } built) return;
        var nodes = SelectedNodes();
        if (nodes.Count == 0) return;
        var items = nodes.Select(n => new DragItem { Node = n, Element = built.Elements[n], Rect = PageRect(built.Elements[n]) }).ToList();
        var frame = new HitRect(items.Min(i => i.Rect.Left) - Pad, items.Min(i => i.Rect.Top) - Pad, items.Max(i => i.Rect.Right) + Pad, items.Max(i => i.Rect.Bottom) + Pad);
        var start = e.GetCurrentPoint(Sheet).Position;
        source.CapturePointer(e.Pointer);
        var drag = new Drag
        {
            Kind = kind,
            Pointer = e.Pointer,
            Source = source,
            Start = start,
            Items = items,
            Frame = frame,
            Center = new Point(frame.CenterX, frame.CenterY),
            Angle = Math.Atan2(start.Y - frame.CenterY, start.X - frame.CenterX),
        };
        if (kind == DragKind.Resize && Layout.TimesOf(nodes[0]) is { } times) drag.End = drag.OriginalEnd = times.End;
        _drag = drag;
        if (kind == DragKind.Move) StartGhosts(drag);
        e.Handled = true;
    }

    /// <summary>
    /// While a part is moved, the part itself stays where it is, dimmed, and a copy of it follows the pointer. (Moving the real control
    /// would put it under whatever comes after it in its container.)
    /// </summary>
    void StartGhosts(Drag drag)
    {
        var style = CurrentLayoutStyle();
        foreach (var item in drag.Items)
        {
            var ghost = LayoutXaml.BuildPart(item.Node, style);
            ghost.Width = item.Rect.Width;
            ghost.Height = item.Rect.Height;
            ghost.Opacity = 0.7;
            ghost.IsHitTestVisible = false;
            Canvas.SetLeft(ghost, item.Rect.Left);
            Canvas.SetTop(ghost, item.Rect.Top);
            EditLayer.Children.Add(ghost);
            item.Ghost = ghost;
            item.Element.Opacity = 0.35;
        }
    }

    void Drag_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is not { } d || e.Pointer.PointerId != d.Pointer.PointerId || _selected?.Built is not { } built) return;
        var p = e.GetCurrentPoint(Sheet).Position;
        switch (d.Kind)
        {
            case DragKind.Move:
            {
                double dx = p.X - d.Start.X, dy = p.Y - d.Start.Y;
                foreach (var item in d.Items)
                {
                    Canvas.SetLeft(item.Ghost, item.Rect.Left + dx);
                    Canvas.SetTop(item.Ghost, item.Rect.Top + dy);
                }
                d.Drop = ComputeDrop(d, built, p, dy);
                ShowDropMark(d.Drop);
                PlaceFrame(new HitRect(d.Frame.Left + dx, d.Frame.Top + dy, d.Frame.Right + dx, d.Frame.Bottom + dy));
                d.Moved |= double.Hypot(dx, dy) > 4;
                break;
            }
            case DragKind.Resize:
            {
                // The end follows the pointer, a step at a time, and the item stretches as it goes.
                var item = d.Items[0];
                if (item.Node.Parent is not { } column || !built.Grids.TryGetValue(column, out var grid) || Layout.TimesOf(item.Node) is not { } times) break;
                d.End = LayoutHit.EndFor(p.Y, PageRect(grid).Top, grid.FromMinutes, times.Start);
                grid.SetEnd(item.Element, d.End);
                d.Moved = d.End != d.OriginalEnd;
                _selected.Host.UpdateLayout();
                var r = PageRect(item.Element);
                PlaceFrame(new HitRect(r.Left - Pad, r.Top - Pad, r.Right + Pad, r.Bottom + Pad));
                break;
            }
            case DragKind.Rotate:
            {
                double angle = Math.Atan2(p.Y - d.Center.Y, p.X - d.Center.X);
                double turn = (angle - d.Angle) * 180 / Math.PI;
                d.Degrees += ((turn + 540) % 360 + 360) % 360 - 180;
                d.Angle = angle;
                foreach (var item in d.Items)
                {
                    item.Element.RenderTransformOrigin = new Point(0.5, 0.5);
                    item.Element.RenderTransform = new RotateTransform { Angle = item.Node.Rot + d.Degrees };
                }
                d.Moved |= Math.Abs(d.Degrees) > 0.5;
                break;
            }
        }
        e.Handled = true;
    }

    void EndDrag(PointerRoutedEventArgs e, bool cancelled)
    {
        if (_drag is not { } d || e.Pointer.PointerId != d.Pointer.PointerId) return;
        _drag = null;
        d.Source.ReleasePointerCapture(e.Pointer);
        var p = e.GetCurrentPoint(Sheet).Position;
        FinishDrag(d, p, cancelled);
        UpdateInkEnabled(p); // still over the frame (it was repositioned or rebuilt): the pen is still for grabbing
        e.Handled = true;
    }

    /// <summary>Puts everything a drag changed on screen back (the copies, the dimming, the mark); a drag that makes a change then builds the block again.</summary>
    void CleanUpDrag(Drag d)
    {
        foreach (var item in d.Items)
        {
            if (item.Ghost is not null) EditLayer.Children.Remove(item.Ghost);
            item.Ghost = null;
            item.Element.Opacity = 1;
        }
        ShowDropMark(null);
    }

    void AbortDrag()
    {
        if (_drag is not { } d) return;
        _drag = null;
        d.Source.ReleasePointerCapture(d.Pointer);
        CleanUpDrag(d);
    }

    void FinishDrag(Drag d, Point p, bool cancelled)
    {
        CleanUpDrag(d);
        var visual = _selected;
        if (visual is null) return;
        if (d.Kind == DragKind.Delete)
        {
            var knob = new HitRect(Canvas.GetLeft(_knobDelete), Canvas.GetTop(_knobDelete), Canvas.GetLeft(_knobDelete) + KnobSize, Canvas.GetTop(_knobDelete) + KnobSize);
            if (!cancelled && knob.Contains(p.X, p.Y, 4)) DeleteSelection();
            return;
        }
        if (cancelled || !d.Moved)
        {
            RestoreDragged(d, visual);
            return;
        }

        var changes = new List<DrawingChange>();
        switch (d.Kind)
        {
            case DragKind.Move when d.Drop is { } drop && drop.Target.Id is { } target:
                for (int k = 0; k < d.Items.Count; k++)
                {
                    var id = d.Items[k].Node.Id!;
                    changes.Add(drop.Timeline
                        ? new DrawingChange(id, "move", target, 0, At: Layout.Hhmm(drop.Starts[k]))
                        : new DrawingChange(id, "move", target, drop.Index + k));
                }
                break;
            case DragKind.Resize:
                changes.Add(new DrawingChange(d.Items[0].Node.Id!, "resize", End: Layout.Hhmm(d.End)));
                break;
            case DragKind.Rotate:
                changes.AddRange(d.Items.Select(i => new DrawingChange(i.Node.Id!, "rotate", Degrees: Math.Round(d.Degrees * 10) / 10)));
                break;
        }
        if (changes.Count == 0)
        {
            RestoreDragged(d, visual); // dropped where nothing accepts it: it goes back
            return;
        }
        // The block is built again from the changed layout (or as it was, if the change wasn't allowed).
        _session.EditDrawing(visual.Note, visual.Drawing, changes);
    }

    /// <summary>A drag that changed nothing: what it moved live (a turn, an end) goes back to what the layout says.</summary>
    void RestoreDragged(Drag d, DrawingVisual visual)
    {
        foreach (var item in d.Items)
        {
            if (d.Kind == DragKind.Rotate) item.Element.RenderTransform = item.Node.Rot != 0 ? new RotateTransform { Angle = item.Node.Rot } : null;
            if (d.Kind == DragKind.Resize && item.Node.Parent is { } column && visual.Built!.Grids.TryGetValue(column, out var grid)) grid.SetEnd(item.Element, d.OriginalEnd);
        }
        UpdateSelectionFrame();
    }

    void DeleteSelection()
    {
        if (_selected is not { } v) return;
        var changes = _selectedIds.Select(id => new DrawingChange(id, "delete")).ToList();
        ClearSelection();
        _session.EditDrawing(v.Note, v.Drawing, changes);
    }

    // ---------- where a drop lands ----------

    /// <summary>
    /// The container under the pointer that accepts what is dragged (the smallest, and never one inside what is dragged), and where in it:
    /// a time on a timeline, from where each part's top edge is; otherwise a place among the container's other parts. A timeline always
    /// takes its own items.
    /// </summary>
    DropSpot? ComputeDrop(Drag d, BuiltLayout built, Point p, double dy)
    {
        var dragged = d.Items.Select(i => i.Node).ToList();
        var own = dragged.Select(n => n.Parent).Where(n => n is not null && Layout.IsTimeline(n)).ToHashSet();
        var candidates = built.Tree.Descendants()
            .Where(n => !n.IsText && (n.Tag is "row" or "col" or "box") && (n.Has("drop") || own.Contains(n)) && built.Elements.ContainsKey(n)
                && !dragged.Any(x => x == n || x.Descendants().Contains(n)))
            .ToList();
        var rects = candidates.Select(n => PageRect(built.Elements[n])).ToList();
        int i = LayoutHit.Smallest(rects, p.X, p.Y);
        if (i < 0) return null;
        var target = candidates[i];

        if (Layout.IsTimeline(target) && built.Grids.TryGetValue(target, out var grid))
        {
            var gridRect = PageRect(grid);
            return new DropSpot
            {
                Target = target,
                Timeline = true,
                Grid = grid,
                Rect = gridRect,
                Starts = d.Items.Select(it => LayoutHit.StartFor(it.Rect.Top + dy, gridRect.Top, grid.FromMinutes,
                    Layout.TimesOf(it.Node) is { } t ? t.End - t.Start : Layout.DefaultMinutes)).ToList(),
            };
        }
        bool row = target.Tag == "row";
        var kids = target.Elements.Where(k => !dragged.Contains(k) && built.Elements.ContainsKey(k)).ToList();
        var kidRects = kids.Select(k => PageRect(built.Elements[k])).ToList();
        return new DropSpot { Target = target, Row = row, Rect = rects[i], Kids = kids, KidRects = kidRects, Index = LayoutHit.IndexAmong(kidRects, row, p.X, p.Y) };
    }

    /// <summary>A line where the part would land (on a timeline, at the time it would start, written on it).</summary>
    void ShowDropMark(DropSpot? drop)
    {
        if (drop is null)
        {
            _dropMark.Visibility = Visibility.Collapsed;
            _dropLabel.Visibility = Visibility.Collapsed;
            return;
        }
        _dropMark.Visibility = Visibility.Visible;
        if (drop.Timeline && drop.Grid is { } grid)
        {
            double y = drop.Rect.Top + LayoutHit.TimelineY(drop.Starts[0], grid.FromMinutes);
            PlaceAt(_dropMark, drop.Rect.Left + grid.Gutter, y - 1.5, drop.Rect.Width - grid.Gutter, 3);
            _dropLabelText.Text = Layout.Hhmm(drop.Starts[0]);
            Canvas.SetLeft(_dropLabel, drop.Rect.Left + grid.Gutter + 2);
            Canvas.SetTop(_dropLabel, y - 20);
            _dropLabel.Visibility = Visibility.Visible;
            return;
        }
        _dropLabel.Visibility = Visibility.Collapsed;
        if (drop.Kids.Count == 0)
        {
            PlaceAt(_dropMark, drop.Rect.Left + 8, drop.Rect.Top + 8, Math.Max(10, drop.Rect.Width - 16), 3);
            return;
        }
        bool before = drop.Index < drop.Kids.Count;
        var r = before ? drop.KidRects[drop.Index] : drop.KidRects[^1];
        if (drop.Row) PlaceAt(_dropMark, before ? r.Left - 7 : r.Right + 5, r.Top, 3, r.Height);
        else PlaceAt(_dropMark, r.Left, before ? r.Top - 5 : r.Bottom + 3, r.Width, 3);
    }
}
