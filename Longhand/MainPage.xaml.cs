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

public sealed partial class MainPage : Page, IPageView
{
    // Page geometry lives in PageLayout, shared with the web version.
    const double PageMaxWidth = PageLayout.PageMaxWidth, LineHeight = PageLayout.LineHeight, Top = PageLayout.Top, MarginX = PageLayout.MarginX, RightPad = PageLayout.RightPad, FontPx = PageLayout.FontPx;
    const double TextNudge = PageLayout.TextNudge;

    /// <summary>What a note is drawn with: its words and the tick in the margin. Kept in <see cref="NoteModel.View"/>.</summary>
    sealed class NoteVisual
    {
        public required TextBlock Block;
        public required Rectangle Tick;
    }

    static NoteVisual Visual(NoteModel note) => (NoteVisual)note.View!;
    /// <summary>A block of Claude's (a table, laid-out text or a drawing), as the picture <see cref="BlockRenderer"/> made of it.</summary>
    static Image PictureOf(DrawingModel drawing) => (Image)drawing.View!;

    /// <summary>The page itself: what is on it, asking, saving, conversations. This class is its screen.</summary>
    readonly PageSession _session;
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    readonly BlockRenderer _blocks;
    /// <summary>Waits for the width to settle before laying blocks out again at it.</summary>
    readonly DispatcherTimer _reflowTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    double _blocksWidth;
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
        _blocks = new BlockRenderer(Root);
        _session = new PageSession(this, new WindowsPageData(() => Ink.InkPresenter.StrokeContainer, _oauth, _nextcloud));

