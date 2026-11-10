using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Anthropic.Exceptions;
using Longhand.Services;
using Windows.Foundation;
using Windows.Storage.Streams;
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

    /// <summary>One of Claude's SVG drawings, shown under the words of its note.</summary>
    sealed class Drawing
    {
        public required string Markup;
        public required Image Image;
        public required double Aspect;
        public string Alt = "";
        /// <summary>The rule lines it takes up.</summary>
        public int Lines;
    }

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
        public readonly List<Drawing> Drawings = new();
        public int DrawingsSeen; // how many of the reply's drawings have been taken in (shown or refused)
        public int DrawLines => Drawings.Sum(d => d.Lines);
        /// <summary>Its text lines and drawings plus one blank rule below as breathing room.</summary>
        public double Span => (Lines + DrawLines + 1) * LineHeight;
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
    DateTime _idleStart;
    bool _busy, _loaded, _dirty, _switching, _discardOnCancel;
    ConversationIndex _index = new();
    string _currentId = "";
    readonly SemaphoreSlim _saveLock = new(1, 1);
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
        _saveTimer.Tick += async (_, _) => { _saveTimer.Stop(); if (!_switching) await SaveAsync(); };
        BuildConversationsFlyout();

        var newConversation = new Windows.UI.Xaml.Input.KeyboardAccelerator
        {
            Key = Windows.System.VirtualKey.N,
            Modifiers = Windows.System.VirtualKeyModifiers.Control,
        };
        newConversation.Invoked += async (_, args) =>
        {
            args.Handled = true;
            await NewConversationAsync();
        };
        KeyboardAccelerators.Add(newConversation);

        Root.Background = _desk;
        Sheet.Background = _paper;
        Tray.Background = _tray;
        Tray.BorderBrush = _trayLine;
        ToastBox.Background = _tray;
        ToastBox.BorderBrush = _trayLine;
        ToastText.Foreground = _uiText;
        TraySeparator.Fill = _trayLine;

        ActualThemeChanged += (_, _) =>
        {
            ApplyTheme(recolorInk: true);
            RefreshDrawings();
        };
        Loaded += OnLoaded;
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyTheme(recolorInk: false);
        _index = await ConversationStore.LoadAsync();
        var start = _index.Find(_index.CurrentId) ?? _index.Newest().FirstOrDefault() ?? _index.Create(DateTimeOffset.UtcNow);
        await ShowConversationAsync(start.Id);
        await ConversationStore.SaveAsync(_index);
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
        // A reply that is only a drawing has no line of words above it.
        int least = n.Drawings.Count > 0 && n.Block.Text.Length == 0 ? 0 : 1;
        n.Lines = Math.Max(least, (int)Math.Round(n.Block.DesiredSize.Height / LineHeight));
        foreach (var d in n.Drawings)
        {
            var (w, h) = Svg.Fit(d.Aspect, Math.Min(TextWidth, 560), 480);
            d.Image.Width = w;
            d.Image.Height = h;
            d.Lines = (int)Math.Ceiling(h / LineHeight);
        }
    }

    void Position(Note n)
    {
        Canvas.SetTop(n.Block, n.Y + TextNudge);
        Canvas.SetTop(n.Tick, n.Y + LineHeight * 0.35);
        n.Tick.Height = Math.Max(4, (n.Lines + n.DrawLines) * LineHeight - LineHeight * 0.35 + 4);
        double top = n.Y + n.Lines * LineHeight;
        foreach (var d in n.Drawings)
        {
            Canvas.SetLeft(d.Image, MarginX);
            Canvas.SetTop(d.Image, top);
            top += d.Lines * LineHeight;
        }
    }

    void RemoveNote(Note n)
    {
        NoteLayer.Children.Remove(n.Block);
        NoteLayer.Children.Remove(n.Tick);
        foreach (var d in n.Drawings) NoteLayer.Children.Remove(d.Image);
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
        .Where(n => !n.IsHint && n.Done && (n.Text.Length > 0 || n.Drawings.Count > 0))
        .OrderBy(n => n.Y)
        .Select(n => new Turn(n.Heard.Length > 0 ? n.Heard : "(unclear handwriting)", (n.Text + string.Concat(n.Drawings.Select(d => " [I drew: " + d.Alt + "]"))).Trim()))
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

    // ---------- drawings ----------

    /// <summary>Adds one of Claude's SVG drawings under the note's words. False when the drawing isn't safe or sensible to show.</summary>
    bool AddDrawing(Note note, string raw)
    {
        if (note.Drawings.Count >= Svg.MaxDrawings || Svg.Sanitize(raw) is not { } svg) return false;
        var title = Svg.Title(svg);
        var image = new Image { Stretch = Stretch.Uniform };
        Windows.UI.Xaml.Automation.AutomationProperties.SetName(image, title);
        var drawing = new Drawing { Markup = svg, Image = image, Alt = title, Aspect = Svg.Aspect(svg) };
        note.Drawings.Add(drawing);
        NoteLayer.Children.Add(image);
        _ = LoadDrawingAsync(drawing);
        return true;
    }

    /// <summary>Takes in the drawings of a reply that haven't been added yet.</summary>
    void AddNewDrawings(Note note, IReadOnlyList<string>? drawings)
    {
        if (drawings is null) return;
        for (; note.DrawingsSeen < drawings.Count; note.DrawingsSeen++)
            if (!AddDrawing(note, drawings[note.DrawingsSeen])) ShowToast("A drawing from Claude couldn't be shown.");
        Remeasure(note);
        Position(note);
    }

    void ClearDrawings(Note note)
    {
        foreach (var d in note.Drawings) NoteLayer.Children.Remove(d.Image);
        note.Drawings.Clear();
        note.DrawingsSeen = 0;
    }

    static string Hex6(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Renders the drawing in the current ink colour: drawings say "currentColor" for it, so they follow the theme.</summary>
    async Task LoadDrawingAsync(Drawing d)
    {
        try
        {
            var source = new SvgImageSource { RasterizePixelWidth = 1200, RasterizePixelHeight = 1200 / d.Aspect };
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteString(d.Markup.Replace("currentColor", Hex6(_claudeInk.Color)));
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var status = await source.SetSourceAsync(stream);
            if (status == SvgImageSourceLoadStatus.Success) d.Image.Source = source;
            else ShowToast("A drawing couldn't be shown.");
        }
        catch (Exception)
        {
            ShowToast("A drawing couldn't be shown.");
        }
    }

    void RefreshDrawings()
    {
        foreach (var n in _notes)
            foreach (var d in n.Drawings) _ = LoadDrawingAsync(d);
    }

    // ---------- tools the app runs for Claude ----------

    readonly Nextcloud _nextcloud = new();

    /// <summary>The calendar is offered to Claude only while a Nextcloud account is connected.</summary>
    IReadOnlyList<IAgentTool> BuildAgentTools() =>
        NextcloudStore.Load() is { } account ? new IAgentTool[] { new CalendarTool(_nextcloud, account) } : Array.Empty<IAgentTool>();

    // ---------- conversations ----------

    readonly Flyout _conversationsFlyout = new() { Placement = Windows.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top };
    readonly StackPanel _conversationList = new() { Spacing = 2, Width = 340 };
    string? _armedDelete, _renaming;

    bool IsBlankNow() => Strokes.GetStrokes().Count == 0 && !_notes.Any(n => !n.IsHint);

    /// <summary>Empties the canvas. What was on it must have been saved already.</summary>
    void ResetCanvas()
    {
        CancelIdle();
        Strokes.Clear();
        foreach (var n in _notes.ToList()) RemoveNote(n);
        _pending.Clear();
        _undo.Clear();
    }

    /// <summary>Loads a conversation's page onto the canvas and makes it the open one.</summary>
    async Task ShowConversationAsync(string id)
    {
        ResetCanvas();
        _currentId = id;
        _index.CurrentId = id;

        var saved = await PageStore.LoadAsync(await ConversationStore.FolderAsync(id), Strokes);
        if (saved is { Count: > 0 })
            foreach (var n in saved)
            {
                var note = AddNote(n.Y, n.Text, n.IsHint, done: true);
                note.Heard = n.Heard;
                foreach (var svg in n.Drawings ?? new List<string>()) AddDrawing(note, svg);
                note.DrawingsSeen = note.Drawings.Count;
                Remeasure(note);
                Position(note);
            }
        else if (Strokes.GetStrokes().Count == 0)
            AddNote(Top, HintText, isHint: true, done: true);
        ApplyTheme(recolorInk: true); // loaded strokes take the current theme's ink colour

        // Writing below the last reply hasn't been answered yet (this survives a restart or a switch away).
        double answered = _notes.Where(n => !n.IsHint).Select(n => n.Y + (n.Lines + n.DrawLines) * LineHeight).DefaultIfEmpty(0).Max();
        foreach (var s in Strokes.GetStrokes())
            if (s.BoundingRect.Y + s.BoundingRect.Height > answered) _pending.Add(s.Id);

        _dirty = false;
        LayoutSheet();
        Scroller.ChangeView(null, 0, null);
        UpdateAsk();
    }

    /// <summary>Runs a change of conversation: stops any answer being written, saves the open one, then makes the change.</summary>
    async Task SwitchAsync(Func<Task> change)
    {
        if (_switching) return;
        _switching = true;
        try
        {
            if (_busy)
            {
                // The half-written reply is dropped and its ink goes back to "not yet answered".
                _discardOnCancel = true;
                _cts?.Cancel();
                while (_busy) await Task.Delay(20);
            }
            CancelIdle();
            _saveTimer.Stop();
            await SaveAsync();
            await change();
        }
        catch (Exception ex)
        {
            ShowToast("Couldn't switch conversation: " + ex.Message);
        }
        finally
        {
            _discardOnCancel = false;
            _switching = false;
        }
    }

    async Task NewConversationAsync()
    {
        // An empty page already is a fresh conversation.
        if (IsBlankNow()) return;
        await SwitchAsync(async () =>
        {
            await DropBlankAsync(keepId: null);
            var created = _index.Create(DateTimeOffset.UtcNow);
            await ShowConversationAsync(created.Id);
            await SaveIndexAsync();
        });
    }

    async Task OpenConversationAsync(string id)
    {
        if (id == _currentId) return;
        await SwitchAsync(async () =>
        {
            await DropBlankAsync(keepId: id);
            await ShowConversationAsync(id);
            await SaveIndexAsync();
        });
    }

    async Task DeleteConversationAsync(string id)
    {
        if (id != _currentId)
        {
            _index.Delete(id);
            await ConversationStore.DeleteAsync(id);
            await SaveIndexAsync();
            return;
        }
        await SwitchAsync(async () =>
        {
            var next = _index.Delete(id);
            await ConversationStore.DeleteAsync(id);
            _dirty = false; // what was just deleted must not be saved again
            _currentId = "";
            await ShowConversationAsync(next ?? _index.Create(DateTimeOffset.UtcNow).Id);
            await SaveIndexAsync();
        });
    }

    /// <summary>Conversations nothing was written in don't stay in the list once you move away from them.</summary>
    async Task DropBlankAsync(string? keepId)
    {
        foreach (var id in _index.RemoveBlank(keepId)) await ConversationStore.DeleteAsync(id);
    }

    async Task SaveIndexAsync()
    {
        await _saveLock.WaitAsync();
        try { await ConversationStore.SaveAsync(_index); }
        catch (Exception ex) { ShowToast("Couldn't save the conversation list: " + ex.Message); }
        finally { _saveLock.Release(); }
    }

    void BuildConversationsFlyout()
    {
        _conversationsFlyout.Content = new ScrollViewer { Content = _conversationList, MaxHeight = 440 };
        _conversationsFlyout.Opening += (_, _) =>
        {
            _armedDelete = _renaming = null;
            RenderConversations();
        };
        ConversationsButton.Flyout = _conversationsFlyout;
    }

    void RenderConversations()
    {
        _conversationList.Children.Clear();

        var add = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
        var addLabel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        addLabel.Children.Add(new FontIcon { Glyph = "\uE710", FontSize = 14 });
        addLabel.Children.Add(new TextBlock { Text = "New conversation" });
        add.Content = addLabel;
        add.Click += async (_, _) =>
        {
            _conversationsFlyout.Hide();
            await NewConversationAsync();
        };
        _conversationList.Children.Add(add);

        foreach (var c in _index.Newest()) _conversationList.Children.Add(ConversationRow(c));
    }

    UIElement ConversationRow(Conversation c)
    {
        var row = new Grid { ColumnSpacing = 4 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (_renaming == c.Id)
        {
            var box = new TextBox { Text = c.CustomTitle.Length > 0 ? c.CustomTitle : c.AutoTitle, PlaceholderText = "Name", MaxLength = 80 };
            async Task Commit()
            {
                _index.Rename(c.Id, box.Text);
                _renaming = null;
                await SaveIndexAsync();
                RenderConversations();
            }
            box.KeyDown += async (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter) await Commit();
                else if (e.Key == Windows.System.VirtualKey.Escape)
                {
                    _renaming = null;
                    RenderConversations();
                }
            };
            var ok = new Button { Content = new FontIcon { Glyph = "\uE73E", FontSize = 14 } };
            ToolTipService.SetToolTip(ok, "Save the name (an empty name goes back to the automatic one)");
            ok.Click += async (_, _) => await Commit();
            Grid.SetColumnSpan(box, 2);
            Grid.SetColumn(ok, 2);
            row.Children.Add(box);
            row.Children.Add(ok);
            box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
            return row;
        }

        var label = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left };
        label.Children.Add(new TextBlock
        {
            Text = c.Title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = c.Id == _currentId ? Windows.UI.Text.FontWeights.Bold : Windows.UI.Text.FontWeights.SemiBold,
        });
        label.Children.Add(new TextBlock
        {
            Text = (c.Id == _currentId ? "Open now · " : "") + ConversationIndex.When(c.Updated, DateTimeOffset.UtcNow),
            FontSize = 12,
            Opacity = 0.75,
        });
        var open = new Button
        {
            Content = label,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Colors.Transparent),
        };
        open.Click += async (_, _) =>
        {
            _conversationsFlyout.Hide();
            await OpenConversationAsync(c.Id);
        };

        var rename = new Button { Content = new FontIcon { Glyph = "\uE70F", FontSize = 14 } };
        ToolTipService.SetToolTip(rename, "Rename");
        rename.Click += (_, _) =>
        {
            _renaming = c.Id;
            _armedDelete = null;
            RenderConversations();
        };

        bool armed = _armedDelete == c.Id;
        var delete = new Button { Content = armed ? new TextBlock { Text = "Delete?" } : new FontIcon { Glyph = "\uE74D", FontSize = 14 } };
        ToolTipService.SetToolTip(delete, armed ? "Tap again to delete this conversation for good" : "Delete");
        delete.Click += async (_, _) =>
        {
            if (!armed)
            {
                _armedDelete = c.Id;
                RenderConversations();
                return;
            }
            _armedDelete = null;
            await DeleteConversationAsync(c.Id);
            RenderConversations();
        };

        Grid.SetColumn(rename, 1);
        Grid.SetColumn(delete, 2);
        row.Children.Add(open);
        row.Children.Add(rename);
        row.Children.Add(delete);
        return row;
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

        var conversation = _currentId;
        var key = ApiKeyStore.Load() ?? await PromptForKeyAsync();
        if (_switching || conversation != _currentId) return; // moved to another conversation meanwhile
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
        if (_switching || conversation != _currentId) return;
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
            var agentTools = BuildAgentTools();
            var legend = await SymbolStore.LegendAsync();
            if (expired.Count > 0) ShowToast("Sign in again to " + string.Join(", ", expired) + " (apps button).", 6);

            var result = await new ClaudeService(key, connectors, ModelStore.Load(), agentTools: agentTools).AskAsync(png, History(), progress =>
            {
                note.Heard = progress.Heard;
                note.Text = progress.Body;
                bool hasDrawing = progress.Drawing || progress.Drawings is { Count: > 0 };
                note.Block.Text = progress.Body.Length > 0 ? progress.Body : progress.Drawing ? "drawing…" : hasDrawing ? "" : "…";
                AddNewDrawings(note, progress.Drawings);
                Grow();
            }, _cts.Token, status =>
            {
                // A tool is running: show what it is doing in place of the reply, which is written after it finishes.
                note.Text = "";
                ClearDrawings(note);
                note.Block.Text = status;
                Grow();
            }, hasContext: context is not null, legend: legend);

            note.Heard = result.Heard;
            AddNewDrawings(note, result.Drawings);
            note.Text = result.Outcome switch
            {
                ReplyOutcome.Refused when result.Body.Length == 0 && note.Drawings.Count == 0 => "I can't help with that one.",
                ReplyOutcome.Truncated => result.Body + " …",
                _ => result.Body.Length > 0 || note.Drawings.Count > 0 ? result.Body : "I couldn't come up with a reply. Try writing it again?",
            };
            note.Done = true;
            note.Block.Text = note.Text;
            Grow();
            answered = true;
        }
        catch (OperationCanceledException)
        {
            if ((note.Text.Length > 0 || note.Drawings.Count > 0) && !_discardOnCancel) FinishPartial(note, Grow);
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
        var replies = _notes.Where(n => !n.IsHint && n.Done && (n.Text.Length > 0 || n.Drawings.Count > 0)).OrderBy(n => n.Y).ToList();
        var band = AnnotationContext.Find(
            strokes.Min(s => s.BoundingRect.Y),
            strokes.Max(s => s.BoundingRect.Y + s.BoundingRect.Height),
            replies.Select(n => (n.Y, n.Y + (n.Lines + n.DrawLines) * LineHeight)).ToList(),
            Top);
        if (band is not { } found) return null;

        var earlier = Strokes.GetStrokes()
            .Where(s => !ids.Contains(s.Id) && s.BoundingRect.Y < found.To && s.BoundingRect.Y + s.BoundingRect.Height > found.From)
            .ToList();
        // Each reply's words and each of its drawings are drawn into the picture on their own.
        var shown = new List<PageReply>();
        foreach (var n in found.Replies.Select(i => replies[i]))
        {
            if (n.Text.Length > 0)
                shown.Add(new PageReply(new Rect(MarginX, n.Y + TextNudge, n.Block.ActualWidth, n.Block.ActualHeight), scale => RenderElementAsync(n.Block, scale)));
            foreach (var d in n.Drawings)
                shown.Add(new PageReply(new Rect(MarginX, Canvas.GetTop(d.Image), d.Image.ActualWidth, d.Image.ActualHeight), scale => RenderElementAsync(d.Image, scale)));
        }
        return new PageContext(earlier, shown);
    }

    static async Task<RenderedText> RenderElementAsync(FrameworkElement element, double scale)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element, (int)Math.Ceiling(element.ActualWidth * scale), (int)Math.Ceiling(element.ActualHeight * scale));
        var pixels = await bitmap.GetPixelsAsync();
        return new RenderedText(pixels.ToArray(), bitmap.PixelWidth, bitmap.PixelHeight);
    }

    static void FinishPartial(Note note, Action grow)
    {
        if (note.Text.Length > 0) note.Text += " …";
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
    readonly McpRegistry _registry = new();

    async void Apps_Click(object sender, RoutedEventArgs e) => await ShowAppsAsync();

    async Task ShowAppsAsync()
    {
        using var cts = new CancellationTokenSource();
        var list = new StackPanel { Spacing = 10 };
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 380, Opacity = 0.75 };

        string? tokenEditing = null; // the app whose access token is being typed

        void Render()
        {
            list.Children.Clear();
            var apps = ConnectorStore.List();
            if (apps.Count == 0)
                list.Children.Add(new TextBlock { Text = "No apps yet. Find one in the registry below, or add one by address.", Opacity = 0.75, TextWrapping = TextWrapping.Wrap });
            foreach (var app in apps)
            {
                var row = new Grid { ColumnSpacing = 8, Width = 380 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var status = app.Access switch
                {
                    AppAccess.Open => "No sign-in needed",
                    AppAccess.Token => app.Ready ? "Access token saved" : "Needs an access token",
                    _ => app.Ready ? "Signed in" : "Not signed in",
                };
                var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                label.Children.Add(new TextBlock { Text = app.Name, FontWeight = Windows.UI.Text.FontWeights.SemiBold });
                label.Children.Add(new TextBlock { Text = status, FontSize = 12, Opacity = 0.75 });
                row.Children.Add(label);

                if (app.Access != AppAccess.Open)
                {
                    var action = new Button { Content = app.Access == AppAccess.Token ? (app.Ready ? "Remove token" : "Add token") : (app.Ready ? "Sign out" : "Sign in") };
                    action.Click += async (_, _) =>
                    {
                        if (app.Ready)
                        {
                            ConnectorStore.SignOut(app.Name);
                            Render();
                            return;
                        }
                        if (app.Access == AppAccess.Token)
                        {
                            tokenEditing = app.Name;
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
                    Grid.SetColumn(action, 1);
                    row.Children.Add(action);
                }

                var remove = new Button { Content = new FontIcon { Glyph = "\uE74D", FontSize = 14 } };
                ToolTipService.SetToolTip(remove, "Remove");
                remove.Click += (_, _) =>
                {
                    ConnectorStore.Remove(app.Name);
                    Render();
                };
                Grid.SetColumn(remove, 2);
                row.Children.Add(remove);
                list.Children.Add(row);

                if (tokenEditing == app.Name)
                {
                    var box = new PasswordBox { PlaceholderText = "Access token from the service", Width = 380 };
                    var save = new Button { Content = "Save token" };
                    save.Click += (_, _) =>
                    {
                        var token = box.Password.Trim();
                        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = token[7..].Trim();
                        if (token.Length == 0) return;
                        ConnectorStore.SaveToken(app.Name, token);
                        tokenEditing = null;
                        Render();
                    };
                    var cancel = new Button { Content = "Cancel" };
                    cancel.Click += (_, _) =>
                    {
                        tokenEditing = null;
                        Render();
                    };
                    var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    buttons.Children.Add(save);
                    buttons.Children.Add(cancel);
                    list.Children.Add(box);
                    list.Children.Add(buttons);
                }
            }
        }

        /// <summary>Adds a server and finds out how it lets you in, so the right button shows up.</summary>
        async Task AddAppAsync(string wantedName, string url, bool tokenRequired)
        {
            var name = ConnectorStore.UniqueName(wantedName);
            string access;
            try
            {
                message.Text = $"Checking how {name} lets you in…";
                var found = await _oauth.InspectAsync(url, cts.Token);
                access = found switch
                {
                    McpAccess.SignIn => AppAccess.SignIn,
                    McpAccess.Token => AppAccess.Token,
                    _ => tokenRequired ? AppAccess.Token : AppAccess.Open,
                };
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                message.Text = $"Couldn't add {name}: {ex.Message}";
                return;
            }
            ConnectorStore.Add(name, url, access);
            message.Text = access switch
            {
                AppAccess.SignIn => $"Added {name}. Sign in to use it.",
                AppAccess.Token => $"Added {name}. It needs an access token: get one from the service, then tap Add token.",
                _ => $"Added {name}. It needs no sign-in.",
            };
            Render();
        }

        // ----- the MCP registry: any server published there that is hosted online -----
        var searchBox = new TextBox { PlaceholderText = "Search the registry (todo, github, calendar…)", Width = 380 };
        var searchButton = new Button { Content = "Search" };
        var results = new StackPanel { Spacing = 10, Width = 380 };
        var resultsNote = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75, MaxWidth = 380 };
        var moreButton = new Button { Content = "More results", Visibility = Visibility.Collapsed };
        string? nextCursor = null, lastQuery = null;
        int shown = 0, hidden = 0;

        UIElement ResultRow(RegistryServer server)
        {
            var box = new StackPanel { Spacing = 6 };
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new StackPanel();
            label.Children.Add(new TextBlock { Text = server.Title, FontWeight = Windows.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            label.Children.Add(new TextBlock { Text = server.Host + (server.Remote.TokenRequired ? " · needs an access token" : ""), FontSize = 12, Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis });
            if (server.Description.Length > 0)
                label.Children.Add(new TextBlock { Text = server.Description, FontSize = 12, MaxLines = 2, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(label);

            var addButton = new Button { Content = "Add", VerticalAlignment = VerticalAlignment.Top };
            Grid.SetColumn(addButton, 1);
            row.Children.Add(addButton);
            box.Children.Add(row);

            addButton.Click += async (_, _) =>
            {
                if (server.Remote.Variables.Count == 0)
                {
                    addButton.IsEnabled = false;
                    await AddAppAsync(McpRegistry.Slug(server.Name), server.Remote.Url, server.Remote.TokenRequired);
                    addButton.IsEnabled = true;
                    return;
                }

                // The address has blanks (an account or region name): ask for them first.
                addButton.IsEnabled = false;
                var getters = new Dictionary<string, Func<string>>();
                var form = new StackPanel { Spacing = 6 };
                foreach (var v in server.Remote.Variables)
                {
                    form.Children.Add(new TextBlock { Text = (v.Description.Length > 0 ? v.Description : v.Name) + (v.Required ? " *" : ""), FontSize = 12, TextWrapping = TextWrapping.Wrap });
                    if (v.Choices.Count > 0)
                    {
                        var choice = new ComboBox { ItemsSource = v.Choices.ToList(), Width = 380 };
                        choice.SelectedIndex = Math.Max(0, v.Choices.ToList().IndexOf(v.Default));
                        form.Children.Add(choice);
                        getters[v.Name] = () => choice.SelectedItem as string ?? "";
                    }
                    else
                    {
                        var text = new TextBox { Text = v.Default, PlaceholderText = v.Name, Width = 380 };
                        form.Children.Add(text);
                        getters[v.Name] = () => text.Text;
                    }
                }
                var confirm = new Button { Content = "Add " + server.Title };
                confirm.Click += async (_, _) =>
                {
                    var url = server.Remote.Resolve(getters.ToDictionary(g => g.Key, g => g.Value()));
                    if (url is null)
                    {
                        message.Text = "Fill in the starred boxes with plain values (no slashes or spaces).";
                        return;
                    }
                    confirm.IsEnabled = false;
                    await AddAppAsync(McpRegistry.Slug(server.Name), url, server.Remote.TokenRequired);
                    box.Children.Remove(form);
                    addButton.IsEnabled = true;
                };
                form.Children.Add(confirm);
                box.Children.Add(form);
            };
            return box;
        }

        async Task SearchRegistryAsync(bool more)
        {
            searchButton.IsEnabled = moreButton.IsEnabled = false;
            try
            {
                if (!more)
                {
                    lastQuery = searchBox.Text;
                    results.Children.Clear();
                    shown = hidden = 0;
                }
                resultsNote.Text = "Searching…";
                var page = await _registry.SearchAsync(lastQuery, more ? nextCursor : null, cts.Token);
                foreach (var server in page.Servers) results.Children.Add(ResultRow(server));
                shown += page.Servers.Count;
                hidden += page.Hidden;
                nextCursor = page.NextCursor;
                moreButton.Visibility = nextCursor is null ? Visibility.Collapsed : Visibility.Visible;
                resultsNote.Text = shown == 0 && nextCursor is null
                    ? "Nothing found."
                    : $"{shown} found" + (hidden > 0 ? $"; {hidden} more can't be used here (they run on your own PC, or need a kind of sign-in this app can't do)." : ".");
            }
            catch (OperationCanceledException)
            {
                resultsNote.Text = "";
            }
            catch (Exception ex)
            {
                resultsNote.Text = "Couldn't search the registry: " + ex.Message;
            }
            searchButton.IsEnabled = moreButton.IsEnabled = true;
        }
        searchButton.Click += async (_, _) => await SearchRegistryAsync(more: false);
        moreButton.Click += async (_, _) => await SearchRegistryAsync(more: true);
        searchBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) await SearchRegistryAsync(more: false);
        };

        // ----- adding one by address -----
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
        add.Click += async (_, _) =>
        {
            // Names go to the API as server names, so keep them to letters, digits and dashes.
            var name = System.Text.RegularExpressions.Regex.Replace(nameBox.Text.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            var url = urlBox.Text.Trim();
            if (name.Length == 0 || !McpRegistry.IsPublicHttps(url))
            {
                message.Text = "An app needs a name and an https address that is reachable from the internet (Anthropic's servers call it).";
                return;
            }
            add.IsEnabled = false;
            await AddAppAsync(name, url, tokenRequired: false);
            nameBox.Text = urlBox.Text = "";
            presets.SelectedIndex = -1;
            add.IsEnabled = true;
        };

        // ----- Nextcloud calendar: signs in through the browser, so there is nothing to paste -----
        var nextcloud = new StackPanel { Spacing = 8, Width = 380 };
        var serverBox = new TextBox { PlaceholderText = "cloud.example.com", Width = 380 };

        void RenderNextcloud()
        {
            nextcloud.Children.Clear();
            nextcloud.Children.Add(new TextBlock { Text = "Nextcloud calendar", FontWeight = Windows.UI.Text.FontWeights.SemiBold });
            var account = NextcloudStore.Load();
            if (account is null)
            {
                nextcloud.Children.Add(new TextBlock
                {
                    Text = "Lets Claude read your calendar (it can't change anything). You approve the connection in your browser; no password is typed here.",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Opacity = 0.75,
                });
                nextcloud.Children.Add(serverBox);
                var connect = new Button { Content = "Connect" };
                connect.Click += async (_, _) =>
                {
                    connect.IsEnabled = false;
                    try
                    {
                        var server = Nextcloud.NormalizeServer(serverBox.Text) ?? throw new InvalidOperationException("Type the address of your Nextcloud, like cloud.example.com. It has to be https.");
                        message.Text = "Contacting Nextcloud…";
                        var start = await _nextcloud.BeginLoginAsync(server.ToString(), cts.Token);
                        if (!await Windows.System.Launcher.LaunchUriAsync(start.LoginUrl)) throw new InvalidOperationException("Couldn't open the browser.");
                        message.Text = "Approve the connection in your browser. This page continues when you have.";
                        var connected = await _nextcloud.WaitForLoginAsync(start, TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(10), cts.Token);
                        NextcloudStore.Save(connected);
                        var calendars = await _nextcloud.ListCalendarsAsync(connected, cts.Token);
                        message.Text = calendars.Count == 0
                            ? "Connected, but no calendars were found."
                            : "Connected. Claude can read: " + string.Join(", ", calendars.Select(c => c.Name)) + ".";
                    }
                    catch (OperationCanceledException)
                    {
                        message.Text = "";
                    }
                    catch (Exception ex)
                    {
                        message.Text = "Couldn't connect to Nextcloud: " + ex.Message;
                    }
                    RenderNextcloud();
                };
                nextcloud.Children.Add(connect);
            }
            else
            {
                nextcloud.Children.Add(new TextBlock { Text = $"Connected as {account.User} on {new Uri(account.Server).Host}", FontSize = 12, Opacity = 0.75 });
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var check = new Button { Content = "Check" };
                check.Click += async (_, _) =>
                {
                    check.IsEnabled = false;
                    try
                    {
                        var calendars = await _nextcloud.ListCalendarsAsync(account, cts.Token);
                        message.Text = calendars.Count == 0 ? "No calendars found." : "Claude can read: " + string.Join(", ", calendars.Select(c => c.Name)) + ".";
                    }
                    catch (OperationCanceledException)
                    {
                        message.Text = "";
                    }
                    catch (Exception ex)
                    {
                        message.Text = ex.Message;
                    }
                    check.IsEnabled = true;
                };
                var disconnect = new Button { Content = "Disconnect" };
                disconnect.Click += async (_, _) =>
                {
                    disconnect.IsEnabled = false;
                    await _nextcloud.RevokeAsync(account, cts.Token);
                    NextcloudStore.Clear();
                    message.Text = "Disconnected from Nextcloud.";
                    RenderNextcloud();
                };
                buttons.Children.Add(check);
                buttons.Children.Add(disconnect);
                nextcloud.Children.Add(buttons);
            }
        }
        RenderNextcloud();

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "Claude can always search the web. Connected apps let it use your own services too. Sign in once in your browser; Longhand keeps the sign-in on this PC.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
        });
        panel.Children.Add(nextcloud);
        panel.Children.Add(new TextBlock { Text = "Other apps (MCP servers)", FontWeight = Windows.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(list);
        panel.Children.Add(message);
        panel.Children.Add(new TextBlock { Text = "Find more in the MCP registry", FontWeight = Windows.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(new TextBlock
        {
            Text = "Anyone can publish a server there. Only add ones you trust: Claude can use what they offer, and what it sends them leaves your PC.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.75,
            MaxWidth = 380,
        });
        panel.Children.Add(searchBox);
        panel.Children.Add(searchButton);
        panel.Children.Add(results);
        panel.Children.Add(moreButton);
        panel.Children.Add(resultsNote);
        panel.Children.Add(new TextBlock { Text = "Add one by address", FontWeight = Windows.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
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

    // ---------- symbols ----------

    /// <summary>
    /// The symbols section of the settings: the user's own shortcuts, each a name, what it means and (optionally) an example
    /// drawing. Changes are saved as they are made.
    /// </summary>
    StackPanel BuildSymbolsSection()
    {
        var section = new StackPanel { Spacing = 8, Width = 380 };
        var symbols = new List<Symbol>();
        string? editingId = null, armedDelete = null;
        bool removeDrawing = false;

        var list = new StackPanel { Spacing = 6 };
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75 };
        var formTitle = new TextBlock { Text = "New symbol", FontWeight = Windows.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) };
        var nameBox = new TextBox { PlaceholderText = "Name, like envelope", MaxLength = SymbolList.MaxName, Width = 380 };
        var meaningBox = new TextBox
        {
            PlaceholderText = "What it means, like: add what I wrote to my Todoist inbox",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MaxLength = SymbolList.MaxMeaning,
            Height = 80,
            Width = 380,
        };

        // A small pad to draw the symbol on once, so Claude knows what yours looks like. Optional.
        var pad = new InkCanvas { Width = 378, Height = 140 };
        pad.InkPresenter.InputDeviceTypes = CoreInputDeviceTypes.Pen | CoreInputDeviceTypes.Mouse;
        pad.InkPresenter.UpdateDefaultDrawingAttributes(new InkDrawingAttributes { Color = _uiText.Color, Size = new Size(2.4, 2.4), FitToCurve = true, PenTip = PenTipShape.Circle });
        var padFrame = new Border { Child = pad, BorderBrush = _muted, BorderThickness = new Thickness(1), Background = _paper, Width = 380 };
        var padHint = new TextBlock { Text = "Optional: draw the symbol here (pen or mouse) so Claude can recognise it.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75 };
        var savedImage = new Image { Height = 56, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
        var savedLabel = new TextBlock { Text = "Saved drawing (draw above to replace it):", FontSize = 12, Opacity = 0.75, Visibility = Visibility.Collapsed };
        var clearPad = new Button { Content = "Clear drawing" };
        var dropSaved = new Button { Content = "Remove saved drawing", Visibility = Visibility.Collapsed };
        var save = new Button { Content = "Add symbol" };
        var cancel = new Button { Content = "Cancel", Visibility = Visibility.Collapsed };

        async Task<BitmapImage?> ThumbnailAsync(string id)
        {
            var png = await SymbolStore.ReadDrawingAsync(id);
            if (png is null) return null;
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(png);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }

        void ResetForm()
        {
            editingId = null;
            removeDrawing = false;
            nameBox.Text = meaningBox.Text = "";
            pad.InkPresenter.StrokeContainer.Clear();
            formTitle.Text = "New symbol";
            save.Content = "Add symbol";
            cancel.Visibility = dropSaved.Visibility = savedImage.Visibility = savedLabel.Visibility = Visibility.Collapsed;
            savedImage.Source = null;
        }

        void Render()
        {
            list.Children.Clear();
            if (symbols.Count == 0)
                list.Children.Add(new TextBlock { Text = "No symbols yet.", Opacity = 0.75 });
            foreach (var symbol in symbols)
            {
                var row = new Grid { ColumnSpacing = 8, Width = 380 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var thumb = new Image { Width = 48, Height = 40, Stretch = Stretch.Uniform };
                if (symbol.HasDrawing) _ = ShowThumbnailAsync(thumb, symbol.Id);
                var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                label.Children.Add(new TextBlock { Text = symbol.Name, FontWeight = Windows.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                label.Children.Add(new TextBlock { Text = symbol.Meaning, FontSize = 12, Opacity = 0.75, MaxLines = 2, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis });

                var edit = new Button { Content = new FontIcon { Glyph = "\uE70F", FontSize = 14 } };
                ToolTipService.SetToolTip(edit, "Edit");
                edit.Click += async (_, _) =>
                {
                    ResetForm();
                    editingId = symbol.Id;
                    nameBox.Text = symbol.Name;
                    meaningBox.Text = symbol.Meaning;
                    formTitle.Text = "Edit symbol";
                    save.Content = "Save changes";
                    cancel.Visibility = Visibility.Visible;
                    if (symbol.HasDrawing)
                    {
                        savedImage.Source = await ThumbnailAsync(symbol.Id);
                        savedImage.Visibility = savedLabel.Visibility = dropSaved.Visibility = Visibility.Visible;
                    }
                    message.Text = "";
                };

                bool armed = armedDelete == symbol.Id;
                var delete = new Button { Content = armed ? new TextBlock { Text = "Delete?" } : new FontIcon { Glyph = "\uE74D", FontSize = 14 } };
                ToolTipService.SetToolTip(delete, armed ? "Tap again to delete this symbol" : "Delete");
                delete.Click += async (_, _) =>
                {
                    if (!armed)
                    {
                        armedDelete = symbol.Id;
                        Render();
                        return;
                    }
                    armedDelete = null;
                    try
                    {
                        symbols = SymbolList.Remove(symbols, symbol.Id);
                        await SymbolStore.SaveAsync(symbols);
                        await SymbolStore.DeleteDrawingAsync(symbol.Id);
                        if (editingId == symbol.Id) ResetForm();
                    }
                    catch (Exception ex)
                    {
                        message.Text = "Couldn't delete the symbol: " + ex.Message;
                    }
                    Render();
                };

                Grid.SetColumn(label, 1);
                Grid.SetColumn(edit, 2);
                Grid.SetColumn(delete, 3);
                row.Children.Add(thumb);
                row.Children.Add(label);
                row.Children.Add(edit);
                row.Children.Add(delete);
                list.Children.Add(row);
            }
        }

        async Task ShowThumbnailAsync(Image target, string id) => target.Source = await ThumbnailAsync(id);

        clearPad.Click += (_, _) => pad.InkPresenter.StrokeContainer.Clear();
        dropSaved.Click += (_, _) =>
        {
            removeDrawing = true;
            savedImage.Visibility = savedLabel.Visibility = dropSaved.Visibility = Visibility.Collapsed;
        };
        cancel.Click += (_, _) =>
        {
            ResetForm();
            message.Text = "";
        };
        save.Click += async (_, _) =>
        {
            var problem = SymbolList.Problem(symbols, editingId, nameBox.Text, meaningBox.Text);
            if (problem is not null)
            {
                message.Text = problem;
                return;
            }
            save.IsEnabled = false;
            try
            {
                var id = editingId ?? SymbolList.NewId();
                bool hasDrawing = symbols.FirstOrDefault(s => s.Id == id)?.HasDrawing ?? false;
                var strokes = pad.InkPresenter.StrokeContainer.GetStrokes().ToList();
                if (strokes.Count > 0)
                {
                    await SymbolStore.WriteDrawingAsync(id, await InkSnapshot.RenderPngAsync(strokes));
                    hasDrawing = true;
                }
                else if (removeDrawing && hasDrawing)
                {
                    await SymbolStore.DeleteDrawingAsync(id);
                    hasDrawing = false;
                }
                symbols = SymbolList.Upsert(symbols, new Symbol(id, nameBox.Text, meaningBox.Text, hasDrawing));
                await SymbolStore.SaveAsync(symbols);
                message.Text = "Saved.";
                ResetForm();
                Render();
            }
            catch (Exception ex)
            {
                message.Text = "Couldn't save the symbol: " + ex.Message;
            }
            save.IsEnabled = true;
        };

        section.Children.Add(new TextBlock
        {
            Text = "Symbols are your own shortcuts: a mark you draw in the margin that means something to you. Give it a name and say what it means; Claude then does that when it sees the symbol in your writing. There are none until you add some. They are saved as you go.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.75,
        });
        section.Children.Add(list);
        section.Children.Add(formTitle);
        section.Children.Add(nameBox);
        section.Children.Add(meaningBox);
        section.Children.Add(padHint);
        section.Children.Add(padFrame);
        section.Children.Add(savedLabel);
        section.Children.Add(savedImage);
        var padButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        padButtons.Children.Add(clearPad);
        padButtons.Children.Add(dropSaved);
        section.Children.Add(padButtons);
        var formButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        formButtons.Children.Add(save);
        formButtons.Children.Add(cancel);
        section.Children.Add(formButtons);
        section.Children.Add(message);

        _ = LoadSymbolsAsync();
        async Task LoadSymbolsAsync()
        {
            symbols = await SymbolStore.LoadAsync();
            Render();
        }
        return section;
    }

    async Task<string?> PromptForKeyAsync()
    {
        bool hasKey = ApiKeyStore.Load() is not null;
        var box = new PasswordBox { PlaceholderText = hasKey ? "A key is saved. Paste a new one to replace it." : "sk-ant-…", Width = 380 };
        var models = new ComboBox { Width = 380, ItemsSource = ModelChoice.All.Select(o => o.Label).ToList() };
        models.SelectedIndex = ModelChoice.All.ToList().FindIndex(o => o.Id == ModelStore.Load());
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "Longhand sends your handwriting to Claude using your own Anthropic API key. The key is stored in Windows Credential Manager on this PC.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
        });
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock { Text = "Model", Margin = new Thickness(0, 4, 0, -6) });
        panel.Children.Add(models);
        panel.Children.Add(new TextBlock { Text = "Symbols", FontWeight = Windows.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, -6) });
        panel.Children.Add(BuildSymbolsSection());

        var dialog = new ContentDialog
        {
            Title = "Settings",
            Content = new ScrollViewer { Content = panel, MaxHeight = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (hasKey) dialog.SecondaryButtonText = "Remove key";

        var choice = await dialog.ShowAsync();
        if (choice != ContentDialogResult.None && models.SelectedIndex >= 0) ModelStore.Save(ModelChoice.All[models.SelectedIndex].Id);
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
        _dirty = true;
        if (_switching) return; // the switch saves when it has stopped the answer
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    async Task SaveAsync()
    {
        await _saveLock.WaitAsync();
        try
        {
            if (!_dirty || _currentId.Length == 0) return;
            _dirty = false;
            var id = _currentId;
            var notes = _notes.Where(n => n.Done).Select(n => new SavedNote(n.Y, n.Text, n.Heard, n.IsHint, n.Drawings.Count > 0 ? n.Drawings.Select(d => d.Markup).ToList() : null)).ToList();
            await PageStore.SaveAsync(await ConversationStore.FolderAsync(id), Strokes, notes);

            _index.Touch(id, DateTimeOffset.UtcNow, blank: IsBlankNow());
            // The title comes from the first thing Claude heard on the page.
            var first = _notes.Where(n => !n.IsHint && n.Heard.Length > 0).OrderBy(n => n.Y).FirstOrDefault();
            if (first is not null) _index.SetAutoTitle(id, first.Heard);
            await ConversationStore.SaveAsync(_index);
        }
        catch (Exception ex)
        {
            _dirty = true;
            ShowToast("Couldn't save the page: " + ex.Message);
        }
        finally
        {
            _saveLock.Release();
        }
    }

}
