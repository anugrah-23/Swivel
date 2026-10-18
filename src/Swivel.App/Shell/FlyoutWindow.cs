using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Serilog;
using Swivel.App.Services;
using Swivel.App.Theme;
using Swivel.Core.History;
using Swivel.Native;

namespace Swivel.App.Shell;

/// <summary>
/// The tray flyout: a header with the on/off switch, recent jobs with Undo, pinned folders, then
/// Pause, Settings and Quit. It closes when it loses focus, like the system's own tray flyouts.
/// </summary>
public sealed class FlyoutWindow : Window
{
    private const int RecentCount = 5;
    private const double FlyoutWidth = 340;

    private readonly SwivelServices _services;
    private readonly ShellState _state;
    private readonly Action _openSettings;
    private readonly Action _quit;
    private readonly StackPanel _content = new();
    private DateTime _hiddenAt;

    public FlyoutWindow(SwivelServices services, ShellState state, Action openSettings, Action quit)
    {
        _services = services;
        _state = state;
        _openSettings = openSettings;
        _quit = quit;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Width = FlyoutWidth;
        SizeToContent = SizeToContent.Height;
        Title = "Swivel";

        var card = new Border
        {
            Background = Palette.Cream,
            BorderBrush = Palette.Ink,
            BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(16),
            Child = _content,
        };
        var root = new Grid { Margin = new Thickness(0, 0, 5, 5) };
        root.Children.Add(new Border { Background = Palette.Ink, CornerRadius = new CornerRadius(16), Margin = new Thickness(5, 5, -5, -5) });
        root.Children.Add(card);
        Content = root;

        Deactivated += (_, _) => { _hiddenAt = DateTime.UtcNow; Hide(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
        state.Changed += () => { if (IsVisible) Build(); };
        services.HistoryChanged += () => { if (IsVisible) Build(); };
    }

    /// <summary>Shows above the tray, near the pointer, or hides if already open.</summary>
    public void Toggle()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }
        // Clicking the tray icon while open deactivates (hides) the flyout first; don't reopen it.
        if (DateTime.UtcNow - _hiddenAt < TimeSpan.FromMilliseconds(300)) return;
        Build();
        Left = Top = -32000;
        Show();
        UpdateLayout();

        Win32.GetCursorPos(out var cursor);
        var (work, scale) = Win32.MonitorAt(cursor);
        int width = (int)(ActualWidth * scale), height = (int)(ActualHeight * scale);
        int x = Math.Clamp(cursor.X - width / 2, work.Left + 8, work.Right - width - 8);
        int y = work.Bottom - height - 8;
        nint hwnd = new WindowInteropHelper(this).Handle;
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, x, y, 0, 0, 0x0001 /* SWP_NOSIZE */);
        Win32.ForceForeground(hwnd);
        Activate();
    }

    internal void Build()
    {
        _content.Children.Clear();
        _content.Children.Add(Header());
        _content.Children.Add(Section("RECENT", Recent()));
        _content.Children.Add(Section("PINNED FOLDERS", Pinned()));
        _content.Children.Add(Footer());
    }

    private Border Header()
    {
        var toggle = new ToggleButton
        {
            Style = (Style)FindResource("SwivelSwitch"),
            IsChecked = _state.Settings.Enabled,
            VerticalAlignment = VerticalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetName(toggle, "Swivel on");
        toggle.Click += (_, _) => _state.SetEnabled(toggle.IsChecked == true);

        var title = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new Image { Source = Logo.CreateImage(), Width = 28, Height = 28, Margin = new Thickness(0, 0, 10, 0) },
                new TextBlock { Text = "SWIVEL", FontFamily = Palette.Display, FontWeight = FontWeights.Black, FontSize = 22, Foreground = Palette.Ink, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        var top = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(toggle, Dock.Right);
        top.Children.Add(toggle);
        top.Children.Add(title);

        return new Border
        {
            Background = _state.IsListening ? Palette.Sunflower : Palette.Paper,
            BorderBrush = Palette.Ink,
            BorderThickness = new Thickness(0, 0, 0, 3),
            CornerRadius = new CornerRadius(13, 13, 0, 0),
            Padding = new Thickness(16, 12, 16, 12),
            Child = new StackPanel
            {
                Children =
                {
                    top,
                    new TextBlock { Text = _state.Status, FontFamily = Palette.Body, FontSize = 12, Foreground = Palette.Ink, Margin = new Thickness(0, 6, 0, 0) },
                },
            },
        };
    }

    private IEnumerable<UIElement> Recent()
    {
        IReadOnlyList<HistoryEntry> entries = [];
        try { entries = _services.History.Recent(RecentCount); }
        catch (Exception ex) { Log.Warning(ex, "Couldn't read history"); }
        if (entries.Count == 0)
        {
            yield return Muted("Nothing yet. Your jobs show up here.");
            yield break;
        }
        foreach (var entry in entries)
        {
            string what = entry.Inputs.Count == 1 ? Path.GetFileName(entry.Inputs[0].TrimEnd('\\')) : $"{entry.Inputs.Count} files";
            bool canUndo = !entry.Undone && _services.History.CanUndo(entry.GroupId);
            UIElement action;
            if (canUndo)
            {
                var undo = SmallButton("Undo");
                undo.Click += (_, _) => _services.Undo(entry.GroupId);
                action = undo;
            }
            else
            {
                action = Muted(entry.Undone ? "undone" : Ago(entry.Finished));
            }
            yield return Row($"{entry.Label} · {what}", action, () =>
            {
                if (entry.Outputs.FirstOrDefault(o => File.Exists(o.Path) || Directory.Exists(o.Path)) is { } output)
                    ShellFiles.Reveal([output.Path]);
            });
        }
    }

    private IEnumerable<UIElement> Pinned()
    {
        var folders = _state.Settings.PinnedFolders;
        foreach (string folder in folders)
        {
            var unpin = SmallButton("✕");
            System.Windows.Automation.AutomationProperties.SetName(unpin, $"Unpin {folder}");
            unpin.Click += (_, _) => _state.Update(s => s with { PinnedFolders = s.PinnedFolders.Where(f => f != folder).ToArray() });
            string name = Path.GetFileName(folder.TrimEnd('\\')) is { Length: > 0 } n ? n : folder;
            yield return Row(name, unpin, () => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true }));
        }
        var add = new Button { Content = "+ Pin a folder", Style = (Style)FindResource("SwivelButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        add.Click += (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = "Pin a folder for Move to and Copy to" };
            Hide();
            if (dialog.ShowDialog() == true && !_state.Settings.PinnedFolders.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase))
                _state.Update(s => s with { PinnedFolders = [.. s.PinnedFolders, dialog.FolderName] });
        };
        yield return add;
    }

    private UIElement Footer()
    {
        var pause = new Button
        {
            Content = _state.PausedUntil is null ? "Pause 1 hour" : "Resume",
            Style = (Style)FindResource("SwivelButton"),
            IsEnabled = _state.Settings.Enabled,
        };
        pause.Click += (_, _) =>
        {
            if (_state.PausedUntil is null) _state.Pause(TimeSpan.FromHours(1));
            else _state.Resume();
        };
        var settings = new Button { Content = "Settings", Style = (Style)FindResource("SwivelButton") };
        settings.Click += (_, _) => { Hide(); _openSettings(); };
        var quit = new Button { Content = "Quit", Style = (Style)FindResource("SwivelButton"), Margin = new Thickness(0) };
        quit.Click += (_, _) => { Hide(); _quit(); };

        return new Border
        {
            BorderBrush = Palette.Ink,
            BorderThickness = new Thickness(0, 3, 0, 0),
            Padding = new Thickness(16, 12, 16, 14),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { pause, settings, quit } },
        };
    }

    private static UIElement Section(string title, IEnumerable<UIElement> rows)
    {
        var panel = new StackPanel { Margin = new Thickness(16, 12, 16, 6) };
        panel.Children.Add(new TextBlock { Text = title, FontFamily = Palette.Mono, FontWeight = FontWeights.Bold, FontSize = 11, Foreground = Palette.Ink, Margin = new Thickness(0, 0, 0, 6) });
        foreach (var row in rows) panel.Children.Add(row);
        return panel;
    }

    private static UIElement Row(string text, UIElement trailing, Action open)
    {
        var label = new TextBlock
        {
            Text = text,
            FontFamily = Palette.Body,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = Palette.Ink,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
        };
        label.MouseLeftButtonUp += (_, _) => open();
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 2, 0, 6) };
        DockPanel.SetDock(trailing, Dock.Right);
        row.Children.Add(trailing);
        row.Children.Add(label);
        return row;
    }

    private Button SmallButton(string text) =>
        new() { Content = text, Style = (Style)FindResource("SwivelButton"), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(8, 0, 0, 0), FontSize = 11 };

    private static TextBlock Muted(string text) => new()
    {
        Text = text,
        FontFamily = Palette.Mono,
        FontSize = 11,
        Foreground = Palette.Ink,
        Opacity = 0.6,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(8, 0, 0, 0),
    };

    private static string Ago(DateTimeOffset when)
    {
        var span = DateTimeOffset.Now - when;
        return span.TotalMinutes < 1 ? "just now"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min ago"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours} h ago"
            : when.ToString("d MMM");
    }
}