        var presenter = Ink.InkPresenter;
        presenter.InputDeviceTypes = CoreInputDeviceTypes.Pen | CoreInputDeviceTypes.Mouse;
        presenter.StrokesCollected += OnStrokesCollected;
        presenter.StrokesErased += OnStrokesErased;

        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastBox.Visibility = Visibility.Collapsed; };
        _reflowTimer.Tick += (_, _) => { _reflowTimer.Stop(); RefreshDrawings(); };
        BuildConversationsFlyout();

        var newConversation = new Windows.UI.Xaml.Input.KeyboardAccelerator
        {
            Key = Windows.System.VirtualKey.N,
            Modifiers = Windows.System.VirtualKeyModifiers.Control,
        };
        newConversation.Invoked += async (_, args) =>
        {
            args.Handled = true;
            await _session.NewConversationAsync();
        };
        KeyboardAccelerators.Add(newConversation);

        Root.Background = _desk;
        Sheet.Background = _paper;
        Tray.Background = _tray;
        Tray.BorderBrush = _trayLine;
        TypeBar.Background = _tray;
        TypeBar.BorderBrush = _trayLine;
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
        await _session.StartAsync();
        LayoutSheet();
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
        foreach (var n in _session.Notes)
        {
            Visual(n).Block.Width = TextWidth;
            Remeasure(n);
            Position(n);
        }
        UpdateSheetHeight();
        // Blocks are pictures laid out at the old width, scaled for now; once the width settles they are laid out at the new one.
        if (TextWidth != _blocksWidth && _session.Notes.Any(n => n.Drawings.Count > 0))
        {
            _reflowTimer.Stop();
            _reflowTimer.Start();
        }
    }

    double ContentBottom() => _session.ContentBottom();

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

    // ---------- Claude's notes ----------

    /// <summary>Works out how many rule lines a note's words and drawings take at the current page width.</summary>
    void Remeasure(NoteModel n)
    {
        var block = Visual(n).Block;
        block.Measure(new Size(TextWidth, double.PositiveInfinity));
        // A reply that is only a drawing has no line of words above it.
        int least = n.Drawings.Count > 0 && block.Text.Length == 0 ? 0 : 1;
        n.Lines = Math.Max(least, (int)Math.Round(block.DesiredSize.Height / LineHeight));
        foreach (var d in n.Drawings)
        {
            var (w, h) = Html.Fit(d.Aspect, TextWidth);
            var image = PictureOf(d);
            image.Width = w;
            image.Height = h;
            d.Lines = (int)Math.Ceiling(h / LineHeight);
        }
    }

    void Position(NoteModel n)
    {
        var visual = Visual(n);
        Canvas.SetTop(visual.Block, n.Y + TextNudge);
        Canvas.SetTop(visual.Tick, n.Y + LineHeight * 0.35);
        visual.Tick.Height = Math.Max(4, (n.Lines + n.DrawLines) * LineHeight - LineHeight * 0.35 + 4);
        double top = n.Y + n.Lines * LineHeight;
        foreach (var d in n.Drawings)
        {
            var image = PictureOf(d);
            Canvas.SetLeft(image, MarginX);
            Canvas.SetTop(image, top);
            top += d.Lines * LineHeight;
        }
    }

    void EnsureVisible(double bottom)
    {
        double visibleBottom = Scroller.VerticalOffset + Scroller.ViewportHeight - 90;
        if (bottom > visibleBottom) Scroller.ChangeView(null, bottom - Scroller.ViewportHeight + 90, null);
    }

    // ---------- ink events ----------

    void OnStrokesCollected(InkPresenter sender, InkStrokesCollectedEventArgs args) =>
        _session.StrokesCollected(args.Strokes.Select(s => s.Id).ToList());

    void OnStrokesErased(InkPresenter sender, InkStrokesErasedEventArgs args) =>
        _session.StrokesErased(args.Strokes.Select(s => s.Id).ToList(), args.Strokes.Select(s => s.Clone()).ToList());

    // ---------- typing ----------

    void ShowTypeBar(bool on)
    {
        TypeBar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        KeyboardButton.IsChecked = on;
        if (on) TypeBox.Focus(FocusState.Programmatic);
    }

    void Keyboard_Click(object sender, RoutedEventArgs e) => ShowTypeBar(KeyboardButton.IsChecked == true);

    async Task SendTypedAsync()
    {
        var text = TypeBox.Text.Trim();
        if (text.Length == 0) return;
        TypeBox.Text = "";
        await _session.TypeAsync(text);
    }

    async void TypeSend_Click(object sender, RoutedEventArgs e) => await SendTypedAsync();

    async void TypeBox_KeyDown(object sender, Windows.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            ShowTypeBar(false);
            return;
        }
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        bool shift = Window.Current.CoreWindow.GetKeyState(Windows.System.VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        if (shift) return; // a new line
        e.Handled = true;
        await SendTypedAsync();
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

    void Undo_Click(object sender, RoutedEventArgs e) => _session.Undo();

    // ---------- drawings ----------

    static string Hex6(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>
    /// Renders a block at the page's text width in the current ink colour, unless its picture already is that; its shape then
    /// sets its size on the page. A block that can't be rendered keeps the picture it had (or none) and says so once.
    /// </summary>
    async Task RenderBlockAsync(DrawingModel d)
    {
        var image = PictureOf(d);
        var key = $"{TextWidth:0}|{Hex6(_claudeInk.Color)}";
        if (image.Tag as string == key) return;
        var rendered = await _blocks.RenderAsync(d.Markup, TextWidth, Hex6(_claudeInk.Color));
        if (rendered is null)
        {
            if (image.Tag is null) ShowToast("A table or drawing couldn't be shown.");
            image.Tag ??= "";
            return;
        }
        var bitmap = new BitmapImage();
        using (var stream = new InMemoryRandomAccessStream())
        {
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(rendered.Png);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
        }
        image.Source = bitmap;
        image.Tag = key;
        _blocksWidth = TextWidth;
        d.Aspect = rendered.Width / rendered.Height;
    }

    /// <summary>Lays the page out again, rendering blocks afresh (after a change of theme or width).</summary>
    void RefreshDrawings()
    {
        _blocksWidth = TextWidth;
        _ = _session.RelayoutAsync();
    }

    // ---------- tools the app runs for Claude ----------

    readonly Nextcloud _nextcloud = new();

    // ---------- conversations ----------

    readonly Flyout _conversationsFlyout = new() { Placement = Windows.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top };
    readonly StackPanel _conversationList = new() { Spacing = 2, Width = 340 };
    string? _armedDelete, _renaming;

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
            await _session.NewConversationAsync();
        };
        _conversationList.Children.Add(add);

        foreach (var c in _session.Index.Newest()) _conversationList.Children.Add(ConversationRow(c));
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
                _renaming = null;
                await _session.RenameConversationAsync(c.Id, box.Text);
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
            FontWeight = c.Id == _session.CurrentId ? Windows.UI.Text.FontWeights.Bold : Windows.UI.Text.FontWeights.SemiBold,
        });
        label.Children.Add(new TextBlock
        {
            Text = (c.Id == _session.CurrentId ? "Open now · " : "") + ConversationIndex.When(c.Updated, DateTimeOffset.UtcNow),
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
            await _session.OpenConversationAsync(c.Id);
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
            await _session.DeleteConversationAsync(c.Id);
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

    async void Ask_Click(object sender, RoutedEventArgs e) => await _session.AskOrStopAsync();

    // ---------- the page's screen (IPageView) ----------

    IReadOnlyList<StrokeSpan> IPageView.Strokes() =>
        Strokes.GetStrokes().Select(s => new StrokeSpan(s.Id, s.BoundingRect.Y, s.BoundingRect.Y + s.BoundingRect.Height)).ToList();

    void IPageView.ShiftStrokes(double anchor, double delta, IReadOnlySet<uint> keep)
    {
        bool any = false;
        foreach (var s in Strokes.GetStrokes())
        {
            bool move = !keep.Contains(s.Id) && s.BoundingRect.Y >= anchor;
            s.Selected = move;
            any |= move;
        }
        if (!any) return;
        Strokes.MoveSelected(new Point(0, delta));
        foreach (var s in Strokes.GetStrokes()) s.Selected = false;
    }

    bool IPageView.DeleteStroke(uint id)
    {
        bool found = false;
        foreach (var s in Strokes.GetStrokes())
        {
            s.Selected = s.Id == id;
            found |= s.Selected;
        }
        if (found) Strokes.DeleteSelected();
        return found;
    }

    void IPageView.RestoreStrokes(object token)
    {
        foreach (var copy in (List<InkStroke>)token) Strokes.AddStroke(copy);
    }

    void IPageView.ClearStrokes() => Strokes.Clear();

    void IPageView.NoteAdded(NoteModel note)
    {
        var block = new TextBlock
        {
            Text = note.Shown,
            FontFamily = _handFont,
            FontSize = FontPx,
            LineHeight = LineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextWrapping = TextWrapping.WrapWholeWords,
            Foreground = note.IsTyped ? _uiText : _claudeInk,
            Width = TextWidth,
            Opacity = note.IsHint ? 0.8 : 1,
            IsTextScaleFactorEnabled = false,
        };
        var tick = new Rectangle { Width = 2.5, RadiusX = 1.25, RadiusY = 1.25, Fill = note.IsTyped ? _uiText : _claudeInk, Opacity = note.IsHint ? 0.35 : 0.6 };
        Canvas.SetLeft(block, MarginX);
        Canvas.SetLeft(tick, MarginX - 31);
        NoteLayer.Children.Add(tick);
        NoteLayer.Children.Add(block);
        note.View = new NoteVisual { Block = block, Tick = tick };
    }

    void IPageView.NoteChanged(NoteModel note) => Visual(note).Block.Text = note.Shown;

    void IPageView.NoteRemoved(NoteModel note)
    {
        var visual = Visual(note);
        NoteLayer.Children.Remove(visual.Block);
        NoteLayer.Children.Remove(visual.Tick);
        foreach (var d in note.Drawings) NoteLayer.Children.Remove(PictureOf(d));
    }

    void IPageView.DrawingAdded(NoteModel note, DrawingModel drawing)
    {
        var image = new Image { Stretch = Stretch.Uniform };
        Windows.UI.Xaml.Automation.AutomationProperties.SetName(image, drawing.Alt);
        drawing.View = image;
        NoteLayer.Children.Add(image); // rendered when the note is measured
    }

    void IPageView.DrawingsCleared(NoteModel note)
    {
        foreach (var d in note.Drawings) NoteLayer.Children.Remove(PictureOf(d));
    }

    async Task IPageView.MeasureAsync(NoteModel note)
    {
        foreach (var d in note.Drawings.ToList()) await RenderBlockAsync(d);
        Remeasure(note);
    }

    void IPageView.Place(NoteModel note) => Position(note);
    void IPageView.ContentChanged() => UpdateSheetHeight();
    void IPageView.EnsureVisible(double bottom) => EnsureVisible(bottom);

    void IPageView.PageShown()
    {
        ApplyTheme(recolorInk: true); // loaded strokes take the current theme's ink colour
        LayoutSheet();
        Scroller.ChangeView(null, 0, null);
    }

    async Task<byte[]> IPageView.RenderSnapshotAsync(IReadOnlyList<uint> strokeIds, SnapshotContext? context)
    {
        var wanted = strokeIds.ToHashSet();
        var strokes = Strokes.GetStrokes().Where(s => wanted.Contains(s.Id)).ToList();
        if (context is null) return await InkSnapshot.RenderPngAsync(strokes);

        var earlierIds = context.EarlierInk.ToHashSet();
        var earlier = Strokes.GetStrokes().Where(s => earlierIds.Contains(s.Id)).ToList();
        // Each reply's words and each of its drawings are drawn into the picture on their own.
        var shown = new List<PageReply>();
        foreach (var n in context.Replies)
        {
            var block = Visual(n).Block;
            if (n.Text.Length > 0)
                shown.Add(new PageReply(new Rect(MarginX, n.Y + TextNudge, block.ActualWidth, block.ActualHeight), scale => RenderElementAsync(block, scale)));
            foreach (var d in n.Drawings)
            {
                var image = PictureOf(d);
                shown.Add(new PageReply(new Rect(MarginX, Canvas.GetTop(image), image.ActualWidth, image.ActualHeight), scale => RenderElementAsync(image, scale)));
            }
        }
        return await InkSnapshot.RenderPngAsync(strokes, new PageContext(earlier, shown));
    }

    static async Task<RenderedText> RenderElementAsync(FrameworkElement element, double scale)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element, (int)Math.Ceiling(element.ActualWidth * scale), (int)Math.Ceiling(element.ActualHeight * scale));
        var pixels = await bitmap.GetPixelsAsync();
        return new RenderedText(pixels.ToArray(), bitmap.PixelWidth, bitmap.PixelHeight);
    }

    Task<string?> IPageView.RequestApiKeyAsync() => PromptForKeyAsync();
    void IPageView.Toast(string message, double seconds) => ShowToast(message, seconds);

    /// <summary>A note that couldn't be sent: its words come back into the box (ahead of anything typed since).</summary>
    void IPageView.RestoreTyped(string text)
    {
        ShowTypeBar(true);
        TypeBox.Text = TypeBox.Text.Length > 0 ? text + "\n" + TypeBox.Text : text;
        TypeBox.SelectionStart = TypeBox.Text.Length;
    }

    void IPageView.StateChanged(PageState state)
    {
        AskLabel.Text = state.Busy ? "Stop" : "Ask";
        BusyRing.IsActive = state.Busy;
        AskIcon.Visibility = state.Busy ? Visibility.Collapsed : Visibility.Visible;
        AskButton.IsEnabled = state.CanAsk;
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
                    Text = "Lets Claude read your calendar, and add, change or delete events when you ask it to. You approve the connection in your browser; no password is typed here.",
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
                            : "Connected. Claude can use: " + string.Join(", ", calendars.Select(c => c.Label)) + ".";
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
                        message.Text = calendars.Count == 0 ? "No calendars found." : "Claude can use: " + string.Join(", ", calendars.Select(c => c.Label)) + ".";
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
        var symbols = new List<UserSymbol>();
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
                symbols = SymbolList.Upsert(symbols, new UserSymbol(id, nameBox.Text, meaningBox.Text, hasDrawing));
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
        models.SelectedIndex = ModelChoice.All.ToList().FindIndex(o => o.Id == _session.CurrentModel);
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "Longhand sends your handwriting to Claude using your own Anthropic API key. The key is stored in Windows Credential Manager on this PC.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
        });
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock { Text = "Model for this conversation", Margin = new Thickness(0, 4, 0, -6) });
        panel.Children.Add(models);
        panel.Children.Add(new TextBlock { Text = "Each conversation keeps its own model. New ones start with the one you chose last.", FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 });
        var v = Windows.ApplicationModel.Package.Current.Id.Version;
        panel.Children.Add(new TextBlock { Text = $"Longhand version {v.Major}.{v.Minor}.{v.Build}.{v.Revision}", FontSize = 12, Opacity = 0.75, Margin = new Thickness(0, 6, 0, 0) });
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
        if (choice != ContentDialogResult.None && models.SelectedIndex >= 0 && ModelChoice.All[models.SelectedIndex].Id != _session.CurrentModel)
            await _session.SetModelAsync(ModelChoice.All[models.SelectedIndex].Id);
        if (choice == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Password))
        {
            var key = box.Password.Trim();
            ApiKeyStore.Save(key);
            ShowToast("API key saved.");
            return key;
        }
        if (choice == ContentDialogResult.Secondary)
        {
            ApiKeyStore.Clear();
            ShowToast("API key removed.");
        }
        return ApiKeyStore.Load();
    }

    // ---------- toast ----------

    void ShowToast(string message, double seconds = 3.5)
    {
        ToastText.Text = message;
        ToastBox.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(seconds);
        _toastTimer.Start();
    }
}
