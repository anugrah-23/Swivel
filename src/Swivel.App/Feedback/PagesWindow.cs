using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Serilog;
using Swivel.App.Theme;
using Swivel.Core.Files;
using Swivel.Engines.Pdf;

namespace Swivel.App.Feedback;

/// <summary>
/// The page organiser: every page of the dropped PDFs as a card. Drag to reorder, rotate, delete, add
/// pages from another PDF, then save everything (or just the selection) as a new PDF. The originals
/// are never touched; the answer is a page plan the PDF engine assembles.
/// </summary>
public sealed class PagesWindow : Window
{
    private const int ThumbSide = 150;
    private const string DragFormat = "SwivelPageCards";

    /// <summary>A page card that screen readers (and UI tests) see as a list item named "Page N".</summary>
    private sealed class CardView : Border
    {
        protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new Peer(this);

        private sealed class Peer(CardView owner) : System.Windows.Automation.Peers.FrameworkElementAutomationPeer(owner)
        {
            protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() =>
                System.Windows.Automation.Peers.AutomationControlType.ListItem;

            protected override string GetClassNameCore() => "SwivelPageCard";
        }
    }

    private sealed class PageCard
    {
        public required int Source { get; init; }
        public required int Page { get; init; }
        public int Rotation { get; set; }
        public Border View { get; set; } = null!;
        public Image Picture { get; } = new() { Stretch = Stretch.Uniform };
        public RotateTransform Turn { get; } = new(0);
    }

    private readonly TaskCompletionSource<IReadOnlyDictionary<string, string>?> _answer = new();
    private readonly List<string> _sources = [];
    private readonly int _droppedCount;
    private readonly List<PageCard> _cards = [];
    private readonly HashSet<PageCard> _selected = [];
    private readonly Stack<List<(PageCard Card, int Rotation)>> _history = new();
    private readonly WrapPanel _grid = new() { Margin = new Thickness(6) };
    private readonly TextBlock _count = new();
    private readonly Button _saveSelection;
    private readonly Button _save;
    private readonly CancellationTokenSource _rendering = new();
    private PageCard? _anchor;
    private Point? _pressed;
    private PageCard? _pressedCard;

    private PagesWindow(IReadOnlyList<string> pdfs)
    {
        _droppedCount = pdfs.Count;
        Title = "Swivel pages";
        Width = 960;
        Height = 680;
        MinWidth = 640;
        MinHeight = 420;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = Shell.Logo.CreateImage();

        _count.FontFamily = Palette.Mono;
        _count.FontWeight = FontWeights.Bold;
        _count.FontSize = 12;
        _count.Foreground = Palette.Ink;
        _count.VerticalAlignment = VerticalAlignment.Center;

        // Title bar
        var close = new Button { Content = "✕", Style = (Style)FindResource("SwivelButton"), Padding = new Thickness(9, 2, 9, 2), Margin = new Thickness(0) };
        System.Windows.Automation.AutomationProperties.SetName(close, "Cancel");
        close.Click += (_, _) => Finish(null);
        var closeHolder = new Border { Padding = new Thickness(0, 8, 12, 8), Child = close };
        var titleBar = new DockPanel { Background = Palette.Sunflower, LastChildFill = true };
        DockPanel.SetDock(closeHolder, Dock.Right);
        titleBar.Children.Add(closeHolder);
        titleBar.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(16, 8, 0, 8),
            Children =
            {
                new TextBlock { Text = "PAGES", FontFamily = Palette.Display, FontWeight = FontWeights.Black, FontSize = 20, Foreground = Palette.Ink, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock
                {
                    Text = pdfs.Count == 1 ? Path.GetFileName(pdfs[0]) : $"{pdfs.Count} PDFs",
                    FontFamily = Palette.Body, FontSize = 13, Foreground = Palette.Ink, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(14, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
                },
            },
        });
        titleBar.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; else DragMove(); };

        // Toolbar
        var toolbar = new WrapPanel { Margin = new Thickness(16, 12, 16, 4) };
        toolbar.Children.Add(Tool("↺ Rotate left", "Shift+R", () => Rotate(-90)));
        toolbar.Children.Add(Tool("↻ Rotate right", "R", () => Rotate(90)));
        toolbar.Children.Add(Tool("Delete", "Del", Delete));
        toolbar.Children.Add(Tool("Add PDF…", null, AddPdf));
        toolbar.Children.Add(Tool("Select all", "Ctrl+A", SelectAll));
        toolbar.Children.Add(Tool("Undo", "Ctrl+Z", Undo));

