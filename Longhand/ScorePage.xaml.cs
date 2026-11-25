using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Longhand.Services;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Input;
using Windows.UI.Input.Inking;
using Windows.UI.Input.Inking.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace Longhand;

/// <summary>
/// Longhand Score, the package's third app: staves to write music on with the pen. When the pen rests, what it wrote is read by
/// <see cref="MusicInk"/> (on the PC, no model, no network) and replaced by clean notation drawn by <see cref="ScoreEngraver"/>; ink
/// it can't read stays, and is read again with what comes next. The score is saved as <c>score.json</c> in the app's local folder
/// after every change. The page itself is <see cref="ScoreSheet"/>, shared with the web version (<c>/score</c>); this class is its screen.
/// </summary>
public sealed partial class ScorePage : Page
{
    const string ScoreFile = "score.json";
    /// <summary>How long the pen rests before what it wrote is read.</summary>
    static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(700);
    const double EraseReach = 10;

    ScoreSheet _sheet = new();
    readonly DispatcherTimer _readTimer = new() { Interval = Pause };
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly SolidColorBrush _desk = new(), _paper = new(), _muted = new(), _tray = new(), _trayLine = new(), _uiText = new();
    Color _engraving, _pendingInk;
    MediaPlayer? _player;
    /// <summary>The eraser being drawn over the page: its pointer, where it has been, and whether it has rubbed anything out yet.</summary>
    sealed class Rub
    {
        public uint Pointer;
        public Point Last;
        public bool Changed;
    }
    Rub? _rub;
    /// <summary>True while a pen with its eraser end down is near the page: the pen is then taken from Windows Ink, so the page gets it.</summary>
    bool _penIsEraser;
    // The pen's eraser end as Windows Ink reports it, on the ink thread (when it reaches the ink before the page notices it).
    readonly object _penEraserLock = new();
    List<Point>? _penEraserPath;
    CoreInkIndependentInputSource? _independent;

    InkStrokeContainer Strokes => Ink.InkPresenter.StrokeContainer;

