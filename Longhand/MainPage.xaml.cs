using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Anthropic.Exceptions;
using Longhand.Services;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Input.Inking;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Shapes;

namespace Longhand;

public sealed partial class MainPage : Page
{
    // Page geometry, in XAML pixels.
    const double PageMaxWidth = 880, LineHeight = 40, Top = 120, MarginX = 76, RightPad = 36, FontPx = 26;
    // Nudges Claude's text so its baseline sits on the ruled line; tune by eye if your font renders differently.
    const double TextNudge = 3;
    static readonly TimeSpan PauseBeforeAsking = TimeSpan.FromMilliseconds(2200);

    const string HintText = "Write anything with your pen: a question, a sum, a sketch. Pause for a moment or tap Ask, and I'll reply right here on the page.";

    sealed class Note
    {
        public required TextBlock Block;
        public required Rectangle Tick;
        public double Y;
        public bool IsHint;
        public bool Done;
        public string Heard = "";
        public string Text = "";
        public int Lines = 1;
        /// <summary>Its lines plus one blank rule below as breathing room.</summary>
        public double Span => (Lines + 1) * LineHeight;
        public double Bottom => Y + Span;
    }

    abstract record UndoAction;
    sealed record StrokeAdded(uint StrokeId) : UndoAction;
    sealed record StrokesErased(List<InkStroke> Copies) : UndoAction;

    readonly List<Note> _notes = new();
    readonly HashSet<uint> _pending = new();       // strokes written since Claude last answered
    readonly Stack<UndoAction> _undo = new();
    readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _clearTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    DateTime _idleStart;
    bool _busy, _clearArmed, _loaded;
    CancellationTokenSource? _cts;
    double _sheetWidth = 800, _rulesDrawnTo, _rulesWidth;

    // Shared brushes: changing their Color on a theme switch recolours everything that uses them.
    readonly SolidColorBrush _desk = new(), _paper = new(), _rule = new(), _margin = new(), _claudeInk = new(),
        _muted = new(), _tray = new(), _trayLine = new(), _uiText = new();
    readonly FontFamily _handFont = new("Ink Free, Segoe Print, Comic Sans MS");

    InkStrokeContainer Strokes => Ink.InkPresenter.StrokeContainer;
    double TextWidth => Math.Max(200, _sheetWidth - MarginX - RightPad - 8);

    public MainPage()
    {
        InitializeComponent();

        var presenter = Ink.InkPresenter;
        presenter.InputDeviceTypes = CoreInputDeviceTypes.Pen | CoreInputDeviceTypes.Mouse;
        presenter.StrokesCollected += OnStrokesCollected;
        presenter.StrokesErased += OnStrokesErased;
        presenter.StrokeInput.StrokeStarted += (_, _) => CancelIdle();

        _idleTimer.Tick += OnIdleTick;
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastBox.Visibility = Visibility.Collapsed; };
        _saveTimer.Tick += async (_, _) => { _saveTimer.Stop(); await SaveAsync(); };
        _clearTimer.Tick += (_, _) => DisarmClear();

        Root.Background = _desk;
        Sheet.Background = _paper;
        Tray.Background = _tray;
        Tray.BorderBrush = _trayLine;
        ToastBox.Background = _tray;
        ToastBox.BorderBrush = _trayLine;
        ToastText.Foreground = _uiText;
        TraySeparator.Fill = _trayLine;

