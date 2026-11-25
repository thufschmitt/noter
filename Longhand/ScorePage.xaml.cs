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
using Windows.UI.Input.Inking;
using Windows.UI.Input.Inking.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
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
    List<Point>? _eraserPath;
    // The pen's eraser end is reported on the ink thread, so its path is gathered under a lock.
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
        presenter.UnprocessedInput.PointerPressed += (_, args) => _eraserPath = new List<Point> { ToSheet(args.CurrentPoint.Position) };
        presenter.UnprocessedInput.PointerMoved += (_, args) => _eraserPath?.Add(ToSheet(args.CurrentPoint.Position));
        presenter.UnprocessedInput.PointerReleased += (_, _) => Guard(EndEraserAsync);
        presenter.UnprocessedInput.PointerLost += (_, _) => _eraserPath = null;
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

    /// <summary>A point the window reports, on the page (which the Viewbox scales).</summary>
    Point ToSheet(Point windowPoint) => Sheet.TransformToVisual(Window.Current.Content).Inverse.TransformPoint(windowPoint);

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

    void Eraser_Click(object sender, RoutedEventArgs e)
    {
        // With the eraser on, the pen's input comes to the page unprocessed, as a path that rubs out ink and notation alike.
        Ink.InkPresenter.InputProcessingConfiguration.Mode = EraserButton.IsChecked == true ? InkInputProcessingMode.None : InkInputProcessingMode.Inking;
    }

    async Task EndEraserAsync()
    {
        var path = _eraserPath;
        _eraserPath = null;
        if (path is { Count: > 0 }) await EraseAlongAsync(path, eraseInk: true);
    }

    async Task EraseAlongAsync(List<Point> path, bool eraseInk)
    {
        if (eraseInk)
        {
            bool any = false;
            foreach (var stroke in Strokes.GetStrokes())
            {
                stroke.Selected = stroke.GetInkPoints().Any(p => path.Any(q => Math.Abs(p.Position.X - q.X) <= EraseReach && Math.Abs(p.Position.Y - q.Y) <= EraseReach));
                any |= stroke.Selected;
            }
            if (any) Strokes.DeleteSelected();
        }
        if (_sheet.EraseAlong(path.Select(p => (p.X, p.Y)).ToList(), EraseReach))
        {
            Render();
            await SaveAsync();
        }
    }

    /// <summary>
    /// The pen's eraser end: Windows rubs out the ink itself, and its path, reported on the ink thread, rubs out the notation too.
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
        _independent.PointerPressing += (_, args) =>
        {
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
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => Guard(() => EraseAlongAsync(path.Select(ToSheet).ToList(), eraseInk: false)));
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