        // Footer
        var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("SwivelButton"), IsCancel = true };
        cancel.Click += (_, _) => Finish(null);
        _saveSelection = new Button { Content = "Save selected", Style = (Style)FindResource("SwivelButton") };
        _saveSelection.Click += (_, _) => Save(selectionOnly: true);
        _save = new Button { Content = "Save as new PDF", Style = (Style)FindResource("SwivelPrimaryButton"), Margin = new Thickness(0) };
        _save.Click += (_, _) => Save(selectionOnly: false);
        var footer = new DockPanel { Margin = new Thickness(16, 8, 16, 14) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { cancel, _saveSelection, _save } };
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(_count);

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _grid,
            Margin = new Thickness(10, 0, 10, 0),
            Background = Brushes.Transparent,
        };
        _grid.Background = Brushes.Transparent; // so clicks on the gaps clear the selection
        _grid.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource == _grid) { _selected.Clear(); Refresh(); } };

        var layout = new DockPanel();
        var top = new Border { BorderBrush = Palette.Ink, BorderThickness = new Thickness(0, 0, 0, 3), CornerRadius = new CornerRadius(13, 13, 0, 0), ClipToBounds = true, Child = titleBar };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(toolbar, Dock.Top);
        var bottom = new Border { BorderBrush = Palette.Ink, BorderThickness = new Thickness(0, 3, 0, 0), Child = footer };
        DockPanel.SetDock(bottom, Dock.Bottom);
        layout.Children.Add(top);
        layout.Children.Add(toolbar);
        layout.Children.Add(bottom);
        layout.Children.Add(scroller);

        var card = new Border { Background = Palette.Cream, BorderBrush = Palette.Ink, BorderThickness = new Thickness(3), CornerRadius = new CornerRadius(16), Child = layout };
        var root = new Grid { Margin = new Thickness(0, 0, 8, 8) };
        root.Children.Add(new Border { Background = Palette.Ink, CornerRadius = new CornerRadius(16), Margin = new Thickness(8, 8, -8, -8) });
        root.Children.Add(card);
        Content = root;

        PreviewKeyDown += OnKey;
        Closed += (_, _) => { _rendering.Cancel(); _answer.TrySetResult(null); };

        foreach (string pdf in pdfs) AddSource(pdf);
        Refresh();
    }

    /// <summary>Opens the organiser for <paramref name="pdfs"/>. Null means cancelled.</summary>
    public static Task<IReadOnlyDictionary<string, string>?> AskAsync(IReadOnlyList<string> pdfs)
    {
        var window = new PagesWindow(pdfs);
        window.Show();
        Native.Win32.ForceForeground(new System.Windows.Interop.WindowInteropHelper(window).Handle);
        window.Activate();
        return window._answer.Task;
    }

    // ---- Pages ----------------------------------------------------------------------------------

    private void AddSource(string pdf)
    {
        int source = _sources.Count;
        _sources.Add(pdf);
        int pages;
        try { pages = PdfPreview.PageCount(pdf); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Couldn't open {Pdf} in the organiser", pdf);
            return;
        }

        var added = new List<PageCard>();
        for (int i = 0; i < pages; i++)
        {
            var card = new PageCard { Source = source, Page = i };
            card.View = BuildView(card);
            _cards.Add(card);
            added.Add(card);
        }

        var token = _rendering.Token;
        _ = Task.Run(() => PdfPreview.Thumbnails(pdf, ThumbSide * 2, (index, thumb) =>
        {
            var image = BitmapSource.Create(thumb.Width, thumb.Height, 192, 192, PixelFormats.Bgr32, null, thumb.Bgrx, thumb.Width * 4);
            image.Freeze();
            Dispatcher.InvokeAsync(() => added[index].Picture.Source = image);
        }, token), token);
    }

    private Border BuildView(PageCard card)
    {
        card.Picture.LayoutTransform = card.Turn;
        var badge = new Border
        {
            Background = Palette.Ink, CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 1, 7, 1),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(-4, -4, 0, 0),
            Child = new TextBlock { FontFamily = Palette.Mono, FontWeight = FontWeights.Bold, FontSize = 11, Foreground = Palette.Sunflower },
        };
        var source = new TextBlock
        {
            Text = _droppedCount > 1 || _sources.Count > 1 ? $"{(char)('A' + Math.Min(card.Source, 25))} · p{card.Page + 1}" : "",
            FontFamily = Palette.Mono, FontSize = 10, Foreground = Palette.Ink, Opacity = 0.7,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0),
        };
        var face = new Border
        {
            Width = ThumbSide + 20, Height = ThumbSide + 40, Background = Palette.Paper, BorderBrush = Palette.Ink,
            BorderThickness = new Thickness(2.5), CornerRadius = new CornerRadius(10), Padding = new Thickness(8),
            Child = new DockPanel
            {
                Children =
                {
                    new Border { Child = source },
                    new Grid { Children = { new Border { Background = Palette.Cream, Child = card.Picture }, badge } },
                },
            },
        };
        DockPanel.SetDock((UIElement)((DockPanel)face.Child).Children[0], Dock.Bottom);

        var view = new CardView { Margin = new Thickness(8), BorderThickness = new Thickness(4, 0, 4, 0), BorderBrush = Brushes.Transparent, Child = face, AllowDrop = true, Cursor = Cursors.Hand };
        System.Windows.Automation.AutomationProperties.SetName(view, $"Page {card.Page + 1}");
        view.PreviewMouseLeftButtonDown += (_, e) => { _pressed = e.GetPosition(this); _pressedCard = card; Select(card, Keyboard.Modifiers); e.Handled = true; };
        view.PreviewMouseMove += (_, e) => StartDrag(e);
        view.PreviewMouseLeftButtonUp += (_, _) => { _pressed = null; _pressedCard = null; };
        view.DragOver += (_, e) => ShowInsertion(view, card, e);
        view.DragLeave += (_, _) => view.BorderBrush = Brushes.Transparent;
        view.Drop += (_, e) => DropBefore(card, e);
        return view;
    }

    private void Refresh()
    {
        _grid.Children.Clear();
        for (int i = 0; i < _cards.Count; i++)
        {
            var card = _cards[i];
            var face = (Border)card.View.Child;
            bool selected = _selected.Contains(card);
            face.Background = selected ? Palette.Sunflower : Palette.Paper;
            face.Effect = null;
            face.BorderThickness = new Thickness(selected ? 3.5 : 2.5);
            var badge = (TextBlock)((Border)((Grid)((DockPanel)face.Child).Children[1]).Children[1]).Child;
            badge.Text = (i + 1).ToString();
            card.Turn.Angle = card.Rotation;
            card.View.BorderBrush = Brushes.Transparent;
            _grid.Children.Add(card.View);
        }
        _count.Text = _cards.Count == 0 ? "No pages left" : $"{_cards.Count} page{(_cards.Count == 1 ? "" : "s")}{(_selected.Count > 0 ? $" · {_selected.Count} selected" : "")}";
        _save.IsEnabled = _cards.Count > 0;
        _saveSelection.IsEnabled = _selected.Count > 0 && _selected.Count < _cards.Count;
    }

    // ---- Selection and editing --------------------------------------------------------------------

    private void Select(PageCard card, ModifierKeys modifiers)
    {
        if (modifiers.HasFlag(ModifierKeys.Shift) && _anchor != null)
        {
            int a = _cards.IndexOf(_anchor), b = _cards.IndexOf(card);
            if (!modifiers.HasFlag(ModifierKeys.Control)) _selected.Clear();
            for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) _selected.Add(_cards[i]);
        }
        else if (modifiers.HasFlag(ModifierKeys.Control))
        {
            if (!_selected.Remove(card)) _selected.Add(card);
            _anchor = card;
        }
        else
        {
            if (!_selected.Contains(card)) { _selected.Clear(); _selected.Add(card); }
            _anchor = card;
        }
        Refresh();
    }

    private IReadOnlyList<PageCard> Targets() => _selected.Count > 0 ? _cards.Where(_selected.Contains).ToList() : [];

    private void Remember() => _history.Push(_cards.Select(c => (c, c.Rotation)).ToList());

    private void Undo()
    {
        if (_history.Count == 0) return;
        var state = _history.Pop();
        _cards.Clear();
        foreach (var (card, rotation) in state) { card.Rotation = rotation; _cards.Add(card); }
        _selected.IntersectWith(_cards);
        Refresh();
    }

    private void Rotate(int degrees)
    {
        var targets = Targets();
        if (targets.Count == 0) return;
        Remember();
        foreach (var card in targets) card.Rotation = PagePlan.Normalise(card.Rotation + degrees);
        Refresh();
    }

    private void Delete()
    {
        var targets = Targets();
        if (targets.Count == 0) return;
        Remember();
        int first = _cards.IndexOf(targets[0]);
        foreach (var card in targets) _cards.Remove(card);
        _selected.Clear();
        if (_cards.Count > 0) _selected.Add(_cards[Math.Min(first, _cards.Count - 1)]);
        Refresh();
    }

    private void SelectAll()
    {
        _selected.UnionWith(_cards);
        Refresh();
    }

    private void AddPdf()
    {
        var dialog = new OpenFileDialog { Title = "Add pages from another PDF", Filter = "PDF files|*.pdf", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        Remember();
        foreach (string file in dialog.FileNames) AddSource(file);
        Refresh();
    }

    // ---- Drag to reorder ----------------------------------------------------------------------------

    private void StartDrag(MouseEventArgs e)
    {
        if (_pressed is not { } start || _pressedCard is null || e.LeftButton != MouseButtonState.Pressed) return;
        var now = e.GetPosition(this);
        if (Math.Abs(now.X - start.X) < 6 && Math.Abs(now.Y - start.Y) < 6) return;
        _pressed = null;
        var dragged = _selected.Contains(_pressedCard) ? Targets() : [_pressedCard];
        DragDrop.DoDragDrop(_pressedCard.View, new DataObject(DragFormat, dragged), DragDropEffects.Move);
    }

    /// <summary>A bar on the card's left or right edge shows where the pages will land.</summary>
    private static void ShowInsertion(Border view, PageCard card, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragFormat)) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        bool after = e.GetPosition(view).X > view.ActualWidth / 2;
        view.BorderBrush = Palette.Pink;
        view.BorderThickness = after ? new Thickness(0, 0, 4, 0) : new Thickness(4, 0, 0, 0);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void DropBefore(PageCard target, DragEventArgs e)
    {
        target.View.BorderBrush = Brushes.Transparent;
        if (e.Data.GetData(DragFormat) is not IReadOnlyList<PageCard> moving || moving.Contains(target)) return;
        bool after = e.GetPosition(target.View).X > target.View.ActualWidth / 2;
        Remember();
        foreach (var card in moving) _cards.Remove(card);
        int index = _cards.IndexOf(target) + (after ? 1 : 0);
        _cards.InsertRange(index, moving);
        Refresh();
        e.Handled = true;
    }

    // ---- Keys and saving ----------------------------------------------------------------------------

    private void OnKey(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Delete or Key.Back: Delete(); break;
            case Key.R: Rotate(shift ? -90 : 90); break;
            case Key.A when ctrl: SelectAll(); break;
            case Key.Z when ctrl: Undo(); break;
            case Key.Escape: Finish(null); break;
            case Key.Enter: Save(selectionOnly: false); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Save(bool selectionOnly)
    {
        var pages = selectionOnly ? Targets() : _cards;
        if (pages.Count == 0) return;
        var answer = new Dictionary<string, string>
        {
            ["plan"] = PagePlan.Format(pages.Select(c => new PlannedPage(c.Source, c.Page, c.Rotation))),
        };
        if (_sources.Count > _droppedCount) answer["sources"] = string.Join('|', _sources.Skip(_droppedCount));
        Finish(answer);
    }

    private void Finish(IReadOnlyDictionary<string, string>? answer)
    {
        _answer.TrySetResult(answer);
        Close();
    }

    private Button Tool(string label, string? key, Action action)
    {
        var button = new Button
        {
            Content = label, Style = (Style)FindResource("SwivelButton"), Margin = new Thickness(0, 0, 8, 8),
            ToolTip = key is null ? null : $"{label.TrimStart('↺', '↻', ' ')} ({key})",
        };
        button.Click += (_, _) => action();
        return button;
    }
}