        ActualThemeChanged += (_, _) => ApplyTheme(recolorInk: true);
        Loaded += OnLoaded;
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyTheme(recolorInk: false);
        var saved = await PageStore.LoadAsync(Strokes);
        if (saved is { Count: > 0 })
            foreach (var n in saved) AddNote(n.Y, n.Text, n.IsHint, done: true).Heard = n.Heard;
        else if (Strokes.GetStrokes().Count == 0)
            AddNote(Top, HintText, isHint: true, done: true);
        ApplyTheme(recolorInk: true); // loaded strokes take the current theme's ink colour
        _loaded = true;
        LayoutSheet();
        if (ApiKeyStore.Load() is null)
            ShowToast("Add your Anthropic API key (gear button) so Claude can answer. You can write in the meantime.", 6);
    }

    // ---------- theme ----------

    void ApplyTheme(bool recolorInk)
    {
        bool dark = ActualTheme == ElementTheme.Dark;
        _desk.Color = dark ? Hex(0x0D0F13) : Hex(0xE9EDF2);
        _paper.Color = dark ? Hex(0x16191F) : Hex(0xFBFCFD);
        _rule.Color = dark ? Hex(0x252B35) : Hex(0xD9E3EE);
        _margin.Color = dark ? Hex(0x4A2A2E) : Hex(0xF0C9C9);
        _claudeInk.Color = dark ? Hex(0x93A9FF) : Hex(0x2C4FC4);
        _muted.Color = dark ? Hex(0x8D96A6) : Hex(0x7A8494);
        _tray.Color = dark ? Color.FromArgb(0xEB, 0x1E, 0x22, 0x2A) : Color.FromArgb(0xEB, 0xFF, 0xFF, 0xFF);
        _trayLine.Color = dark ? Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1F, 0x1E, 0x28, 0x3C);
        _uiText.Color = dark ? Hex(0xE6E8EC) : Hex(0x2A2F37);

        var ink = dark ? Hex(0xE6E8EC) : Hex(0x22262C);
        var attrs = new InkDrawingAttributes
        {
            Color = ink,
            Size = new Size(2.4, 2.4),
            FitToCurve = true,
            IgnorePressure = false,
            PenTip = PenTipShape.Circle,
        };
        Ink.InkPresenter.UpdateDefaultDrawingAttributes(attrs);
        if (recolorInk)
        {
            foreach (var stroke in Strokes.GetStrokes())
            {
                var a = stroke.DrawingAttributes;
                a.Color = ink;
                stroke.DrawingAttributes = a;
            }
        }
    }

    static Color Hex(int rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    // ---------- page layout ----------

    void Scroller_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutSheet();

    void LayoutSheet()
    {
        _sheetWidth = Math.Min(Scroller.ActualWidth, PageMaxWidth);
        if (_sheetWidth <= 0) return;
        Sheet.Width = _sheetWidth;
        foreach (var n in _notes)
        {
            n.Block.Width = TextWidth;
            Remeasure(n);
            Position(n);
        }
        UpdateSheetHeight();
    }

    double ContentBottom()
    {
        double bottom = Top;
        var ink = Strokes.BoundingRect;
        if (ink.Width > 0 && ink.Height > 0) bottom = Math.Max(bottom, ink.Y + ink.Height);
        foreach (var n in _notes) bottom = Math.Max(bottom, n.Bottom);
        return bottom;
    }

    /// <summary>The sheet always runs well past the last ink, so there are fresh lines to write on.</summary>
    void UpdateSheetHeight()
    {
        double viewport = Math.Max(Scroller.ViewportHeight, 400);
        double height = Math.Max(viewport, ContentBottom() + viewport * 0.75);
        Sheet.Height = height;
        DrawRules(height);
    }

    void DrawRules(double height)
    {
        if (_rulesWidth != _sheetWidth)
        {
            RuleLayer.Children.Clear();
            _rulesDrawnTo = 0;
            _rulesWidth = _sheetWidth;

            var margin = new Rectangle { Width = 1.5, Fill = _margin, Height = 100000 };
            Canvas.SetLeft(margin, MarginX - 16);
            RuleLayer.Children.Add(margin);

            var title = new TextBlock { Text = "LONGHAND", FontSize = 13, FontWeight = Windows.UI.Text.FontWeights.SemiBold, Foreground = _muted, CharacterSpacing = 60 };
            Canvas.SetLeft(title, MarginX);
            Canvas.SetTop(title, 48);
            RuleLayer.Children.Add(title);

            var date = new TextBlock { Text = DateTime.Now.ToString("MMM d, yyyy").ToUpperInvariant(), FontSize = 13, FontWeight = Windows.UI.Text.FontWeights.SemiBold, Foreground = _muted, Width = 200, TextAlignment = TextAlignment.Right };
            Canvas.SetLeft(date, _sheetWidth - RightPad - 200);
            Canvas.SetTop(date, 48);
            RuleLayer.Children.Add(date);
        }
        double y = _rulesDrawnTo == 0 ? Top : _rulesDrawnTo + LineHeight;
        for (; y <= height; y += LineHeight)
        {
            RuleLayer.Children.Add(new Line { X1 = 0, X2 = _sheetWidth, Y1 = y, Y2 = y, Stroke = _rule, StrokeThickness = 1 });
            _rulesDrawnTo = y;
        }
    }

    static double RuleAtOrBelow(double y) => Top + Math.Max(0, Math.Ceiling((y - Top) / LineHeight)) * LineHeight;

    // ---------- Claude's notes ----------

    Note AddNote(double y, string text, bool isHint, bool done)
    {
        var block = new TextBlock
        {
            Text = text,
            FontFamily = _handFont,
            FontSize = FontPx,
            LineHeight = LineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextWrapping = TextWrapping.WrapWholeWords,
            Foreground = _claudeInk,
            Width = TextWidth,
            Opacity = isHint ? 0.8 : 1,
            IsTextScaleFactorEnabled = false,
        };
        var tick = new Rectangle { Width = 2.5, RadiusX = 1.25, RadiusY = 1.25, Fill = _claudeInk, Opacity = isHint ? 0.35 : 0.6 };
        Canvas.SetLeft(block, MarginX);
        Canvas.SetLeft(tick, MarginX - 31);
        NoteLayer.Children.Add(tick);
        NoteLayer.Children.Add(block);

        var note = new Note { Block = block, Tick = tick, Y = y, IsHint = isHint, Done = done, Text = text };
        Remeasure(note);
        Position(note);
        _notes.Add(note);
        return note;
    }

    void Remeasure(Note n)
    {
        n.Block.Measure(new Size(TextWidth, double.PositiveInfinity));
        n.Lines = Math.Max(1, (int)Math.Round(n.Block.DesiredSize.Height / LineHeight));
    }

    void Position(Note n)
    {
        Canvas.SetTop(n.Block, n.Y + TextNudge);
        Canvas.SetTop(n.Tick, n.Y + LineHeight * 0.35);
        n.Tick.Height = Math.Max(4, n.Lines * LineHeight - LineHeight * 0.35 + 4);
    }

    void RemoveNote(Note n)
    {
        NoteLayer.Children.Remove(n.Block);
        NoteLayer.Children.Remove(n.Tick);
        _notes.Remove(n);
    }

    /// <summary>Pushes everything below <paramref name="anchor"/> down (or up) to make room for an answer.</summary>
    void ShiftBelow(double anchor, double delta, Note? except, HashSet<uint>? keepStrokes)
    {
        if (delta == 0) return;
        bool any = false;
        foreach (var s in Strokes.GetStrokes())
        {
            bool move = !(keepStrokes?.Contains(s.Id) ?? false) && s.BoundingRect.Y >= anchor;
            s.Selected = move;
            any |= move;
        }
        if (any)
        {
            Strokes.MoveSelected(new Point(0, delta));
            foreach (var s in Strokes.GetStrokes()) s.Selected = false;
        }
        foreach (var n in _notes)
        {
            if (n == except || n.Y < anchor) continue;
            n.Y += delta;
            Position(n);
        }
        UpdateSheetHeight();
    }

    IReadOnlyList<Turn> History() => _notes
        .Where(n => !n.IsHint && n.Done && n.Text.Length > 0)
        .OrderBy(n => n.Y)
        .Select(n => new Turn(n.Heard.Length > 0 ? n.Heard : "(unclear handwriting)", n.Text))
        .ToList();

    void EnsureVisible(double bottom)
    {
        double visibleBottom = Scroller.VerticalOffset + Scroller.ViewportHeight - 90;
        if (bottom > visibleBottom) Scroller.ChangeView(null, bottom - Scroller.ViewportHeight + 90, null);
    }

    // ---------- ink events ----------

    void OnStrokesCollected(InkPresenter sender, InkStrokesCollectedEventArgs args)
    {
        foreach (var s in args.Strokes)
        {
            _pending.Add(s.Id);
            _undo.Push(new StrokeAdded(s.Id));
        }
        UpdateSheetHeight();
        UpdateAsk();
        ScheduleIdle();
        ScheduleSave();
    }

    void OnStrokesErased(InkPresenter sender, InkStrokesErasedEventArgs args)
    {
        var copies = new List<InkStroke>();
        foreach (var s in args.Strokes)
        {
            _pending.Remove(s.Id);
            copies.Add(s.Clone());
        }
        if (copies.Count > 0) _undo.Push(new StrokesErased(copies));
        UpdateAsk();
        ScheduleIdle();
        ScheduleSave();
    }

    // ---------- toolbar ----------

    void Pen_Click(object sender, RoutedEventArgs e) => SetErasing(false);
    void Eraser_Click(object sender, RoutedEventArgs e) => SetErasing(true);

    void SetErasing(bool erasing)
    {
        Ink.InkPresenter.InputProcessingConfiguration.Mode = erasing ? InkInputProcessingMode.Erasing : InkInputProcessingMode.Inking;
        PenButton.IsChecked = !erasing;
        EraserButton.IsChecked = erasing;
    }

    void Touch_Click(object sender, RoutedEventArgs e)
    {
        bool on = TouchButton.IsChecked == true;
        var types = CoreInputDeviceTypes.Pen | CoreInputDeviceTypes.Mouse;
        Ink.InkPresenter.InputDeviceTypes = on ? types | CoreInputDeviceTypes.Touch : types;
        ShowToast(on ? "Finger writing is on. Scroll with the scroll bar or mouse wheel." : "Fingers scroll the page again. Write with a pen or mouse.");
    }

    void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_undo.Count == 0) return;
        switch (_undo.Pop())
        {
            case StrokeAdded added:
                bool found = false;
                foreach (var s in Strokes.GetStrokes())
                {
                    s.Selected = s.Id == added.StrokeId;
                    found |= s.Selected;
                }
                if (found) Strokes.DeleteSelected();
                _pending.Remove(added.StrokeId);
                break;
            case StrokesErased erased:
                foreach (var copy in erased.Copies) Strokes.AddStroke(copy);
                break;
        }
        UpdateAsk();
        ScheduleSave();
    }

    void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (!_clearArmed)
        {
            _clearArmed = true;
            ToolTipService.SetToolTip(ClearButton, "Click again to clear the whole page");
            ShowToast("Click the bin again to clear the whole page.", 2.5);
            _clearTimer.Start();
            return;
        }
        DisarmClear();
        _cts?.Cancel();
        CancelIdle();
        Strokes.Clear();
        foreach (var n in _notes.ToList()) RemoveNote(n);
        _pending.Clear();
        _undo.Clear();
        AddNote(Top, HintText, isHint: true, done: true);
        Scroller.ChangeView(null, 0, null);
        UpdateSheetHeight();
        UpdateAsk();
        ScheduleSave();
    }

    void DisarmClear()
    {
        _clearTimer.Stop();
        _clearArmed = false;
        ToolTipService.SetToolTip(ClearButton, "Clear page");
    }

    async void Settings_Click(object sender, RoutedEventArgs e) => await PromptForKeyAsync();

    async void Ask_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) _cts?.Cancel();
        else await AskAsync();
    }

    void UpdateAsk()
    {
        AskLabel.Text = _busy ? "Stop" : "Ask";
        BusyRing.IsActive = _busy;
        AskIcon.Visibility = _busy || _idleTimer.IsEnabled ? Visibility.Collapsed : Visibility.Visible;
        AskButton.IsEnabled = _busy || _pending.Count > 0;
    }

    // ---------- pause-to-ask ----------

    void ScheduleIdle()
    {
        CancelIdle();
        if (_pending.Count == 0 || _busy || ApiKeyStore.Load() is null) return;
        _idleStart = DateTime.UtcNow;
        Countdown.Value = 0;
        Countdown.Visibility = Visibility.Visible;
        _idleTimer.Start();
        UpdateAsk();
    }

    void CancelIdle()
    {
        _idleTimer.Stop();
        Countdown.Visibility = Visibility.Collapsed;
        UpdateAsk();
    }

    async void OnIdleTick(object? sender, object e)
    {
        double progress = (DateTime.UtcNow - _idleStart).TotalMilliseconds / PauseBeforeAsking.TotalMilliseconds;
        Countdown.Value = Math.Min(1, progress);
        if (progress < 1) return;
        CancelIdle();
        await AskAsync();
    }

    // ---------- asking Claude ----------

    async Task AskAsync()
    {
        CancelIdle();
        if (_busy || _pending.Count == 0) return;

        var key = ApiKeyStore.Load() ?? await PromptForKeyAsync();
        if (key is null)
        {
            ShowToast("Add your Anthropic API key (gear button) to get answers.");
            return;
        }

        var ids = new HashSet<uint>(_pending);
        var strokes = Strokes.GetStrokes().Where(s => ids.Contains(s.Id)).ToList();
        if (strokes.Count == 0)
        {
            _pending.Clear();
            UpdateAsk();
            return;
        }

        // Writing over earlier replies (annotating them) sends those replies along, so Claude sees what the ink refers to.
        var context = ContextAround(strokes, ids);

        byte[] png;
        try
        {
            try
            {
                png = await InkSnapshot.RenderPngAsync(strokes, context);
            }
            catch (Exception) when (context is not null)
            {
                // The earlier replies are a bonus: rather than not asking, send the new ink alone.
                context = null;
                ShowToast("Claude will only see your new ink this time.");
                png = await InkSnapshot.RenderPngAsync(strokes);
            }
        }
        catch (Exception ex)
        {
            ShowToast("Couldn't capture the handwriting: " + ex.Message);
            return;
        }
        _pending.Clear();

        // The answer goes at the end of the page, so it never lands in the middle of what is already there. Anything the user
        // writes below it while it streams is pushed down out of the way.
        var note = AddNote(EndOfPage(), "…", isHint: false, done: false);
        note.Text = "";
        double occupied = note.Span;
        ShiftBelow(note.Y, occupied, note, ids);
        EnsureVisible(note.Bottom);

        void Grow()
        {
            Remeasure(note);
            Position(note);
            if (note.Span != occupied)
            {
                ShiftBelow(note.Y + occupied, note.Span - occupied, note, ids);
                occupied = note.Span;
            }
            EnsureVisible(note.Bottom);
        }

        bool answered = false;
        _busy = true;
        _cts = new CancellationTokenSource();
        UpdateAsk();
        try
        {
            // Connected apps whose sign-in can't be renewed are skipped; say so, since Claude won't have them.
            var (connectors, expired) = await ConnectorStore.ActiveAsync(_oauth, _cts.Token);
            if (expired.Count > 0) ShowToast("Sign in again to " + string.Join(", ", expired) + " (apps button).", 6);

            var result = await new ClaudeService(key, connectors).AskAsync(png, History(), progress =>
            {
                note.Heard = progress.Heard;
                note.Text = progress.Body;
                note.Block.Text = progress.Body.Length > 0 ? progress.Body : "…";
                Grow();
            }, _cts.Token, status =>
            {
                // A tool is running: show what it is doing in place of the reply, which is written after it finishes.
                note.Text = "";
                note.Block.Text = status;
                Grow();
            }, hasContext: context is not null);

            note.Heard = result.Heard;
            note.Text = result.Outcome switch
            {
                ReplyOutcome.Refused when result.Body.Length == 0 => "I can't help with that one.",
                ReplyOutcome.Truncated => result.Body + " …",
                _ => result.Body.Length > 0 ? result.Body : "I couldn't come up with a reply. Try writing it again?",
            };
            note.Done = true;
            note.Block.Text = note.Text;
            Grow();
            answered = true;
        }
        catch (OperationCanceledException)
        {
            if (note.Text.Length > 0) FinishPartial(note, Grow);
            else Discard(note, occupied, ids);
        }
        catch (AnthropicUnauthorizedException)
        {
            Discard(note, occupied, ids);
            ShowToast("Anthropic rejected the API key. Check it with the gear button, then tap Ask.", 6);
        }
        catch (AnthropicRateLimitException)
        {
            Discard(note, occupied, ids);
            ShowToast("Too many requests right now. Wait a moment, then tap Ask.", 6);
        }
        catch (Exception ex)
        {
            Discard(note, occupied, ids);
            ShowToast("Claude couldn't answer: " + ex.Message + " Tap Ask to try again.", 7);
        }
        finally
        {
            _busy = false;
            _cts?.Dispose();
            _cts = null;
            UpdateAsk();
            ScheduleSave();
            // Only re-arm the pause timer after a good answer, so a failing request is never retried in a loop.
            if (answered && _pending.Count > 0) ScheduleIdle();
        }
    }

    /// <summary>The first free rule below all the ink and all the replies.</summary>
    double EndOfPage()
    {
        double ink = Strokes.GetStrokes().Select(s => s.BoundingRect.Y + s.BoundingRect.Height).DefaultIfEmpty(Top).Max() - LineHeight * 0.3;
        double notes = _notes.Where(n => !n.IsHint).Select(n => n.Bottom).DefaultIfEmpty(Top).Max();
        return RuleAtOrBelow(Math.Max(ink, notes));
    }

    /// <summary>
    /// When the new ink overlaps earlier replies, what Claude should be shown of the page around it: those replies, and the
    /// writing they answered. Null when the ink simply follows the page.
    /// </summary>
    PageContext? ContextAround(List<InkStroke> strokes, HashSet<uint> ids)
    {
        var replies = _notes.Where(n => !n.IsHint && n.Done && n.Text.Length > 0).OrderBy(n => n.Y).ToList();
        var band = AnnotationContext.Find(
            strokes.Min(s => s.BoundingRect.Y),
            strokes.Max(s => s.BoundingRect.Y + s.BoundingRect.Height),
            replies.Select(n => (n.Y, n.Y + n.Lines * LineHeight)).ToList(),
            Top);
        if (band is not { } found) return null;

        var earlier = Strokes.GetStrokes()
            .Where(s => !ids.Contains(s.Id) && s.BoundingRect.Y < found.To && s.BoundingRect.Y + s.BoundingRect.Height > found.From)
            .ToList();
        var shown = found.Replies
            .Select(i => replies[i])
            .Select(n => new PageReply(new Rect(MarginX, n.Y + TextNudge, n.Block.ActualWidth, n.Block.ActualHeight), scale => RenderBlockAsync(n.Block, scale)))
            .ToList();
        return new PageContext(earlier, shown);
    }

    static async Task<RenderedText> RenderBlockAsync(TextBlock block, double scale)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(block, (int)Math.Ceiling(block.ActualWidth * scale), (int)Math.Ceiling(block.ActualHeight * scale));
        var pixels = await bitmap.GetPixelsAsync();
        return new RenderedText(pixels.ToArray(), bitmap.PixelWidth, bitmap.PixelHeight);
    }

    static void FinishPartial(Note note, Action grow)
    {
        note.Text += " …";
        note.Block.Text = note.Text;
        note.Done = true;
        grow();
    }

    /// <summary>Removes an answer that never arrived and gives the writing back so it can be asked again.</summary>
    void Discard(Note note, double occupied, HashSet<uint> ids)
    {
        RemoveNote(note);
        ShiftBelow(note.Y + occupied, -occupied, null, ids);
        foreach (var s in Strokes.GetStrokes()) if (ids.Contains(s.Id)) _pending.Add(s.Id);
    }

    // ---------- connected apps ----------

    static readonly (string Name, string Url)[] AppPresets =
    {
        ("todoist", "https://ai.todoist.net/mcp"),
    };

    readonly McpOAuth _oauth = new();

    async void Apps_Click(object sender, RoutedEventArgs e) => await ShowAppsAsync();

    async Task ShowAppsAsync()
    {
        using var cts = new CancellationTokenSource();
        var list = new StackPanel { Spacing = 10 };
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 380, Opacity = 0.75 };

        var presets = new ComboBox { PlaceholderText = "Pick a service, or fill in the two boxes", Width = 380, ItemsSource = AppPresets.Select(p => p.Name).ToList() };
        var nameBox = new TextBox { PlaceholderText = "Name", Width = 380 };
        var urlBox = new TextBox { PlaceholderText = "https://server/mcp", Width = 380 };
        presets.SelectionChanged += (_, _) =>
        {
            if (presets.SelectedIndex < 0) return;
            var preset = AppPresets[presets.SelectedIndex];
            nameBox.Text = preset.Name;
            urlBox.Text = preset.Url;
        };
        var add = new Button { Content = "Add" };

        void Render()
        {
            list.Children.Clear();
            var apps = ConnectorStore.List();
            if (apps.Count == 0)
                list.Children.Add(new TextBlock { Text = "No apps yet. Add one below, then sign in.", Opacity = 0.75 });
            foreach (var app in apps)
            {
                var row = new Grid { ColumnSpacing = 8, Width = 380 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                label.Children.Add(new TextBlock { Text = app.Name, FontWeight = Windows.UI.Text.FontWeights.SemiBold });
                label.Children.Add(new TextBlock { Text = app.SignedIn ? "Signed in" : "Not signed in", FontSize = 12, Opacity = 0.75 });

                var action = new Button { Content = app.SignedIn ? "Sign out" : "Sign in" };
                action.Click += async (_, _) =>
                {
                    if (app.SignedIn)
                    {
                        ConnectorStore.SignOut(app.Name);
                        Render();
                        return;
                    }
                    action.IsEnabled = false;
                    message.Text = $"Finish signing in to {app.Name} in your browser. This page continues when you come back.";
                    try
                    {
                        await SignInAsync(app, cts.Token);
                        message.Text = "";
                    }
                    catch (OperationCanceledException)
                    {
                        message.Text = "";
                    }
                    catch (Exception ex)
                    {
                        message.Text = $"Couldn't sign in to {app.Name}: {ex.Message}";
                    }
                    Render();
                };

                var remove = new Button { Content = new FontIcon { Glyph = "\uE74D", FontSize = 14 } };
                ToolTipService.SetToolTip(remove, "Remove");
                remove.Click += (_, _) =>
                {
                    ConnectorStore.Remove(app.Name);
                    Render();
                };

                Grid.SetColumn(action, 1);
                Grid.SetColumn(remove, 2);
                row.Children.Add(label);
                row.Children.Add(action);
                row.Children.Add(remove);
                list.Children.Add(row);
            }
        }

        add.Click += (_, _) =>
        {
            // Names go to the API as server names, so keep them to letters, digits and dashes.
            var name = System.Text.RegularExpressions.Regex.Replace(nameBox.Text.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            var url = urlBox.Text.Trim();
            if (name.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                message.Text = "An app needs a name and an https address.";
                return;
            }
            ConnectorStore.Add(name, url);
            nameBox.Text = urlBox.Text = "";
            presets.SelectedIndex = -1;
            message.Text = "";
            Render();
        };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "Claude can always search the web. Connected apps let it use your own services too. Sign in once in your browser; Longhand keeps the sign-in on this PC.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
        });
        panel.Children.Add(list);
        panel.Children.Add(message);
        panel.Children.Add(presets);
        panel.Children.Add(nameBox);
        panel.Children.Add(urlBox);
        panel.Children.Add(add);
        Render();

        var dialog = new ContentDialog
        {
            Title = "Connected apps",
            Content = new ScrollViewer { Content = panel, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            CloseButtonText = "Done",
        };
        await dialog.ShowAsync();
        cts.Cancel();
    }

    async Task SignInAsync(ConnectedApp app, CancellationToken ct)
    {
        var request = await _oauth.BeginAsync(app.Url, McpOAuth.RedirectUri, ct);
        var query = await OAuthBroker.AuthorizeAsync(request.AuthorizeUrl, request.State, ct);
        ConnectorStore.SaveSession(app.Name, await _oauth.FinishAsync(request, query, ct));
    }

    async Task<string?> PromptForKeyAsync()
    {
        bool hasKey = ApiKeyStore.Load() is not null;
        var box = new PasswordBox { PlaceholderText = hasKey ? "A key is saved. Paste a new one to replace it." : "sk-ant-…", Width = 380 };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "Longhand sends your handwriting to Claude using your own Anthropic API key. The key is stored in Windows Credential Manager on this PC.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
        });
        panel.Children.Add(box);

        var dialog = new ContentDialog
        {
            Title = "Anthropic API key",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (hasKey) dialog.SecondaryButtonText = "Remove key";

        var choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Password))
        {
            var key = box.Password.Trim();
            ApiKeyStore.Save(key);
            ShowToast("API key saved.");
            ScheduleIdle();
            return key;
        }
        if (choice == ContentDialogResult.Secondary)
        {
            ApiKeyStore.Clear();
            ShowToast("API key removed.");
        }
        return ApiKeyStore.Load();
    }

    // ---------- toast + saving ----------

    void ShowToast(string message, double seconds = 3.5)
    {
        ToastText.Text = message;
        ToastBox.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(seconds);
        _toastTimer.Start();
    }

    void ScheduleSave()
    {
        if (!_loaded) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    async Task SaveAsync()
    {
        try
        {
            var notes = _notes.Where(n => n.Done).Select(n => new SavedNote(n.Y, n.Text, n.Heard, n.IsHint)).ToList();
            await PageStore.SaveAsync(Strokes, notes);
        }
        catch (Exception ex)
        {
            ShowToast("Couldn't save the page: " + ex.Message);
        }
    }
}
