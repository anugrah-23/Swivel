using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Serilog;
using Swivel.App.Services;
using Swivel.App.Theme;
using Swivel.Core.Jobs;
using Swivel.Engines.Pdf;

namespace Swivel.App.Feedback;

/// <summary>
/// The PDF editor: one page at a time with every line of text and every picture outlined. Double-click
/// a line to retype it in place, drag things to move them, pull a picture's corners to resize it, add
/// text or pictures, then save as a new PDF. Edits go through <see cref="PdfEditSession"/>, which never
/// writes the original; the answer is the path of the edited copy.
/// </summary>
public sealed class EditorWindow : Window
{
    private const double MinZoom = 0.5, MaxZoom = 3, NewTextSize = 12, ThumbSide = 110;
    private const string PictureFilter = "Pictures|*.jpg;*.jpeg;*.png;*.webp;*.heic;*.heif;*.avif;*.gif;*.bmp;*.tif;*.tiff";

    /// <summary>An outlined item that screen readers (and UI tests) see by its text, or as "Image N".</summary>
    private sealed class ItemView : Grid
    {
        public required PageItem Item { get; init; }
        public System.Windows.Shapes.Rectangle Outline { get; } = new() { StrokeThickness = 1.5, IsHitTestVisible = false };

        protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

        private sealed class Peer(ItemView owner) : FrameworkElementAutomationPeer(owner)
        {
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

            protected override string GetClassNameCore() => "SwivelPdfItem";
        }
    }

    private readonly TaskCompletionSource<IReadOnlyDictionary<string, string>?> _answer = new();
    private readonly PdfEditSession _session;
    private readonly string _name;
    private readonly Image _pageImage = new() { Stretch = Stretch.Fill };
    private readonly Canvas _overlay = new() { Background = Brushes.Transparent, ClipToBounds = true };
    private readonly Grid _surface = new() { Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
    private readonly ScrollViewer _scroller;
    private readonly StackPanel _thumbs = new() { Margin = new Thickness(8, 4, 8, 8) };
    private readonly List<Image> _thumbImages = [];
    private readonly List<System.Windows.Shapes.Rectangle> _handles = [];
    private readonly Border _noticeBox = new() { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 4, 10, 4), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _notice = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _pageLabel = new();
    private readonly Button _save, _undo, _redo, _delete, _replace, _addText, _previous, _next;
    private readonly CancellationTokenSource _thumbnailing = new();
    private BitmapSource? _pageBitmap;
    private int _page;
    private double _zoom = 1;
    private ItemView? _selected;
    private TextBox? _editor;
    private PageItem? _editorItem;
    private Point _editorAt;
    private bool _placingText, _confirmDiscard;
    private Point? _dragStart;
    private string? _dragHandle;
    private bool _dragging;
    private PageBox _dragFrom, _dragTo;

    private EditorWindow(PdfEditSession session, string pdf)
    {
        _session = session;
        _name = Path.GetFileName(pdf);
        Title = "Swivel editor";
        Width = 1080;
        Height = 820;
        MinWidth = 720;
        MinHeight = 480;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = Shell.Logo.CreateImage();

        // Title bar
        var close = new Button { Content = "✕", Style = (Style)FindResource("SwivelButton"), Padding = new Thickness(9, 2, 9, 2), Margin = new Thickness(0) };
        AutomationProperties.SetName(close, "Close editor");
        close.Click += (_, _) => Cancel();
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
                new TextBlock { Text = "EDIT", FontFamily = Palette.Display, FontWeight = FontWeights.Black, FontSize = 20, Foreground = Palette.Ink, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock
                {
                    Text = _name, FontFamily = Palette.Body, FontSize = 13, Foreground = Palette.Ink, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(14, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
                },
            },
        });
        titleBar.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; else DragMove(); };

