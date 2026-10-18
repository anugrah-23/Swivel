using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Serilog;
using Swivel.App.Services;
using Swivel.App.Theme;
using Swivel.Core.Catalog;
using Swivel.Core.Files;
using Swivel.Engines.Images;
using Swivel.Native;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using IDataObject = System.Windows.IDataObject;

namespace Swivel.App.Overlay;

/// <summary>A drop on a wedge: what to run, on which files, and where the wheel's centre was on screen.</summary>
public sealed record WheelDrop(Operation Operation, IReadOnlyList<string> Files, Point ScreenCentre, double Scale);

/// <summary>
/// The transparent, never-activated, topmost wheel. It is created once and parked off-screen so the
/// trigger tap only has to move and reveal it, which keeps open latency well under 100 ms.
/// </summary>
public partial class OverlayWindow : Window
{
    private const double LogicalWidth = 420, LogicalHeight = 640;
    private const double CentreX = 210, CentreY = 220;
    private const double RingRadius = 118, HubRadius = 56, OuterRadius = 192, RimRadius = 165;
    private const int Parked = -32000;
    private static readonly TimeSpan MoreDwell = TimeSpan.FromMilliseconds(450);

    private static readonly Brush CatchAll = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255));

    private readonly SwivelServices _services;
    private readonly WedgeTile[] _tiles = new WedgeTile[OperationCatalog.WedgesPerRing];
    private readonly DispatcherTimer _releasePoll;
    private readonly DispatcherTimer _moreTimer;
    private readonly Dictionary<WheelMode, IReadOnlyList<IReadOnlyList<Operation?>>> _rings = [];
    private nint _hwnd;
    private Win32.RECT _target;
    private double _scale = 1;
    private bool _open, _dragInside, _dropping;
    private WheelMode _mode;
    private int _ring;
    private int _active = -1;
    private long _openedAt;
    private double? _lastOpenMs;
    private bool _elevatedSource;
    private DragSession? _session;
    private IReadOnlyDictionary<string, int> _usage = new Dictionary<string, int>();

    static OverlayWindow() => CatchAll.Freeze();

    public OverlayWindow(SwivelServices services)
    {
        _services = services;
        InitializeComponent();

        for (int i = 0; i < _tiles.Length; i++)
        {
            var tile = new WedgeTile();
            double angle = i * Math.PI / 4;
            Canvas.SetLeft(tile, CentreX + RingRadius * Math.Sin(angle) - WedgeTile.TileWidth / 2);
            Canvas.SetTop(tile, CentreY - RingRadius * Math.Cos(angle) - WedgeTile.TileHeight / 2);
            Ring.Children.Add(tile);
            _tiles[i] = tile;
        }
        RimArc.Data = BuildRimArc();
        Pill.SizeChanged += (_, _) => CentrePill();

        _releasePoll = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(30) };
        _releasePoll.Tick += OnReleasePoll;
        _moreTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = MoreDwell };
        _moreTimer.Tick += (_, _) => { _moreTimer.Stop(); if (IsMore(_active)) NextRing(); };

        SourceInitialized += OnSourceInitialized;
        DpiChanged += (_, _) => { if (_open) Dispatcher.InvokeAsync(ApplyTargetRect, DispatcherPriority.Send); };

        Root.DragEnter += OnDragEnter;
        Root.DragOver += OnDragOver;
        Root.DragLeave += OnDragLeave;
        Root.Drop += OnDrop;

        ShowRing();
    }

    /// <summary>Raised on the UI thread when the user lets go on a wedge.</summary>
    public event Action<WheelDrop>? Dropped;

    /// <summary>Creates the HWND and runs one layout pass off-screen so the first gesture is fast.</summary>
    public void Prewarm()
    {
        Show();
        Root.Visibility = Visibility.Visible;
        UpdateLayout();
        Root.Visibility = Visibility.Hidden;
    }

    public void OnSignal(HookSignal signal, long timestamp)
    {
        switch (signal)
        {
            case HookSignal.TriggerTap when !_open:
                Open(timestamp);
                break;
            case HookSignal.TriggerTap when !_dropping:
                CycleMode();
                break;
            case HookSignal.Left or HookSignal.Up when _open && !_dropping:
                Step(-1);
                break;
            case HookSignal.Right or HookSignal.Down when _open && !_dropping:
                Step(+1);
                break;
            case HookSignal.Enter when _open && !_dragInside && !_dropping && _active >= 0:
                if (IsMore(_active)) NextRing();
                else if (_session?.Paths is { Count: > 0 } paths) Commit(CurrentRing()[_active]!, paths);
                break;
            case HookSignal.Escape when _open:
                Log.Debug("Wheel cancelled with Esc");
                Dismiss();
                break;
        }
    }

    /// <summary>
    /// Renders the wheel for <paramref name="files"/> to a PNG without showing it, for design review
    /// and screenshots (<c>Swivel.exe --snapshot out-dir file…</c>).
    /// </summary>
    public void RenderSnapshot(IReadOnlyList<string> files, WheelMode mode, int active, string outputPath)
    {
        _open = true;
        _session = null;
        _rings.Clear();
        _active = -1;
        _usage = _services.Usage();
        ResetHub();
        StartSession(new DataObject(DataFormats.FileDrop, files.ToArray()));
        _session?.LoadDetailsAsync().Wait();
        if (_session != null) ShowHub();
        _mode = mode;
        _ring = 0;
        ShowRing();
        _dragInside = true;
        Highlight(active);
        _dragInside = false;
        _moreTimer.Stop();
        foreach (var tile in _tiles) tile.PopIn(TimeSpan.Zero);

        Root.Background = Brushes.Transparent;
        Root.Visibility = Visibility.Visible;
        Root.Measure(new Size(LogicalWidth, LogicalHeight));
        Root.Arrange(new Rect(0, 0, LogicalWidth, LogicalHeight));
        Root.UpdateLayout();
        CentrePill();
        Root.UpdateLayout();

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(840, 1280, 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(Root);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = File.Create(outputPath);
        encoder.Save(file);
        Root.Visibility = Visibility.Hidden;
        _open = false;
    }

    // ---- Window placement -----------------------------------------------------------------------

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        nint style = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE);
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, style | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW);
    }

    private void Open(long timestamp)
    {
        _openedAt = timestamp;
        Win32.GetCursorPos(out var cursor);
        var (work, scale) = Win32.MonitorAt(cursor);
        _scale = scale;
        int width = (int)Math.Round(LogicalWidth * scale), height = (int)Math.Round(LogicalHeight * scale);
        int x = cursor.X - (int)Math.Round(CentreX * scale), y = cursor.Y - (int)Math.Round(CentreY * scale);
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height));
        _target = new Win32.RECT { Left = x, Top = y, Right = x + width, Bottom = y + height };

        _open = true;
        _dragInside = _dropping = false;
        TriggerHook.OverlayOpen = true;
        _elevatedSource = ProcessElevation.IsForegroundElevatedAboveUs();
        _session = null;
        _rings.Clear();
        _mode = WheelMode.Convert;
        _ring = 0;
        _active = -1;
        _usage = _services.Usage();

        ResetHub();
        ShowRing();
        foreach (var tile in _tiles) tile.ResetPunch();

        ApplyTargetRect();
        Root.Background = CatchAll;
        Root.Visibility = Visibility.Visible;
        PlayOpen();

        _releasePoll.Start();
        CompositionTarget.Rendering += OnFirstFrame;
    }

    private void Dismiss()
    {
        if (!_open) return;
        _open = _dragInside = _dropping = false;
        TriggerHook.OverlayOpen = false;
        _releasePoll.Stop();
        _moreTimer.Stop();
        Root.Visibility = Visibility.Hidden;
        Root.Background = Brushes.Transparent;
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, Parked, Parked,
            _target.Right - _target.Left, _target.Bottom - _target.Top, Win32.SWP_NOACTIVATE);
    }

    private void ApplyTargetRect() =>
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, _target.Left, _target.Top,
            _target.Right - _target.Left, _target.Bottom - _target.Top, Win32.SWP_NOACTIVATE);

    private Point ScreenCentre => new(_target.Left + CentreX * _scale, _target.Top + CentreY * _scale);

    private void OnFirstFrame(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= OnFirstFrame;
        _lastOpenMs = Stopwatch.GetElapsedTime(_openedAt).TotalMilliseconds;
        Log.Information("Wheel opened in {Ms:F1} ms (elevated source: {Elevated})", _lastOpenMs, _elevatedSource);
        UpdateHints();
    }

    /// <summary>Closes silently when the button is released without a drag ever reaching the wheel.</summary>
    private void OnReleasePoll(object? sender, EventArgs e)
    {
        if (!_open) { _releasePoll.Stop(); return; }
        if (!_dragInside && !_dropping && !Win32.PrimaryButtonDown())
        {
            Log.Debug("Wheel closed: button released without a drop");
            Dismiss();
        }
    }

    // ---- Drag and drop --------------------------------------------------------------------------

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        _dragInside = true;
        if (_session == null) StartSession(e.Data);
        UpdateDrag(e);
    }

    private void OnDragOver(object sender, DragEventArgs e) => UpdateDrag(e);

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        _dragInside = false;
        if (!_dropping) Highlight(-1);
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        _dragInside = false;
        int wedge = HitTest(e.GetPosition(Root));
        e.Handled = true;
        if (!_open || _dropping || wedge < 0 || IsMore(wedge))
        {
            e.Effects = DragDropEffects.None;
            Log.Debug("Drop outside a wedge: cancelled");
            Dismiss();
            return;
        }
        var operation = CurrentRing()[wedge]!;
        var files = CollectFiles(e.Data);
        e.Effects = SafeEffect(e.AllowedEffects);
        if (files.Count == 0)
        {
            Log.Warning("Drop carried no readable files");
            Dismiss();
            return;
        }
        Highlight(wedge);
        Commit(operation, files);
    }

    private void UpdateDrag(DragEventArgs e)
    {
        int wedge = _dropping ? _active : HitTest(e.GetPosition(Root));
        if (!_dropping) Highlight(wedge);
        e.Effects = wedge >= 0 && !IsMore(wedge) ? SafeEffect(e.AllowedEffects) : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Never report Move: a source would then delete the original.</summary>
    private static DragDropEffects SafeEffect(DragDropEffects allowed) =>
        allowed.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy
        : allowed.HasFlag(DragDropEffects.Link) ? DragDropEffects.Link
        : DragDropEffects.None;

    private int HitTest(Point p)
    {
        double dx = p.X - CentreX, dy = p.Y - CentreY;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance <= HubRadius || distance > OuterRadius) return -1;
        double degrees = (Math.Atan2(dx, -dy) * 180 / Math.PI + 360) % 360;
        int wedge = (int)((degrees + 22.5) / 45) % 8;
        return wedge < CurrentRing().Count ? wedge : -1;
    }

    private static IReadOnlyList<string> CollectFiles(IDataObject data)
    {
        try
        {
            if (data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths) return paths;

            if (data.GetDataPresent(VirtualFiles.DescriptorFormat)
                && data.GetData(VirtualFiles.DescriptorFormat) is MemoryStream descriptor
                && data is ComDataObject com)
            {
                var entries = VirtualFiles.ReadDescriptor(descriptor);
                string folder = Path.Combine(AppPaths.Drops, Guid.NewGuid().ToString("N"));
                return VirtualFiles.Extract(com, entries, folder);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not read the dropped data");
        }
        return [];
    }

    private void Commit(Operation operation, IReadOnlyList<string> files)
    {
        _dropping = true;
        _moreTimer.Stop();
        _tiles[_active].Punch();
        Dropped?.Invoke(new WheelDrop(operation, files, ScreenCentre, _scale));

        var shrink = new QuadraticEase { EasingMode = EasingMode.EaseIn };
        Palette.To(WheelScale, ScaleTransform.ScaleXProperty, 0.15, 160, shrink, TimeSpan.FromMilliseconds(60));
        Palette.To(WheelScale, ScaleTransform.ScaleYProperty, 0.15, 160, shrink, TimeSpan.FromMilliseconds(60));
        var done = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Palette.Animations ? 230 : 0) };
        done.Tick += (_, _) => { done.Stop(); Dismiss(); };
        done.Start();
    }

    // ---- Session and rings ----------------------------------------------------------------------

    private void StartSession(IDataObject data)
    {
        try
        {
            _session = DragSession.From(data);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read drag contents");
            _session = null;
        }

        if (_session == null)
        {
            HubCount.Text = "?";
            HubName.Text = "Not a file";
            HubMeta.Text = "Nothing to spin";
            foreach (var mode in Enum.GetValues<WheelMode>()) _rings[mode] = [[]];
            ShowRing();
            return;
        }

        var hidden = _services.Settings.Current.HiddenOperations.ToHashSet();
        foreach (var mode in Enum.GetValues<WheelMode>())
        {
            var ops = _services.Catalog.For(_session.Kinds, mode, _usage).Where(o => !hidden.Contains(o.Id)).ToArray();
            _rings[mode] = OperationCatalog.Rings(ops);
        }
        _mode = HasOperations(WheelMode.Convert) ? WheelMode.Convert
            : HasOperations(WheelMode.Tools) ? WheelMode.Tools
            : WheelMode.Actions;
        _ring = 0;
        ShowHub();
        ShowRing();

        var session = _session;
        session.LoadDetailsAsync().ContinueWith(_ => Dispatcher.InvokeAsync(() =>
        {
            if (_session != session || !_open) return;
            ShowHub();
            UpdateCard();
        }), TaskScheduler.Default);
    }

    private IReadOnlyList<Operation?> CurrentRing() =>
        _rings.TryGetValue(_mode, out var rings) && rings.Count > 0 ? rings[Math.Min(_ring, rings.Count - 1)] : [];

    private bool HasOperations(WheelMode mode) => _rings.TryGetValue(mode, out var r) && r.Any(ring => ring.Count > 0);

    private bool IsMore(int wedge) => wedge >= 0 && wedge < CurrentRing().Count && CurrentRing()[wedge] is null;

    private void CycleMode()
    {
        var modes = Enum.GetValues<WheelMode>();
        for (int step = 1; step <= modes.Length; step++)
        {
            var next = modes[((int)_mode + step) % modes.Length];
            if (_session == null || HasOperations(next) || next == _mode)
            {
                if (next == _mode) return;
                _mode = next;
                break;
            }
        }
        _ring = 0;
        int keep = _active;
        _active = -1;
        ShowRing(spin: true);
        if (!_dragInside && keep >= 0) Highlight(Math.Min(keep, CurrentRing().Count - 1));
    }

    private void NextRing()
    {
        int count = _rings.TryGetValue(_mode, out var rings) ? rings.Count : 1;
        if (count <= 1) return;
        _ring = (_ring + 1) % count;
        _active = -1;
        ShowRing(spin: true);
    }

    private void Step(int delta)
    {
        int count = CurrentRing().Count;
        if (count == 0) return;
        Highlight(_active < 0 ? (delta > 0 ? 0 : count - 1) : (_active + delta + count) % count);
    }

    // ---- Visual state ---------------------------------------------------------------------------

    private void ShowRing(bool spin = false)
    {
        Disc.Fill = _mode switch
        {
            WheelMode.Convert => Palette.Sunflower,
            WheelMode.Tools => Palette.Pink,
            _ => Palette.Sky,
        };
        var tileColours = new[] { Palette.Sky, Palette.Mint, Palette.Orange, Palette.Lilac, Palette.Pink, Palette.Sunflower, Palette.Paper }
            .Where(b => b != Disc.Fill).ToArray();

        var ring = CurrentRing();
        for (int i = 0; i < _tiles.Length; i++)
        {
            _tiles[i].SetDisc(Disc.Fill);
            if (_session == null && _rings.Count == 0) _tiles[i].SetNeutral();
            else if (i >= ring.Count) _tiles[i].Visibility = Visibility.Hidden;
            else if (ring[i] is { } op) _tiles[i].SetContent(op.Label, op.Hint, tileColours[i % tileColours.Length], op.AutomationName);
            else _tiles[i].SetContent("MORE", NextRingHint(), Palette.Paper, "More options");
            _tiles[i].SetActive(i == _active);
        }

        BuildPill();
        UpdateCard();
        if (spin) Palette.To(RingSpin, RotateTransform.AngleProperty, 0, 160, from: -45);
    }

    private string NextRingHint()
    {
        int count = _rings.TryGetValue(_mode, out var rings) ? rings.Count : 1;
        return $"{(_ring + 1) % count + 1} of {count}";
    }

    private void Highlight(int wedge)
    {
        if (wedge == _active) return;
        _active = wedge;
        for (int i = 0; i < _tiles.Length; i++) _tiles[i].SetActive(i == wedge);
        if (wedge < 0)
        {
            RimArc.Visibility = Visibility.Hidden;
        }
        else
        {
            bool wasHidden = RimArc.Visibility != Visibility.Visible;
            RimArc.Visibility = Visibility.Visible;
            Palette.To(RimSpin, RotateTransform.AngleProperty, wedge * 45, wasHidden ? 0 : 80);
        }
        if (IsMore(wedge) && _dragInside) _moreTimer.Start(); else _moreTimer.Stop();
        UpdateCard();
    }

    private void UpdateCard()
    {
        if (_dropping) return;
        Sticker.Visibility = Visibility.Hidden;
        var ring = CurrentRing();

        if (_session == null || _active < 0 || _active >= ring.Count)
        {
            CardTitle.Text = _mode.ToString().ToUpperInvariant();
            CardDetail.Text = _session == null ? "Bring a file in." : ring.Count == 0 ? "Nothing here fits these files." : "Hover a wedge to preview it.";
            CardCta.Text = "Let go off the wheel to cancel";
            return;
        }

        if (ring[_active] is not { } op)
        {
            CardTitle.Text = "MORE";
            CardDetail.Text = "Hold here to see the next set.";
            CardCta.Text = "Or press Enter";
            return;
        }

        CardTitle.Text = op.Id.StartsWith("preset.", StringComparison.Ordinal) ? $"{op.Label} PRESET"
            : op.Mode == WheelMode.Convert ? $"TO {op.Label}"
            : op.Label.ToUpperInvariant();
        CardDetail.Text = Describe(op);
        CardCta.Text = "Let go to apply";
    }

    private string Describe(Operation op)
    {
        string files = _session!.Count == 1 ? "" : $" · {_session.Count} files";
        if (op.ProducesNoOutput || op.CombinesInputs || op.OutputIsFolder) return op.Hint + files;

        long? estimate = _session.Probe is { } probe ? ImageEstimator.Estimate(probe, op)
            : _session.Media is { } media ? media.Estimate(op)
            : null;
        if (estimate is not long each)
        {
            bool probing = _session.Paths != null && !_session.DetailsLoaded
                && FileKinds.Family(_session.Kinds[0]) is FileFamily.Image or FileFamily.Audio or FileFamily.Video;
            return probing ? $"{op.Hint}{files} · sizing it up…" : op.Hint + files;
        }

        long total = each * _session.Count;
        if (_session.TotalBytes is long before && before > 0 && total < before)
        {
            StickerText.Text = $"−{Math.Round(100.0 * (before - total) / before):F0}%";
            Sticker.Visibility = Visibility.Visible;
        }
        return $"{op.Hint}{files} · ≈ {Format.Size(total)}";
    }

    private void ResetHub()
    {
        HubThumbFrame.Visibility = HubStack.Visibility = HubBadge.Visibility = Visibility.Collapsed;
        HubCount.Visibility = Visibility.Visible;
        HubCount.Text = "···";
        HubName.Text = "";
        HubMeta.Text = _elevatedSource ? "Admin window: drag can't reach Swivel" : "";
        UpdateHints();
    }

    private void ShowHub()
    {
        var session = _session!;
        string ext = Path.GetExtension(session.Names[0]).TrimStart('.').ToUpperInvariant();
        bool many = session.Count > 1;

        if (session.Thumbnail is { } thumb)
        {
            HubThumb.ImageSource = thumb;
            HubThumbFrame.Visibility = Visibility.Visible;
            HubCount.Visibility = Visibility.Collapsed;
        }
        else
        {
            HubThumbFrame.Visibility = Visibility.Collapsed;
            HubCount.Visibility = Visibility.Visible;
            HubCount.Text = session.Kinds[0] == FileKind.Folder ? "DIR" : ext.Length is > 0 and <= 5 ? ext : "FILE";
        }
        HubStack.Visibility = many ? Visibility.Visible : Visibility.Collapsed;
        HubBadge.Visibility = many ? Visibility.Visible : Visibility.Collapsed;
        HubBadgeText.Text = $"×{session.Count}";

        HubName.Text = many ? $"{session.Count} files" : session.Names[0];
        HubMeta.Text = _elevatedSource ? "Admin window: drag can't reach Swivel"
            : session.TotalBytes is long bytes ? $"{(many ? "" : ext + " · ")}{Format.Size(bytes)}"
            : many ? "" : ext;
    }

    private void BuildPill()
    {
        PillPanel.Children.Clear();
        foreach (var mode in Enum.GetValues<WheelMode>())
        {
            bool current = mode == _mode;
            bool available = _session == null || HasOperations(mode);
            PillPanel.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 2, 10, 2),
                Background = current ? Palette.Ink : Brushes.Transparent,
                Opacity = available ? 1 : 0.35,
                Child = new TextBlock
                {
                    Text = mode.ToString().ToUpperInvariant(),
                    FontFamily = Palette.Display,
                    FontWeight = FontWeights.Black,
                    FontSize = 12,
                    Foreground = current ? Palette.Sunflower : Palette.Ink,
                },
            });
        }
    }

    private void CentrePill()
    {
        double left = (LogicalWidth - Pill.ActualWidth) / 2;
        Canvas.SetLeft(Pill, left);
        Canvas.SetLeft(PillShadow, left + 5);
        PillShadow.Width = Pill.ActualWidth;
    }

    private void UpdateHints()
    {
        string hints = $"{TriggerKeys.Label(_services.Settings.Current.Trigger)} mode   ← → pick   Esc cancel";
#if DEBUG
        if (_lastOpenMs is double ms) hints += $"   · {ms:F0} ms";
#endif
        KeyHints.Text = hints;
    }

    private void PlayOpen()
    {
        WheelScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        WheelScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        WheelScale.ScaleX = WheelScale.ScaleY = 1;
        var overshoot = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut };
        Palette.To(WheelScale, ScaleTransform.ScaleXProperty, 1, 140, overshoot, from: 0.85);
        Palette.To(WheelScale, ScaleTransform.ScaleYProperty, 1, 140, overshoot, from: 0.85);
        for (int i = 0; i < _tiles.Length; i++) _tiles[i].PopIn(TimeSpan.FromMilliseconds(15 * i));
    }

    private static Geometry BuildRimArc()
    {
        static Point On(double degrees) => new(
            CentreX + RimRadius * Math.Sin(degrees * Math.PI / 180),
            CentreY - RimRadius * Math.Cos(degrees * Math.PI / 180));
        var figure = new PathFigure { StartPoint = On(-18), IsClosed = false };
        figure.Segments.Add(new ArcSegment(On(18), new Size(RimRadius, RimRadius), 0, false, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }
}
