using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Swivel.App.Theme;
using Swivel.Core.Catalog;
using Swivel.Engines.Images;
using Swivel.Engines.Media;
using Swivel.Native;

namespace Swivel.App.Feedback;

/// <summary>
/// The small panel that follows a drop on Resize, Crop or Trim (and Copy/Move to without a pinned
/// folder), asking only what it must. Enter applies, Esc cancels.
/// </summary>
public sealed class PanelWindow : Window
{
    private readonly TaskCompletionSource<IReadOnlyDictionary<string, string>?> _answer = new();
    private readonly StackPanel _body = new();
    private Func<IReadOnlyDictionary<string, string>?>? _collect;

    private PanelWindow(string title, string subtitle)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = title;

        var apply = new Button { Content = "Apply", Style = (Style)FindResource("SwivelPrimaryButton"), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("SwivelButton"), IsCancel = true };
        apply.Click += (_, _) => Finish(_collect?.Invoke());
        cancel.Click += (_, _) => Finish(null);

        var header = new Border
        {
            Background = Palette.Sunflower,
            BorderBrush = Palette.Ink,
            BorderThickness = new Thickness(0, 0, 0, 3),
            CornerRadius = new CornerRadius(9, 9, 0, 0),
            Padding = new Thickness(16, 10, 16, 10),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = title, FontFamily = Palette.Display, FontWeight = FontWeights.Black, FontSize = 20, Foreground = Palette.Ink },
                    new TextBlock { Text = subtitle, FontFamily = Palette.Body, FontSize = 12, Foreground = Palette.Ink, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 340 },
                },
            },
        };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, -8, 0), Children = { cancel, apply } };
        var card = new Border
        {
            Background = Palette.Paper,
            BorderBrush = Palette.Ink,
            BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(12),
            MinWidth = 360,
            Child = new StackPanel { Children = { header, new StackPanel { Margin = new Thickness(16, 14, 16, 16), Children = { _body, footer } } } },
        };
        var root = new Grid { Margin = new Thickness(0, 0, 5, 5) };
        root.Children.Add(new Border { Background = Palette.Ink, CornerRadius = new CornerRadius(12), Margin = new Thickness(5, 5, -5, -5) });
        root.Children.Add(card);
        Content = root;

        header.MouseLeftButtonDown += (_, _) => DragMove();
        Closed += (_, _) => _answer.TrySetResult(null);
    }

    /// <summary>Asks for an operation's details. Null means the user cancelled.</summary>
    public static Task<IReadOnlyDictionary<string, string>?> AskAsync(Operation op, IReadOnlyList<string> files, Point screenCentre)
    {
        string subtitle = files.Count == 1 ? Path.GetFileName(files[0].TrimEnd('\\')) : $"{files.Count} files";
        if (op.Panel == "folder") return Task.FromResult(AskFolder(op, files));
        if (op.Panel == "pages") return PagesWindow.AskAsync(files);

        var panel = new PanelWindow(op.Label.ToUpperInvariant(), subtitle);
        switch (op.Panel)
        {
            case "resize": panel.BuildResize(files); break;
            case "crop": panel.BuildCrop(); break;
            case "trim": panel.BuildTrim(files[0]); break;
            default: return Task.FromResult<IReadOnlyDictionary<string, string>?>(new Dictionary<string, string>());
        }
        panel.ShowNear(screenCentre);
        return panel._answer.Task;
    }

    /// <summary>Renders a panel to a PNG for design review (<c>--snapshot</c>), without showing it.</summary>
    internal static void Snapshot(string panelKind, string file, string outputPath)
    {
        var panel = new PanelWindow(panelKind.ToUpperInvariant(), Path.GetFileName(file));
        switch (panelKind)
        {
            case "resize": panel.BuildResize([file]); break;
            case "crop": panel.BuildCrop(); break;
            case "trim": panel.BuildTrim(file); break;
        }
        var content = (FrameworkElement)panel.Content;
        panel.Content = null;
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        content.Arrange(new Rect(content.DesiredSize));
        content.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(content.ActualWidth * 2), (int)(content.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(outputPath);
        encoder.Save(stream);
        panel.Close();
    }

    private static IReadOnlyDictionary<string, string>? AskFolder(Operation op, IReadOnlyList<string> files)
    {
        var dialog = new OpenFolderDialog
        {
            Title = $"{op.Label} — {(files.Count == 1 ? Path.GetFileName(files[0].TrimEnd('\\')) : $"{files.Count} items")}",
            InitialDirectory = Path.GetDirectoryName(files[0].TrimEnd('\\')),
        };
        var host = Actions.ForegroundHost.Open();
        try
        {
            return dialog.ShowDialog(host) == true ? new Dictionary<string, string> { ["target"] = dialog.FolderName } : null;
        }
        finally { host.Close(); }
    }

    private void ShowNear(Point physicalCentre)
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Top = -32000;
        Show();
        UpdateLayout();
        var (work, scale) = Win32.MonitorAt(new Win32.POINT { X = (int)physicalCentre.X, Y = (int)physicalCentre.Y });
        int width = (int)(ActualWidth * scale), height = (int)(ActualHeight * scale);
        int x = Math.Clamp((int)physicalCentre.X - width / 2, work.Left, Math.Max(work.Left, work.Right - width));
        int y = Math.Clamp((int)physicalCentre.Y - height / 2, work.Top, Math.Max(work.Top, work.Bottom - height));
        nint hwnd = new WindowInteropHelper(this).Handle;
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, x, y, 0, 0, 0x0001 /* SWP_NOSIZE */);
        Win32.ForceForeground(hwnd);
        Activate();
        MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private void Finish(IReadOnlyDictionary<string, string>? answer)
    {
        _answer.TrySetResult(answer);
        Close();
    }

    // ---- Resize ---------------------------------------------------------------------------------

    private void BuildResize(IReadOnlyList<string> files)
    {
        var probe = ImageEstimator.Probe(files[0]);
        double aspect = probe is { Height: > 0 } p ? (double)p.Width / p.Height : 1;
        var width = Field(probe?.Width.ToString(CultureInfo.InvariantCulture) ?? "");
        var height = Field(probe?.Height.ToString(CultureInfo.InvariantCulture) ?? "");
        var keep = Chip("Keep shape");
        keep.IsChecked = true;
        string? preset = null;
        bool syncing = false;

        _body.Children.Add(Note(probe is null ? "Pick a size." : $"Now {probe.Width} × {probe.Height}{(files.Count > 1 ? " (first file)" : "")}"));
        var chips = ChipRow(["50%", "1920", "1280", "800"], chosen =>
        {
            preset = chosen;
            syncing = true;
            if (probe != null)
            {
                (int w, int h) = chosen == "50%" ? (probe.Width / 2, probe.Height / 2)
                    : Fit(probe.Width, probe.Height, int.Parse(chosen, CultureInfo.InvariantCulture));
                width.Text = w.ToString(CultureInfo.InvariantCulture);
                height.Text = h.ToString(CultureInfo.InvariantCulture);
            }
            syncing = false;
        });
        _body.Children.Add(chips);

        width.TextChanged += (_, _) =>
        {
            if (syncing) return;
            preset = null;
            UncheckAll(chips);
            if (keep.IsChecked == true && int.TryParse(width.Text, out int w)) { syncing = true; height.Text = Math.Max(1, (int)Math.Round(w / aspect)).ToString(CultureInfo.InvariantCulture); syncing = false; }
        };
        height.TextChanged += (_, _) =>
        {
            if (syncing) return;
            preset = null;
            UncheckAll(chips);
            if (keep.IsChecked == true && int.TryParse(height.Text, out int h)) { syncing = true; width.Text = Math.Max(1, (int)Math.Round(h * aspect)).ToString(CultureInfo.InvariantCulture); syncing = false; }
        };

        _body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { width, Inline("  ×  "), height, new Border { Width = 12 }, keep },
        });

        _collect = () =>
        {
            // Presets scale every image in a batch by its own shape; typed sizes apply as typed.
            if (preset == "50%") return new Dictionary<string, string> { ["scale"] = "0.5" };
            if (preset != null) return new Dictionary<string, string> { ["longest"] = preset };
            int.TryParse(width.Text, out int w);
            int.TryParse(height.Text, out int h);
            if (w <= 0 && h <= 0) return null;
            return keep.IsChecked == true && files.Count > 1
                ? new Dictionary<string, string> { ["width"] = Math.Max(0, w).ToString(CultureInfo.InvariantCulture) }
                : new Dictionary<string, string> { ["width"] = Math.Max(0, w).ToString(CultureInfo.InvariantCulture), ["height"] = Math.Max(0, h).ToString(CultureInfo.InvariantCulture) };
        };
    }

    private static (int, int) Fit(int width, int height, int longest) => width >= height
        ? (longest, Math.Max(1, (int)Math.Round(height * (double)longest / width)))
        : (Math.Max(1, (int)Math.Round(width * (double)longest / height)), longest);

    // ---- Crop -----------------------------------------------------------------------------------

    private void BuildCrop()
    {
        string aspect = "1:1";
        _body.Children.Add(Note("Pick a shape. Swivel keeps the most interesting part."));
        var chips = ChipRow(["1:1", "4:5", "4:3", "3:2", "16:9", "9:16"], chosen => aspect = chosen);
        ((ToggleButton)chips.Children[0]).IsChecked = true;
        _body.Children.Add(chips);
        _collect = () => new Dictionary<string, string> { ["aspect"] = aspect };
    }

    // ---- Trim -----------------------------------------------------------------------------------

    private void BuildTrim(string file)
    {
        var start = Field("0:00.0");
        var end = Field("…");
        var from = new Slider { Minimum = 0, Maximum = 1, Width = 340, Margin = new Thickness(0, 10, 0, 0), IsEnabled = false, Style = (Style)FindResource("SwivelSlider") };
        var to = new Slider { Minimum = 0, Maximum = 1, Value = 1, Width = 340, Margin = new Thickness(0, 6, 0, 0), IsEnabled = false, Style = (Style)FindResource("SwivelSlider") };
        var exact = Chip("Exact cut (slower)");
        exact.HorizontalAlignment = HorizontalAlignment.Left;
        double duration = 0;
        bool syncing = false;

        _body.Children.Add(Note("Keep the part between Start and End."));
        _body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { Inline("Start  "), start, new Border { Width = 16 }, Inline("End  "), end } });
        _body.Children.Add(from);
        _body.Children.Add(to);
        _body.Children.Add(new Border { Height = 10 });
        _body.Children.Add(exact);

        from.ValueChanged += (_, _) => { if (!syncing) { syncing = true; if (from.Value > to.Value) to.Value = from.Value; start.Text = Clock(from.Value); end.Text = Clock(to.Value); syncing = false; } };
        to.ValueChanged += (_, _) => { if (!syncing) { syncing = true; if (to.Value < from.Value) from.Value = to.Value; start.Text = Clock(from.Value); end.Text = Clock(to.Value); syncing = false; } };
        start.LostFocus += (_, _) => { if (ParseClock(start.Text) is double s) from.Value = Math.Clamp(s, 0, duration); };
        end.LostFocus += (_, _) => { if (ParseClock(end.Text) is double e) to.Value = Math.Clamp(e, 0, duration); };

        _ = Task.Run(async () =>
        {
            try
            {
                var info = await MediaInfo.ProbeAsync(file, CancellationToken.None);
                await Dispatcher.InvokeAsync(() =>
                {
                    duration = info.Duration;
                    from.Maximum = to.Maximum = duration;
                    to.Value = duration;
                    from.IsEnabled = to.IsEnabled = true;
                    end.Text = Clock(duration);
                });
            }
            catch (Exception)
            {
                await Dispatcher.InvokeAsync(() => end.Text = "?");
            }
        });

        _collect = () =>
        {
            double s = ParseClock(start.Text) ?? from.Value, e = ParseClock(end.Text) ?? to.Value;
            if (e - s < 0.1) return null;
            return new Dictionary<string, string>
            {
                ["start"] = s.ToString("0.###", CultureInfo.InvariantCulture),
                ["end"] = e.ToString("0.###", CultureInfo.InvariantCulture),
                ["exact"] = exact.IsChecked == true ? "true" : "false",
            };
        };
    }

    private static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.f", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss\.f", CultureInfo.InvariantCulture);
    }

    /// <summary>Reads "75", "1:15", "1:15.5" or "0:01:15".</summary>
    public static double? ParseClock(string text)
    {
        double total = 0;
        foreach (string part in text.Trim().Split(':'))
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || value < 0) return null;
            total = total * 60 + value;
        }
        return total;
    }

    // ---- Bits -----------------------------------------------------------------------------------

    private TextBox Field(string text) => new() { Text = text, Width = 96, Style = (Style)FindResource("SwivelField") };

    private ToggleButton Chip(string label) => new() { Content = label, Style = (Style)FindResource("SwivelChip") };

    private static TextBlock Note(string text, double size = 13) => new()
    {
        Text = text,
        FontFamily = Palette.Body,
        FontSize = size,
        Foreground = Palette.Ink,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 0, 10),
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 340,
    };

    /// <summary>A label that sits level with the fields beside it.</summary>
    private static TextBlock Inline(string text) => new()
    {
        Text = text,
        FontFamily = Palette.Body,
        FontWeight = FontWeights.Bold,
        FontSize = 13,
        Foreground = Palette.Ink,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private WrapPanel ChipRow(IEnumerable<string> labels, Action<string> chosen)
    {
        var row = new WrapPanel { MaxWidth = 360 };
        foreach (string label in labels)
        {
            var chip = Chip(label);
            chip.Checked += (_, _) =>
            {
                foreach (var other in row.Children.OfType<ToggleButton>()) if (other != chip) other.IsChecked = false;
                chosen(label);
            };
            row.Children.Add(chip);
        }
        return row;
    }

    private static void UncheckAll(Panel row)
    {
        foreach (var chip in row.Children.OfType<ToggleButton>()) chip.IsChecked = false;
    }
}