        // Toolbar
        var toolbar = new WrapPanel { Margin = new Thickness(16, 12, 16, 4) };
        _addText = Tool("Add text", "T", () => StartPlacingText(!_placingText));
        toolbar.Children.Add(_addText);
        toolbar.Children.Add(Tool("Add picture…", null, AddPicture));
        _replace = Tool("Replace picture…", null, ReplacePicture);
        toolbar.Children.Add(_replace);
        _delete = Tool("Delete", "Del", DeleteSelected);
        toolbar.Children.Add(_delete);
        _undo = Tool("Undo", "Ctrl+Z", Undo);
        toolbar.Children.Add(_undo);
        _redo = Tool("Redo", "Ctrl+Y", Redo);
        toolbar.Children.Add(_redo);
        toolbar.Children.Add(Tool("−", "Ctrl+−", () => Zoom(_zoom / 1.25), "Zoom out"));
        toolbar.Children.Add(Tool("+", "Ctrl++", () => Zoom(_zoom * 1.25), "Zoom in"));

        // Footer
        foreach (var text in new[] { _notice, _pageLabel })
        {
            text.FontFamily = Palette.Mono;
            text.FontWeight = FontWeights.Bold;
            text.FontSize = 12;
            text.Foreground = Palette.Ink;
            text.VerticalAlignment = VerticalAlignment.Center;
        }
        _noticeBox.Child = _notice;
        _previous = Tool("‹", "Page Up", () => GoTo(_page - 1), "Previous page");
        _next = Tool("›", "Page Down", () => GoTo(_page + 1), "Next page");
        var paging = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0), Children = { _previous, _pageLabel, _next } };
        _pageLabel.Margin = new Thickness(0, 0, 8, 8);
        var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("SwivelButton") };
        cancel.Click += (_, _) => Cancel();
        _save = new Button { Content = "Save as new PDF", Style = (Style)FindResource("SwivelPrimaryButton"), Margin = new Thickness(0) };
        _save.Click += (_, _) => Save();
        var footer = new DockPanel { Margin = new Thickness(16, 8, 16, 14) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { cancel, _save } };
        DockPanel.SetDock(paging, Dock.Left);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(paging);
        footer.Children.Add(buttons);
        footer.Children.Add(_noticeBox);

        // The page: the rendered picture with the item outlines drawn over it.
        _surface.Children.Add(new Border { Background = Palette.Ink, Margin = new Thickness(6, 6, -6, -6) });
        _surface.Children.Add(new Border { BorderBrush = Palette.Ink, BorderThickness = new Thickness(2), Margin = new Thickness(-2), Child = _pageImage });
        _surface.Children.Add(_overlay);
        _overlay.MouseLeftButtonDown += OnPageDown;
        _overlay.MouseMove += OnDragMove;
        _overlay.MouseLeftButtonUp += OnDragEnd;
        _overlay.LostMouseCapture += (_, _) => { if (_dragStart != null) OnDragEnd(this, null); };
        _scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _surface,
            Background = Palette.Cream,
        };
        _scroller.PreviewMouseWheel += (_, e) =>
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
            Zoom(e.Delta > 0 ? _zoom * 1.1 : _zoom / 1.1);
            e.Handled = true;
        };

        var layout = new DockPanel();
        var top = new Border { BorderBrush = Palette.Ink, BorderThickness = new Thickness(0, 0, 0, 3), CornerRadius = new CornerRadius(13, 13, 0, 0), ClipToBounds = true, Child = titleBar };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(toolbar, Dock.Top);
        var bottom = new Border { BorderBrush = Palette.Ink, BorderThickness = new Thickness(0, 3, 0, 0), Child = footer };
        DockPanel.SetDock(bottom, Dock.Bottom);
        layout.Children.Add(top);
        layout.Children.Add(toolbar);
        layout.Children.Add(bottom);
        if (_session.PageCount > 1)
        {
            var side = new Border
            {
                BorderBrush = Palette.Ink, BorderThickness = new Thickness(0, 3, 3, 0), Width = ThumbSide + 40,
                Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _thumbs },
            };
            DockPanel.SetDock(side, Dock.Left);
            layout.Children.Add(side);
            BuildThumbnails(pdf);
        }
        layout.Children.Add(new Border { BorderBrush = Palette.Ink, BorderThickness = new Thickness(0, 3, 0, 0), Child = _scroller });

        var card = new Border { Background = Palette.Cream, BorderBrush = Palette.Ink, BorderThickness = new Thickness(3), CornerRadius = new CornerRadius(16), Child = layout, ClipToBounds = true };
        var root = new Grid { Margin = new Thickness(0, 0, 8, 8) };
        root.Children.Add(new Border { Background = Palette.Ink, CornerRadius = new CornerRadius(16), Margin = new Thickness(8, 8, -8, -8) });
        root.Children.Add(card);
        Content = root;

        PreviewKeyDown += OnKey;
        Closed += (_, _) =>
        {
            _thumbnailing.Cancel();
            _answer.TrySetResult(null);
            _session.Dispose();
        };
        Loaded += (_, _) => ShowPage();
        Notice("Double-click a line to retype it. Drag to move; pull a picture's corners to resize.", warn: false);
    }

    /// <summary>Opens the editor for <paramref name="pdf"/>. Null means cancelled.</summary>
    public static Task<IReadOnlyDictionary<string, string>?> AskAsync(string pdf)
    {
        PdfEditSession session;
        try { session = PdfEditSession.Open(pdf); }
        catch (Exception ex)
        {
            // Let the job fail with the reason, so it shows up like any other failure.
            Log.Warning(ex, "Couldn't open {Pdf} in the editor", pdf);
            string reason = ex is JobException ? ex.Message : $"Couldn't crack {Path.GetFileName(pdf)}.";
            return Task.FromResult<IReadOnlyDictionary<string, string>?>(new Dictionary<string, string> { ["error"] = reason });
        }
        var window = new EditorWindow(session, pdf);
        window.Show();
        Native.Win32.ForceForeground(new System.Windows.Interop.WindowInteropHelper(window).Handle);
        window.Activate();
        return window._answer.Task;
    }

    /// <summary>Device-independent pixels per PDF point at the current zoom.</summary>
    private double Scale => _zoom * 96 / 72;

    // ---- Showing the page -----------------------------------------------------------------------------

    private void ShowPage(PageBox? reselect = null, bool reselectText = false)
    {
        var (width, height) = _session.PageSize(_page);
        double scale = Scale;
        _surface.Width = width * scale;
        _surface.Height = height * scale;
        double dpi = 72 * scale * VisualTreeHelper.GetDpi(this).DpiScaleY;
        var render = _session.Render(_page, dpi);
        _pageBitmap = BitmapSource.Create(render.Width, render.Height, 96, 96, PixelFormats.Bgr32, null, render.Bgrx, render.Width * 4);
        _pageBitmap.Freeze();
        _pageImage.Source = _pageBitmap;
        ShowItems(reselect, reselectText);
        _pageLabel.Text = $"{_page + 1} / {_session.PageCount}";
        for (int i = 0; i < _thumbs.Children.Count; i++)
            ((Button)_thumbs.Children[i]).BorderBrush = i == _page ? Palette.Pink : Palette.Ink;
        UpdateButtons();
    }

    private void ShowItems(PageBox? reselect, bool reselectText)
    {
        _overlay.Children.Clear();
        _handles.Clear();
        _selected = null;
        int pictures = 0;
        ItemView? closest = null;
        double best = 30; // points: further than this and it's not the same thing any more
        foreach (var item in _session.Items(_page))
        {
            var view = new ItemView { Item = item, Background = Brushes.Transparent, Cursor = Cursors.SizeAll };
            view.Children.Add(view.Outline);
            AutomationProperties.SetName(view, item.IsText ? item.Text : $"Image {++pictures}");
            view.ToolTip = item.IsText ? $"{item.Font} {item.FontSize:0.#} pt · double-click to edit" : "Drag to move · corners resize";
            view.MouseEnter += (_, _) => Outline(view);
            view.MouseLeave += (_, _) => Outline(view);
            view.MouseLeftButtonDown += (_, e) => OnItemDown(view, e, handle: null);
            Place(view, item.Box);
            Outline(view);
            _overlay.Children.Add(view);

            if (reselect is { } box && item.IsText == reselectText)
            {
                double distance = Math.Abs(item.Box.Left - box.Left) + Math.Abs(item.Box.Top - box.Top);
                if (distance < best) { best = distance; closest = view; }
            }
        }
        if (closest != null) Select(closest);
    }

    private void Place(FrameworkElement view, PageBox box)
    {
        const double pad = 3; // a little slack so thin lines are easy to grab
        double s = Scale;
        Canvas.SetLeft(view, box.Left * s - pad);
        Canvas.SetTop(view, box.Top * s - pad);
        view.Width = Math.Max(8, box.Width * s + pad * 2);
        view.Height = Math.Max(8, box.Height * s + pad * 2);
    }

    private void Outline(ItemView view)
    {
        bool selected = view == _selected;
        view.Outline.Stroke = selected || view.IsMouseOver ? Palette.Pink : Palette.Ink;
        view.Outline.Opacity = selected || view.IsMouseOver ? 1 : 0.22;
        view.Outline.StrokeThickness = selected ? 2.5 : 1.5;
        view.Outline.StrokeDashArray = selected ? null : new DoubleCollection { 3, 2 };
    }

    private void Select(ItemView? view)
    {
        var previous = _selected;
        _selected = view;
        if (previous != null) Outline(previous);
        if (view != null) Outline(view);
        PlaceHandles();
        UpdateButtons();
    }

    /// <summary>Corner handles on a selected picture; dragging one resizes it, keeping its shape.</summary>
    private void PlaceHandles()
    {
        foreach (var handle in _handles) _overlay.Children.Remove(handle);
        _handles.Clear();
        if (_selected is not { Item.IsText: false } view) return;
        var box = _dragging ? _dragTo : view.Item.Box;
        double s = Scale;
        foreach (string corner in new[] { "nw", "ne", "sw", "se" })
        {
            var handle = new System.Windows.Shapes.Rectangle
            {
                Width = 12, Height = 12, Fill = Palette.Paper, Stroke = Palette.Ink, StrokeThickness = 2,
                Cursor = corner is "nw" or "se" ? Cursors.SizeNWSE : Cursors.SizeNESW,
            };
            AutomationProperties.SetName(handle, $"Resize {corner}");
            Canvas.SetLeft(handle, (corner[1] == 'w' ? box.Left : box.Right) * s - 6);
            Canvas.SetTop(handle, (corner[0] == 'n' ? box.Top : box.Bottom) * s - 6);
            handle.MouseLeftButtonDown += (_, e) => OnItemDown(view, e, corner);
            _handles.Add(handle);
            _overlay.Children.Add(handle);
        }
    }

    private void UpdateButtons()
    {
        _undo.IsEnabled = _session.CanUndo;
        _redo.IsEnabled = _session.CanRedo;
        _save.IsEnabled = _session.HasChanges;
        _delete.IsEnabled = _selected != null;
        _replace.IsEnabled = _selected is { Item.IsText: false };
        _previous.IsEnabled = _page > 0;
        _next.IsEnabled = _page < _session.PageCount - 1;
        if (_placingText) _addText.Background = Palette.Pink;
        else _addText.ClearValue(BackgroundProperty);
    }

    private void Notice(string? text, bool warn)
    {
        _notice.Text = text ?? "";
        _noticeBox.Background = warn && text != null ? Palette.Pink : Brushes.Transparent;
        _notice.Opacity = warn ? 1 : 0.7;
    }

    private void GoTo(int page)
    {
        if (page < 0 || page >= _session.PageCount || page == _page) return;
        CommitEditor();
        StartPlacingText(false);
        _page = page;
        ShowPage();
        _scroller.ScrollToTop();
    }

    private void Zoom(double zoom)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        if (Math.Abs(zoom - _zoom) < 0.001) return;
        CommitEditor();
        var keep = _selected?.Item;
        _zoom = zoom;
        ShowPage(keep?.Box, keep?.IsText ?? false);
    }

    // ---- Thumbnails -----------------------------------------------------------------------------------

    private void BuildThumbnails(string pdf)
    {
        for (int i = 0; i < _session.PageCount; i++)
        {
            int page = i;
            var picture = new Image { Stretch = Stretch.Uniform, Width = ThumbSide, Height = ThumbSide };
            _thumbImages.Add(picture);
            var button = new Button
            {
                Style = (Style)FindResource("SwivelButton"), Margin = new Thickness(0, 4, 0, 4), Padding = new Thickness(4),
                Content = new StackPanel
                {
                    Children =
                    {
                        picture,
                        new TextBlock { Text = (i + 1).ToString(), FontFamily = Palette.Mono, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center },
                    },
                },
            };
            AutomationProperties.SetName(button, $"Go to page {i + 1}");
            button.Click += (_, _) => GoTo(page);
            _thumbs.Children.Add(button);
        }

        var token = _thumbnailing.Token;
        _ = Task.Run(() => PdfPreview.Thumbnails(pdf, (int)ThumbSide * 2, (index, thumb) =>
        {
            var image = BitmapSource.Create(thumb.Width, thumb.Height, 192, 192, PixelFormats.Bgr32, null, thumb.Bgrx, thumb.Width * 4);
            image.Freeze();
            Dispatcher.InvokeAsync(() => _thumbImages[index].Source ??= image); // an edited page already has its own
        }, token), token);
    }

    /// <summary>Redraws one page's thumbnail from the edited document.</summary>
    private void RefreshThumbnail(int page)
    {
        if (page < 0 || page >= _thumbImages.Count) return;
        var (width, height) = _session.PageSize(page);
        var thumb = _session.Render(page, ThumbSide * 2 / Math.Max(width, height) * 72);
        var image = BitmapSource.Create(thumb.Width, thumb.Height, 192, 192, PixelFormats.Bgr32, null, thumb.Bgrx, thumb.Width * 4);
        image.Freeze();
        _thumbImages[page].Source = image;
    }

    // ---- Editing --------------------------------------------------------------------------------------

    private void Apply(PdfEdit edit, PageBox? reselect = null, bool reselectText = false)
    {
        try
        {
            string? note = _session.Apply(edit);
            Notice(note, warn: note != null);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "PDF edit {Edit} failed", edit.GetType().Name);
            Notice(ex is JobException ? ex.Message : "That edit didn't work on this PDF.", warn: true);
        }
        _confirmDiscard = false;
        ShowPage(reselect, reselectText);
        RefreshThumbnail(_page);
    }

    private void Undo()
    {
        CancelEditor();
        Step(_session.Undo());
    }

    private void Redo()
    {
        CancelEditor();
        Step(_session.Redo());
    }

    private void Step(int page)
    {
        if (page < 0) return;
        _confirmDiscard = false;
        Notice(null, warn: false);
        _page = page;
        ShowPage();
        RefreshThumbnail(page);
    }

    private void DeleteSelected()
    {
        if (_selected is not { } view) return;
        Apply(new DeleteItem(_page, view.Item.Id));
    }

    private void AddPicture()
    {
        var dialog = new OpenFileDialog { Title = "Add a picture", Filter = PictureFilter };
        if (dialog.ShowDialog(this) != true) return;
        CommitEditor();
        // Centred in the visible part of the page, at most half its width; it keeps its own shape inside.
        var (width, height) = _session.PageSize(_page);
        double side = Math.Min(width, height) * 0.4;
        double s = Scale;
        double centreX = Math.Clamp((_scroller.HorizontalOffset + _scroller.ViewportWidth / 2 - 24) / s, side / 2, width - side / 2);
        double centreY = Math.Clamp((_scroller.VerticalOffset + _scroller.ViewportHeight / 2 - 24) / s, side / 2, height - side / 2);
        var box = new PageBox(centreX - side / 2, centreY - side / 2, centreX + side / 2, centreY + side / 2);
        Apply(new AddImage(_page, dialog.FileName, box), box, reselectText: false);
    }

    private void ReplacePicture()
    {
        if (_selected is not { Item.IsText: false } view) return;
        var dialog = new OpenFileDialog { Title = "Replace the picture", Filter = PictureFilter };
        if (dialog.ShowDialog(this) != true) return;
        Apply(new ReplaceImage(_page, view.Item.Id, dialog.FileName), view.Item.Box, reselectText: false);
    }

    private void Nudge(double dx, double dy)
    {
        if (_selected is not { } view) return;
        var b = view.Item.Box;
        var to = new PageBox(b.Left + dx, b.Top + dy, b.Right + dx, b.Bottom + dy);
        Apply(new MoveItem(_page, view.Item.Id, to), to, view.Item.IsText);
    }

    // ---- Mouse: select, drag, resize, place -----------------------------------------------------------

    private void OnItemDown(ItemView view, MouseButtonEventArgs e, string? handle)
    {
        e.Handled = true;
        if (_editor != null) { CommitEditor(); return; } // the page was redrawn; this view is stale
        if (_placingText) StartPlacingText(false);
        Select(view);
        if (e.ClickCount == 2 && handle is null && view.Item.IsText)
        {
            OpenEditor(view.Item, new Point(view.Item.Box.Left, view.Item.Box.Top));
            return;
        }
        _dragStart = e.GetPosition(_overlay);
        _dragHandle = handle;
        _dragFrom = _dragTo = view.Item.Box;
        _dragging = false;
        _overlay.CaptureMouse();
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || _selected is not { } view) return;
        var now = e.GetPosition(_overlay);
        if (!_dragging)
        {
            if (Math.Abs(now.X - start.X) < 4 && Math.Abs(now.Y - start.Y) < 4) return;
            _dragging = true;
            Ghost(view);
        }
        double dx = (now.X - start.X) / Scale, dy = (now.Y - start.Y) / Scale;
        var f = _dragFrom;
        _dragTo = _dragHandle is null ? new PageBox(f.Left + dx, f.Top + dy, f.Right + dx, f.Bottom + dy) : Resized(f, _dragHandle, dx);
        Place(view, _dragTo);
        PlaceHandles();
    }

    /// <summary>While dragging, the item carries a copy of how it looks, so you see what you're moving.</summary>
    private void Ghost(ItemView view)
    {
        if (_pageBitmap is null) return;
        double perPoint = _pageBitmap.PixelWidth / _session.PageSize(_page).Width;
        var b = view.Item.Box;
        int x = (int)Math.Clamp(b.Left * perPoint, 0, _pageBitmap.PixelWidth - 1), y = (int)Math.Clamp(b.Top * perPoint, 0, _pageBitmap.PixelHeight - 1);
        int w = (int)Math.Clamp(b.Width * perPoint, 1, _pageBitmap.PixelWidth - x), h = (int)Math.Clamp(b.Height * perPoint, 1, _pageBitmap.PixelHeight - y);
        // The view is padded by 3 on each side; inset the copy so it lines up with the item itself.
        var brush = new ImageBrush(new CroppedBitmap(_pageBitmap, new Int32Rect(x, y, w, h))) { Opacity = 0.75, ViewportUnits = BrushMappingMode.Absolute };
        view.Background = brush;
        view.SizeChanged += (_, _) => brush.Viewport = new Rect(3, 3, Math.Max(1, view.Width - 6), Math.Max(1, view.Height - 6));
        brush.Viewport = new Rect(3, 3, Math.Max(1, view.Width - 6), Math.Max(1, view.Height - 6));
    }

    /// <summary>A picture pulled by one corner keeps its shape; the opposite corner stays put.</summary>
    private static PageBox Resized(PageBox from, string corner, double dx)
    {
        bool left = corner[1] == 'w', top = corner[0] == 'n';
        double width = Math.Max(12, from.Width + (left ? -dx : dx));
        double height = width * from.Height / Math.Max(0.01, from.Width);
        double l = left ? from.Right - width : from.Left;
        double t = top ? from.Bottom - height : from.Top;
        return new PageBox(l, t, l + width, t + height);
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs? e)
    {
        if (_dragStart is null) return;
        _dragStart = null;
        if (_overlay.IsMouseCaptured) _overlay.ReleaseMouseCapture();
        bool moved = _dragging;
        _dragging = false;
        if (moved && _selected is { } view) Apply(new MoveItem(_page, view.Item.Id, _dragTo), _dragTo, view.Item.IsText);
    }

    private void OnPageDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || e.OriginalSource != _overlay) return;
        if (_editor != null) { CommitEditor(); return; }
        if (_placingText)
        {
            var at = e.GetPosition(_overlay);
            OpenEditor(null, new Point(at.X / Scale, at.Y / Scale - NewTextSize * 0.6));
            e.Handled = true;
            return;
        }
        Select(null);
    }

    private void StartPlacingText(bool on)
    {
        _placingText = on;
        _overlay.Cursor = on ? Cursors.IBeam : null;
        if (on) Notice("Click on the page where the new text should go.", warn: false);
        UpdateButtons();
    }

    // ---- Typing in place ------------------------------------------------------------------------------

    /// <summary>A text box over the line (or at the click, for new text) in about the same font and size.</summary>
    private void OpenEditor(PageItem? item, Point at)
    {
        double size = item?.FontSize ?? NewTextSize;
        string family = item is { Font.Length: > 0 } ? $"{item.Font}, Arial" : "Arial";
        var box = new TextBox
        {
            Text = item?.Text ?? "",
            FontFamily = new FontFamily(family),
            FontSize = Math.Max(6, size * Scale),
            FontWeight = item?.Bold == true ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = item?.Italic == true ? FontStyles.Italic : FontStyles.Normal,
            Foreground = Brushes.Black,
            Background = Brushes.White,
            BorderBrush = Palette.Pink,
            BorderThickness = new Thickness(2),
            Padding = new Thickness(0),
            MinWidth = Math.Max(120, (item?.Box.Width ?? 0) * Scale + 32),
        };
        AutomationProperties.SetName(box, item is null ? "New text" : "Edit text");
        Canvas.SetLeft(box, at.X * Scale - 3);
        Canvas.SetTop(box, at.Y * Scale - 4);
        box.LostKeyboardFocus += (_, _) => { if (_editor == box && IsActive) CommitEditor(); };
        _editor = box;
        _editorItem = item;
        _editorAt = at;
        _overlay.Children.Add(box);
        Notice(item is null ? "Type, then Enter to place it. Esc to cancel." : "Enter to keep the change. Esc to cancel.", warn: false);
        Dispatcher.InvokeAsync(() => { box.Focus(); if (item is null) box.CaretIndex = 0; else box.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void CommitEditor()
    {
        if (_editor is not { } box) return;
        _editor = null;
        _overlay.Children.Remove(box);
        string text = box.Text.TrimEnd();
        var item = _editorItem;
        StartPlacingText(false);
        Keyboard.Focus(this);
        if (item is not null)
        {
            if (text != item.Text) Apply(new EditText(_page, item.Id, text), item.Box, reselectText: true);
            else Notice(null, warn: false);
        }
        else if (text.Trim().Length > 0)
        {
            var at = _editorAt;
            Apply(new AddText(_page, at.X, at.Y, text, NewTextSize, Bold: false), new PageBox(at.X, at.Y, at.X + 1, at.Y + 1), reselectText: true);
        }
    }

    private void CancelEditor()
    {
        if (_editor is not { } box) return;
        _editor = null;
        _overlay.Children.Remove(box);
        StartPlacingText(false);
        Notice(null, warn: false);
        Keyboard.Focus(this);
    }

    // ---- Keys, saving, closing ------------------------------------------------------------------------

    private void OnKey(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (_editor != null)
        {
            switch (e.Key)
            {
                case Key.Enter: CommitEditor(); break;
                case Key.Escape: CancelEditor(); break;
                default: return;
            }
            e.Handled = true;
            return;
        }
        double step = shift ? 10 : 1;
        switch (e.Key)
        {
            case Key.Z when ctrl && shift: Redo(); break;
            case Key.Z when ctrl: Undo(); break;
            case Key.Y when ctrl: Redo(); break;
            case Key.S when ctrl: Save(); break;
            case Key.OemPlus or Key.Add when ctrl: Zoom(_zoom * 1.25); break;
            case Key.OemMinus or Key.Subtract when ctrl: Zoom(_zoom / 1.25); break;
            case Key.Delete or Key.Back: DeleteSelected(); break;
            case Key.T when !ctrl: StartPlacingText(!_placingText); break;
            case Key.F2 or Key.Enter when _selected is { Item.IsText: true } view: OpenEditor(view.Item, new Point(view.Item.Box.Left, view.Item.Box.Top)); break;
            case Key.Left when _selected != null: Nudge(-step, 0); break;
            case Key.Right when _selected != null: Nudge(step, 0); break;
            case Key.Up when _selected != null: Nudge(0, -step); break;
            case Key.Down when _selected != null: Nudge(0, step); break;
            case Key.PageUp: GoTo(_page - 1); break;
            case Key.PageDown: GoTo(_page + 1); break;
            case Key.Escape when _placingText: StartPlacingText(false); Notice(null, warn: false); break;
            case Key.Escape when _selected != null: Select(null); break;
            case Key.Escape: Cancel(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Save()
    {
        CommitEditor();
        if (!_session.HasChanges) return;
        string folder = Path.Combine(AppPaths.Drops, Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, Path.GetFileNameWithoutExtension(_name) + " edited.pdf");
        try
        {
            Directory.CreateDirectory(folder);
            _session.Save(path);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Couldn't save the edited PDF");
            Notice(ex is JobException ? ex.Message : "Couldn't write the edited PDF.", warn: true);
            return;
        }
        Finish(new Dictionary<string, string> { ["edited"] = path });
    }

    /// <summary>Closing with unsaved edits asks once, in the footer, rather than with a dialog.</summary>
    private void Cancel()
    {
        CancelEditor();
        if (_session.HasChanges && !_confirmDiscard)
        {
            _confirmDiscard = true;
            Notice("You have unsaved edits. Cancel again to throw them away, or Save as new PDF.", warn: true);
            return;
        }
        Finish(null);
    }

    private void Finish(IReadOnlyDictionary<string, string>? answer)
    {
        _answer.TrySetResult(answer);
        Close();
    }

    private Button Tool(string label, string? key, Action action, string? name = null)
    {
        var button = new Button
        {
            Content = label, Style = (Style)FindResource("SwivelButton"), Margin = new Thickness(0, 0, 8, 8),
            ToolTip = key is null ? name : $"{name ?? label.TrimEnd('…')} ({key})",
        };
        if (name != null) AutomationProperties.SetName(button, name);
        button.Click += (_, _) => action();
        return button;
    }
}
