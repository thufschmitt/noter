using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Windows.Forms;
using Longhand.Services;

namespace Longhand.PenDemo;

/// <summary>
/// A sheet to write on, fed by Win32's pointer messages: on every message it takes all the samples the digitizer took since the last
/// one (the pointer's history), draws them, and draws the predictor's guess ahead of them on top, in place of the last guess.
/// Finished strokes are only their samples. The pen's eraser end, or the mouse's right button, erases whole strokes.
/// </summary>
sealed class InkPanel : Control
{
    const double PenSize = 2.4; // DIPs, as on the page

    sealed class Stroke
    {
        public readonly List<(double X, double Y, double Pressure)> Points = new(); // client pixels
    }

    enum Mode { Ink, Erase }

    readonly List<Stroke> _strokes = new();
    readonly StrokePredictor _predictor = new(0.025);
    Stroke? _current;
    Mode _mode;
    uint _pointerId;
    bool _active;
    double _lastTime;
    (double X, double Y)? _lastErase;

    Bitmap? _ink, _frame;
    Graphics? _inkG;
    Rectangle _guessBounds = Rectangle.Empty;

    // What the status line shows.
    readonly Queue<double> _sampleTimes = new();
    int _messages, _samples;

    public InkPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
        BackColor = Color.White;
    }

    /// <summary>Whether the guess is drawn at all, to compare with and without.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Predicting { get; set; } = true;
    /// <summary>
    /// Hides the pointer (the pen's dot, the mouse's arrow) while a stroke is written: it is drawn by the system a frame or more behind
    /// the pen, and the ink drawn ahead of the pen makes the lag plain. Windows Ink hides it too.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool HidePointer { get; set; } = true;

    static readonly Lazy<Cursor> Transparent = new(() => new Cursor(Native.TransparentCursor()));
    Cursor? _shownCursor;

    void HideCursor()
    {
        _shownCursor ??= Cursor;
        Cursor = Transparent.Value; // WinForms answers WM_SETCURSOR with it from now on
        Native.SetCursor(Transparent.Value.Handle); // and now, without waiting for the next one
    }

    void ShowCursor()
    {
        if (_shownCursor is null) return;
        Cursor = _shownCursor;
        _shownCursor = null;
        Native.SetCursor(Cursor.Handle);
    }

    /// <summary>Draws the guess in red, to see what it does.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowGuess { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double HorizonMs { get => _predictor.Horizon * 1000; set => _predictor.Horizon = value / 1000; }

    public event Action<string>? Status;

    double DpiScale => DeviceDpi / 96.0;

    public void Clear()
    {
        _strokes.Clear();
        Rebuild();
        Invalidate();
    }

    // ---------- pointer messages ----------

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.NoFeedback(Handle); // no tap ripples or barrel-button rings, which trail the pen like the pointer does
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case Native.WM_POINTERDOWN:
                OnPointer(Native.PointerId(m.WParam), down: true, up: false);
                m.Result = IntPtr.Zero;
                return;
            case Native.WM_POINTERUPDATE:
                OnPointer(Native.PointerId(m.WParam), down: false, up: false);
                m.Result = IntPtr.Zero;
                return;
            case Native.WM_POINTERUP:
                OnPointer(Native.PointerId(m.WParam), down: false, up: true);
                m.Result = IntPtr.Zero;
                return;
            case Native.WM_POINTERCAPTURECHANGED:
                if (_active && Native.PointerId(m.WParam) == _pointerId) Finish();
                m.Result = IntPtr.Zero;
                return;
        }
        base.WndProc(ref m);
    }

    /// <summary>One sample, in client pixels, with its pressure (0 to 1) and time (seconds).</summary>
    readonly record struct Sample(double X, double Y, double Pressure, double Time, bool InContact, bool Erases);

    void OnPointer(uint id, bool down, bool up)
    {
        if (!Native.GetPointerType(id, out var type)) return;
        if (down && !_active)
        {
            var first = Samples(id, type, newestOnly: true);
            if (first.Count == 0) return;
            var s = first[^1];
            _active = true;
            _pointerId = id;
            _mode = s.Erases ? Mode.Erase : Mode.Ink;
            _lastTime = double.NegativeInfinity;
            _lastErase = null;
            if (type == Native.PointerType.Mouse) Capture = true;
            if (HidePointer) HideCursor();
            if (_mode == Mode.Ink)
            {
                _current = new Stroke();
                _strokes.Add(_current);
                _predictor.Reset();
            }
            Take(first);
            return;
        }
        if (!_active || id != _pointerId) return;
        Take(Samples(id, type, newestOnly: false));
        if (up) Finish();
    }

    /// <summary>The samples since the last message, oldest first (the history comes newest first).</summary>
    List<Sample> Samples(uint id, Native.PointerType type, bool newestOnly)
    {
        var result = new List<Sample>();
        var origin = PointToScreen(Point.Empty);
        if (type == Native.PointerType.Pen)
        {
            if (!Native.GetPointerPenInfo(id, out var latest)) return result;
            var entries = new[] { latest };
            uint count = newestOnly ? 1 : Math.Max(1, latest.pointerInfo.historyCount);
            if (count > 1)
            {
                entries = new Native.POINTER_PEN_INFO[count];
                if (!Native.GetPointerPenInfoHistory(id, ref count, entries)) entries = new[] { latest };
                else if (count < entries.Length) Array.Resize(ref entries, (int)count);
            }
            for (int i = entries.Length - 1; i >= 0; i--)
            {
                var e = entries[i];
                var (x, y) = Position(e.pointerInfo, origin);
                double pressure = (e.penMask & Native.PEN_MASK_PRESSURE) != 0 ? e.pressure / 1024.0 : 0.5;
                bool erases = (e.penFlags & (Native.PEN_FLAG_ERASER | Native.PEN_FLAG_INVERTED)) != 0;
                result.Add(new Sample(x, y, pressure, Time(e.pointerInfo), (e.pointerInfo.pointerFlags & Native.POINTER_FLAG_INCONTACT) != 0, erases));
            }
        }
        else
        {
            if (!Native.GetPointerInfo(id, out var latest)) return result;
            var entries = new[] { latest };
            uint count = newestOnly ? 1 : Math.Max(1, latest.historyCount);
            if (count > 1)
            {
                entries = new Native.POINTER_INFO[count];
                if (!Native.GetPointerInfoHistory(id, ref count, entries)) entries = new[] { latest };
                else if (count < entries.Length) Array.Resize(ref entries, (int)count);
            }
            for (int i = entries.Length - 1; i >= 0; i--)
            {
                var e = entries[i];
                var (x, y) = Position(e, origin);
                bool erases = type == Native.PointerType.Mouse && (e.pointerFlags & Native.POINTER_FLAG_SECONDBUTTON) != 0;
                result.Add(new Sample(x, y, 0.5, Time(e), (e.pointerFlags & Native.POINTER_FLAG_INCONTACT) != 0, erases));
            }
        }
        return result;
    }

    /// <summary>
    /// Where the pointer was, in client pixels, with the digitizer's own precision: the HIMETRIC position mapped onto the display
    /// (whole pixels would add a quarter pixel of noise). Falls back to the pixel position when the mapping doesn't agree with it
    /// (a rotated screen, a mouse).
    /// </summary>
    static (double X, double Y) Position(Native.POINTER_INFO info, Point origin)
    {
        double px = info.ptPixelLocation.X, py = info.ptPixelLocation.Y;
        if (Native.GetPointerDeviceRects(info.sourceDevice, out var device, out var display))
        {
            double dw = device.Right - device.Left, dh = device.Bottom - device.Top;
            if (dw > 0 && dh > 0)
            {
                double x = display.Left + (info.ptHimetricLocation.X - device.Left) * (display.Right - display.Left) / dw;
                double y = display.Top + (info.ptHimetricLocation.Y - device.Top) * (display.Bottom - display.Top) / dh;
                if (Math.Abs(x - px) < 2 && Math.Abs(y - py) < 2)
                {
                    px = x;
                    py = y;
                }
            }
        }
        return (px - origin.X, py - origin.Y);
    }

    /// <summary>When the sample was taken, in seconds: the high-resolution counter when the device gives it, else milliseconds.</summary>
    static double Time(Native.POINTER_INFO info) =>
        info.PerformanceCount != 0 ? info.PerformanceCount / (double)Stopwatch.Frequency : info.dwTime / 1000.0;

    void Take(List<Sample> samples)
    {
        _messages++;
        var dirty = Rectangle.Empty;
        foreach (var s in samples)
        {
            if (s.Time <= _lastTime) continue;
            if (!s.InContact && (_current?.Points.Count > 0 || _lastErase is not null)) continue; // lifting: not part of the stroke
            _lastTime = s.Time;
            _samples++;
            _sampleTimes.Enqueue(s.Time);
            if (_mode == Mode.Erase)
            {
                Erase(s);
                continue;
            }
            var points = _current!.Points;
            points.Add((s.X, s.Y, s.Pressure));
            _predictor.Add(new PenSample(s.X / DpiScale, s.Y / DpiScale, s.Pressure, s.Time));
            dirty = Union(dirty, DrawSegment(_inkG!, points, points.Count - 1));
        }
        while (_sampleTimes.Count > 0 && _lastTime - _sampleTimes.Peek() > 1) _sampleTimes.Dequeue();
        if (_mode == Mode.Ink) Present(dirty, guess: true);
        Report();
    }

    // ---------- drawing ----------

    double LineWidth(double pressure) => PenSize * DpiScale * (0.25 + 1.5 * Math.Clamp(pressure, 0, 1));

    /// <summary>Draws the segment ending at point i of the stroke (a dot for the first), and returns where it drew.</summary>
    Rectangle DrawSegment(Graphics g, List<(double X, double Y, double Pressure)> pts, int i)
    {
        var b = pts[i];
        var a = i > 0 ? pts[i - 1] : b;
        double w = LineWidth((a.Pressure + b.Pressure) / 2);
        using var pen = new Pen(Color.FromArgb(0x22, 0x26, 0x2C), (float)w) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        if (i == 0) g.FillEllipse(pen.Brush, (float)(b.X - w / 2), (float)(b.Y - w / 2), (float)w, (float)w);
        else g.DrawLine(pen, (float)a.X, (float)a.Y, (float)b.X, (float)b.Y);
        return Area(a.X, a.Y, b.X, b.Y, w);
    }

    static Rectangle Area(double x0, double y0, double x1, double y1, double w)
    {
        int pad = (int)Math.Ceiling(w) + 2;
        return Rectangle.FromLTRB((int)Math.Floor(Math.Min(x0, x1)) - pad, (int)Math.Floor(Math.Min(y0, y1)) - pad, (int)Math.Ceiling(Math.Max(x0, x1)) + pad, (int)Math.Ceiling(Math.Max(y0, y1)) + pad);
    }

    static Rectangle Union(Rectangle a, Rectangle b) => a.IsEmpty ? b : b.IsEmpty ? a : Rectangle.Union(a, b);

    /// <summary>Puts the changed part of the sheet on the screen, with the guess on top of the ink (replacing the last guess).</summary>
    void Present(Rectangle dirty, bool guess)
    {
        if (_ink is null || _frame is null) return;
        dirty = Union(dirty, _guessBounds);
        _guessBounds = Rectangle.Empty;
        var points = Array.Empty<PointF>();
        float width = 0;
        if (guess && Predicting && _current is { Points.Count: > 0 } stroke)
        {
            var g = _predictor.Predict();
            if (g.Count > 0)
            {
                var last = stroke.Points[^1];
                points = new[] { new PointF((float)last.X, (float)last.Y) }.Concat(g.Select(p => new PointF((float)(p.X * DpiScale), (float)(p.Y * DpiScale)))).ToArray();
                width = (float)LineWidth(last.Pressure);
                foreach (var p in points) _guessBounds = Union(_guessBounds, Area(p.X, p.Y, p.X, p.Y, width));
                dirty = Union(dirty, _guessBounds);
            }
        }
        dirty.Intersect(new Rectangle(Point.Empty, _ink.Size));
        if (dirty.IsEmpty) return;

        using (var f = Graphics.FromImage(_frame))
        {
            f.CompositingMode = CompositingMode.SourceCopy;
            f.DrawImage(_ink, dirty, dirty, GraphicsUnit.Pixel);
            if (points.Length > 1)
            {
                f.CompositingMode = CompositingMode.SourceOver;
                f.SmoothingMode = SmoothingMode.AntiAlias;
                var color = ShowGuess ? Color.FromArgb(0xE0, 0x30, 0x30) : Color.FromArgb(0x22, 0x26, 0x2C);
                using var pen = new Pen(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                f.DrawLines(pen, points);
            }
        }
        using var screen = CreateGraphics();
        screen.CompositingMode = CompositingMode.SourceCopy;
        screen.DrawImage(_frame, dirty, dirty, GraphicsUnit.Pixel);
    }

    void Finish()
    {
        _active = false;
        Capture = false;
        ShowCursor(); // the pointer back
        _current = null;
        _lastErase = null;
        Present(Rectangle.Empty, guess: false); // takes the last guess away
        Report();
    }

    void Erase(Sample s)
    {
        var here = (s.X / DpiScale, s.Y / DpiScale);
        var from = _lastErase ?? here;
        _lastErase = here;
        int removed = _strokes.RemoveAll(st => EraserHit.Touches(st.Points.Select(p => (p.X / DpiScale, p.Y / DpiScale)).ToList(), from, here));
        if (removed == 0) return;
        Rebuild();
        Invalidate();
    }

    void Report()
    {
        double rate = _sampleTimes.Count > 1 ? (_sampleTimes.Count - 1) / Math.Max(1e-3, _sampleTimes.Last() - _sampleTimes.Peek()) : 0;
        double batch = _messages > 0 ? _samples / (double)_messages : 0;
        Status?.Invoke($"{rate:F0} samples/s · {batch:F1} samples per message · guess confidence {_predictor.Confidence:F2}");
    }

    // ---------- the sheet's bitmaps ----------

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Rebuild();
        Invalidate();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Rebuild();
        Invalidate();
    }

    /// <summary>Draws every stroke again onto a sheet the size of the panel.</summary>
    void Rebuild()
    {
        int w = Math.Max(1, ClientSize.Width), h = Math.Max(1, ClientSize.Height);
        _inkG?.Dispose();
        _ink?.Dispose();
        _frame?.Dispose();
        _ink = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        _frame = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        _inkG = Graphics.FromImage(_ink);
        _inkG.SmoothingMode = SmoothingMode.AntiAlias;
        _inkG.Clear(Color.White);
        foreach (var stroke in _strokes)
            for (int i = 0; i < stroke.Points.Count; i++) DrawSegment(_inkG, stroke.Points, i);
        _guessBounds = Rectangle.Empty;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_ink is null) Rebuild();
        e.Graphics.CompositingMode = CompositingMode.SourceCopy;
        e.Graphics.DrawImage(_ink!, e.ClipRectangle, e.ClipRectangle, GraphicsUnit.Pixel);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inkG?.Dispose();
            _ink?.Dispose();
            _frame?.Dispose();
        }
        base.Dispose(disposing);
    }
}