    public ScorePage()
    {
        InitializeComponent();
        var presenter = Ink.InkPresenter;
        presenter.InputDeviceTypes = CoreInputDeviceTypes.Pen | CoreInputDeviceTypes.Mouse;
        presenter.StrokeInput.StrokeStarted += (_, _) => _readTimer.Stop();
        presenter.StrokesCollected += (_, _) => { _readTimer.Stop(); _readTimer.Start(); };
        // Erasing goes through the page's own pointer events, whose positions are the sheet's whatever the Viewbox does (see Erasing).
        Sheet.AddHandler(PointerPressedEvent, new PointerEventHandler(Sheet_PointerPressed), true);
        Sheet.AddHandler(PointerMovedEvent, new PointerEventHandler(Sheet_PointerMoved), true);
        Sheet.AddHandler(PointerReleasedEvent, new PointerEventHandler(Sheet_PointerReleased), true);
        Sheet.AddHandler(PointerCanceledEvent, new PointerEventHandler(Sheet_PointerEnded), true);
        Sheet.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(Sheet_PointerEnded), true);
        Sheet.AddHandler(PointerExitedEvent, new PointerEventHandler(Sheet_PointerExited), true);
        _readTimer.Tick += (_, _) => Guard(ReadAsync);
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastBox.Visibility = Visibility.Collapsed; };

        var undo = new Windows.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Z, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        undo.Invoked += (_, args) => { args.Handled = true; Guard(UndoAsync); };
        KeyboardAccelerators.Add(undo);

        Root.Background = _desk;
        Sheet.Background = _paper;
        SheetTitle.Foreground = _muted;
        Tray.Background = _tray;
        Tray.BorderBrush = _trayLine;
        TraySeparator.Fill = _trayLine;
        ToastBox.Background = _tray;
        ToastBox.BorderBrush = _trayLine;
        ToastText.Foreground = _uiText;
        ActualThemeChanged += (_, _) => { ApplyTheme(); Render(); };
        Loaded += (_, _) => Guard(OnLoadedAsync);
    }

    async Task OnLoadedAsync()
    {
        ApplyTheme();
        CrashLog.Reported += message => _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => ShowToast("Something went wrong: " + message, 8));
        WatchPenEraser();
        try
        {
            if (await ApplicationData.Current.LocalFolder.TryGetItemAsync(ScoreFile) is StorageFile file)
                _sheet = ScoreSheet.FromJson(await FileIO.ReadTextAsync(file));
        }
        catch (Exception ex)
        {
            // A damaged save starts a fresh score rather than blocking the app.
            CrashLog.Write(ex, "loading the score", fatal: false);
        }
        Render();
    }

    // ---------- crashes ----------

    /// <summary>Runs an event handler's work, catching what it throws, as MainPage does: written down, said, and the app carries on.</summary>
    async void Guard(Func<Task> work, [System.Runtime.CompilerServices.CallerMemberName] string member = "", [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            try
            {
                CrashLog.Write(ex, $"{member} (ScorePage.xaml.cs, line {line})", fatal: false);
                ShowToast("Something went wrong: " + ex.Message + " It's in the crash report (Longhand's gear button → Copy crash report).", 8);
            }
            catch (Exception) { /* nothing more can be done */ }
        }
    }

    // ---------- the page ----------

    void ApplyTheme()
    {
        bool dark = ActualTheme == ElementTheme.Dark;
        _desk.Color = dark ? Hex(0x0D0F13) : Hex(0xE9EDF2);
        _paper.Color = dark ? Hex(0x16191F) : Hex(0xFBFCFD);
        _muted.Color = dark ? Hex(0x8D96A6) : Hex(0x7A8494);
        _tray.Color = dark ? Color.FromArgb(0xEB, 0x1E, 0x22, 0x2A) : Color.FromArgb(0xEB, 0xFF, 0xFF, 0xFF);
        _trayLine.Color = dark ? Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1F, 0x1E, 0x28, 0x3C);
        _uiText.Color = dark ? Hex(0xE6E8EC) : Hex(0x2A2F37);
        _engraving = dark ? Hex(0xE6E8EC) : Hex(0x22262C);
        // Ink not read yet is blue, so it stands apart from what has been engraved.
        _pendingInk = dark ? Hex(0x93A9FF) : Hex(0x2C4FC4);
        Ink.InkPresenter.UpdateDefaultDrawingAttributes(new InkDrawingAttributes
        {
            Color = _pendingInk,
            Size = new Size(2.6, 2.6),
            FitToCurve = true,
            IgnorePressure = true,
            PenTip = PenTipShape.Circle,
        });
        foreach (var stroke in Strokes.GetStrokes())
        {
            var a = stroke.DrawingAttributes;
            a.Color = _pendingInk;
            stroke.DrawingAttributes = a;
        }
    }

    static Color Hex(int rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    /// <summary>Draws the score again, as native shapes built from its SVG, at the page's own size.</summary>
    void Render()
    {
        Sheet.Width = ScoreSheet.PageWidth;
        Sheet.Height = _sheet.Height;
        if (SvgScene.Parse(_sheet.Svg()) is not { } scene)
        {
            Engraving.Child = null;
            return;
        }
        var drawn = SvgXaml.Build(scene, _engraving, new FontFamily("Segoe UI"));
        drawn.Width = scene.Width;
        drawn.Height = scene.Height;
        Engraving.Child = drawn;
        UndoButton.IsEnabled = _sheet.CanUndo || Strokes.GetStrokes().Count > 0;
    }

    async Task SaveAsync()
    {
        var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(ScoreFile, CreationCollisionOption.ReplaceExisting);
        await FileIO.WriteTextAsync(file, _sheet.ToJson());
    }

    // ---------- reading the ink ----------

    /// <summary>Reads all the ink on the page; what became notation comes off it.</summary>
    async Task ReadAsync()
    {
        _readTimer.Stop();
        var strokes = Strokes.GetStrokes();
        if (strokes.Count == 0) return;
        var music = strokes.Select(s => new MusicStroke(s.Id, s.GetInkPoints().Select(p => (p.Position.X, p.Position.Y)).ToList())).ToList();
        var read = _sheet.Read(music);
        if (read.Used.Count > 0)
        {
            var used = read.Used.ToHashSet();
            foreach (var stroke in strokes) stroke.Selected = used.Contains(stroke.Id);
            Strokes.DeleteSelected();
        }
        if (read.Changed)
        {
            Render();
            if (read.Summary.Length > 0) ShowToast(read.Summary, 2.5);
            await SaveAsync();
        }
    }

    // ---------- erasing ----------
    //
    // The eraser rubs out ink and engraved notation alike, as it goes. It is handled with the page's own pointer events, whose
    // positions are the sheet's (GetCurrentPoint(Sheet) undoes the Viewbox's scaling and centring), rather than with Windows Ink's
    // unprocessed input: for that to happen, Windows Ink is given no device while the eraser button is on, and no pen while the pen
    // is turned round to its eraser end (seen as it comes near the page, before it touches). Windows Ink then leaves the input to
    // the page.

    CoreInputDeviceTypes InkDevices => EraserButton.IsChecked == true ? CoreInputDeviceTypes.None
        : _penIsEraser ? CoreInputDeviceTypes.Mouse : CoreInputDeviceTypes.Pen | CoreInputDeviceTypes.Mouse;

    void UpdateInkDevices()
    {
        var wanted = InkDevices;
        if (Ink.InkPresenter.InputDeviceTypes != wanted) Ink.InkPresenter.InputDeviceTypes = wanted;
    }

    void Eraser_Click(object sender, RoutedEventArgs e) => UpdateInkDevices();

    /// <summary>A pen near the page: turned round to its eraser end or not.</summary>
    void NoticePen(PointerPoint point)
    {
        if (point.PointerDevice.PointerDeviceType != Windows.Devices.Input.PointerDeviceType.Pen) return;
        bool eraser = point.Properties.IsEraser || point.Properties.IsInverted;
        if (eraser == _penIsEraser || _rub is not null) return;
        _penIsEraser = eraser;
        UpdateInkDevices();
    }

    bool Erases(PointerPoint point) =>
        point.PointerDevice.PointerDeviceType != Windows.Devices.Input.PointerDeviceType.Touch
        && (EraserButton.IsChecked == true || point.Properties.IsEraser || point.Properties.IsInverted);

    void Sheet_PointerPressed(object sender, PointerRoutedEventArgs e) => Guard(async () =>
    {
        var point = e.GetCurrentPoint(Sheet);
        NoticePen(point);
        if (_rub is not null || !Erases(point)) return;
        Sheet.CapturePointer(e.Pointer);
        e.Handled = true;
        _rub = new Rub { Pointer = e.Pointer.PointerId, Last = point.Position };
        await RubAsync(new List<Point> { point.Position });
    });

    void Sheet_PointerMoved(object sender, PointerRoutedEventArgs e) => Guard(async () =>
    {
        var point = e.GetCurrentPoint(Sheet);
        if (!point.IsInContact) NoticePen(point);
        if (_rub is not { } rub || rub.Pointer != e.Pointer.PointerId) return;
        e.Handled = true;
        var path = e.GetIntermediatePoints(Sheet).Reverse().Select(p => p.Position).Prepend(rub.Last).ToList();
        rub.Last = point.Position;
        await RubAsync(path);
    });

    void Sheet_PointerReleased(object sender, PointerRoutedEventArgs e) => Guard(async () =>
    {
        if (_rub is not { } rub || rub.Pointer != e.Pointer.PointerId) return;
        e.Handled = true;
        Sheet.ReleasePointerCapture(e.Pointer);
        await EndRubAsync();
        NoticePen(e.GetCurrentPoint(Sheet));
    });

    void Sheet_PointerEnded(object sender, PointerRoutedEventArgs e) => Guard(async () =>
    {
        if (_rub is { } rub && rub.Pointer == e.Pointer.PointerId) await EndRubAsync();
    });

    /// <summary>The pen gone away from the page: it writes again when it comes back, unless it comes back as an eraser.</summary>
    void Sheet_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_rub is not null || e.Pointer.PointerDeviceType != Windows.Devices.Input.PointerDeviceType.Pen || !_penIsEraser) return;
        _penIsEraser = false;
        UpdateInkDevices();
    }

    /// <summary>Rubs out what is along this part of the eraser's path: ink not read yet, and notation (one undo for the whole stroke).</summary>
    async Task RubAsync(List<Point> path)
    {
        bool inkGone = EraseInk(path);
        bool first = _rub is { Changed: false };
        if (_sheet.EraseAlong(path.Select(p => (p.X, p.Y)).ToList(), EraseReach, remember: first))
        {
            if (_rub is { } rub) rub.Changed = true;
            Render();
        }
        else if (inkGone)
        {
            UndoButton.IsEnabled = _sheet.CanUndo || Strokes.GetStrokes().Count > 0;
        }
        await Task.CompletedTask;
    }

    async Task EndRubAsync()
    {
        bool changed = _rub?.Changed == true;
        _rub = null;
        if (changed) await SaveAsync();
    }

    /// <summary>Takes off the ink not read yet that the path goes over. True when some went.</summary>
    bool EraseInk(List<Point> path)
    {
        bool any = false;
        foreach (var stroke in Strokes.GetStrokes())
        {
            stroke.Selected = stroke.GetInkPoints().Any(p => path.Any(q => Math.Abs(p.Position.X - q.X) <= EraseReach && Math.Abs(p.Position.Y - q.Y) <= EraseReach));
            any |= stroke.Selected;
        }
        if (any) Strokes.DeleteSelected();
        return any;
    }

    /// <summary>
    /// The pen's eraser end, as Windows Ink sees it: when the pen is turned round on its way to the page, Windows Ink notices before
    /// the page does, so it is told to let the pen go; and if an eraser stroke reaches it all the same (Windows Ink then rubs out the
    /// ink itself), its path, in the ink canvas's coordinates, which are the sheet's, rubs out the notation once it ends.
    /// </summary>
    void WatchPenEraser()
    {
        try
        {
            _independent = CoreInkIndependentInputSource.Create(Ink.InkPresenter);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "watching the pen's eraser end", fatal: false);
            return;
        }
        void Noticed(PointerEventArgs args)
        {
            var props = args.CurrentPoint.Properties;
            if (props.IsEraser || props.IsInverted)
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.High, () => { if (_rub is null && !_penIsEraser) { _penIsEraser = true; UpdateInkDevices(); } });
        }
        _independent.PointerEntering += (_, args) => Noticed(args);
        _independent.PointerHovering += (_, args) => Noticed(args);
        _independent.PointerPressing += (_, args) =>
        {
            Noticed(args);
            if (!args.CurrentPoint.Properties.IsEraser) return;
            lock (_penEraserLock) _penEraserPath = new List<Point> { args.CurrentPoint.Position };
        };
        _independent.PointerMoving += (_, args) =>
        {
            lock (_penEraserLock) _penEraserPath?.Add(args.CurrentPoint.Position);
        };
        _independent.PointerReleasing += (_, _) =>
        {
            List<Point>? path;
            lock (_penEraserLock) { path = _penEraserPath; _penEraserPath = null; }
            if (path is { Count: > 0 })
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => Guard(async () =>
                {
                    if (_sheet.EraseAlong(path.Select(p => (p.X, p.Y)).ToList(), EraseReach))
                    {
                        Render();
                        await SaveAsync();
                    }
                }));
        };
    }

    // ---------- the tray ----------

    void Undo_Click(object sender, RoutedEventArgs e) => Guard(UndoAsync);

    /// <summary>Takes back the last stroke not read yet, or else the last change to the score.</summary>
    async Task UndoAsync()
    {
        var strokes = Strokes.GetStrokes();
        if (strokes.Count > 0)
        {
            foreach (var stroke in strokes) stroke.Selected = false;
            strokes[strokes.Count - 1].Selected = true;
            Strokes.DeleteSelected();
            Render();
            return;
        }
        if (_sheet.Undo())
        {
            Render();
            await SaveAsync();
        }
    }

    void Clear_Click(object sender, RoutedEventArgs e) => Guard(async () =>
    {
        Strokes.Clear();
        _sheet.Clear();
        Render();
        ShowToast("Cleared. Undo brings it back.", 3);
        await SaveAsync();
    });

    void Play_Click(object sender, RoutedEventArgs e) => Guard(async () =>
    {
        if (_player is not null)
        {
            StopPlaying();
            return;
        }
        await ReadAsync(); // what was just written is part of it
        var notes = _sheet.Playback();
        if (notes.Count == 0)
        {
            ShowToast("There is nothing to play yet.", 3);
            return;
        }
        var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(ScoreAudio.Wav(notes, _sheet.Tempo).AsBuffer());
        stream.Seek(0);
        var player = new MediaPlayer { Source = MediaSource.CreateFromStream(stream, "audio/wav") };
        player.MediaEnded += (_, _) => _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => { if (_player == player) StopPlaying(); });
        player.MediaFailed += (sender, args) => _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
        {
            if (_player == player) StopPlaying();
            ShowToast("Couldn't play: " + args.ErrorMessage, 6);
        });
        _player = player;
        PlayIcon.Glyph = "";
        PlayLabel.Text = "Stop";
        player.Play();
    });

    void StopPlaying()
    {
        _player?.Pause();
        _player?.Dispose();
        _player = null;
        PlayIcon.Glyph = "";
        PlayLabel.Text = "Play";
    }

    void ShowToast(string text, double seconds)
    {
        ToastText.Text = text;
        ToastBox.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(seconds);
        _toastTimer.Start();
    }
}
