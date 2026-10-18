using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Swivel.App.Services;
using Swivel.App.Theme;
using Swivel.Core.Catalog;
using Swivel.Core.Settings;
using Swivel.Engines.Documents;
using Swivel.Engines.Infrastructure;

namespace Swivel.App.Shell;

/// <summary>
/// Settings: General, Shortcuts, Wheels, Output, Formats and engines, About. Every change saves at
/// once and applies live; there is no Apply button to forget.
/// </summary>
public sealed class SettingsWindow : Window
{
    private static readonly string[] Sections = ["General", "Shortcuts", "Wheels", "Output", "Formats and engines", "About"];

    private readonly ShellState _state;
    private readonly HotkeyHost? _hotkeys;
    private readonly ContentControl _page = new();
    private readonly StackPanel _nav = new();
    private string _current = Sections[0];

    public SettingsWindow(ShellState state, HotkeyHost? hotkeys)
    {
        _state = state;
        _hotkeys = hotkeys;
        Title = "Swivel settings";
        Width = 800;
        Height = 580;
        MinWidth = 640;
        MinHeight = 460;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = Logo.CreateImage();

        var close = new Button { Content = "✕", Style = (Style)FindResource("SwivelButton"), Padding = new Thickness(9, 2, 9, 2), Margin = new Thickness(0) };
        System.Windows.Automation.AutomationProperties.SetName(close, "Close settings");
        close.Click += (_, _) => Close();
        var titleBar = new DockPanel { Background = Palette.Sunflower, LastChildFill = true };
        var closeHolder = new Border { Padding = new Thickness(0, 8, 12, 8), Child = close };
        DockPanel.SetDock(closeHolder, Dock.Right);
        titleBar.Children.Add(closeHolder);
        titleBar.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(16, 8, 0, 8),
            Children =
            {
                new Image { Source = Logo.CreateImage(), Width = 26, Height = 26, Margin = new Thickness(0, 0, 10, 0) },
                new TextBlock { Text = "SETTINGS", FontFamily = Palette.Display, FontWeight = FontWeights.Black, FontSize = 20, Foreground = Palette.Ink, VerticalAlignment = VerticalAlignment.Center },
            },
        });
        titleBar.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 1) DragMove(); };

        var nav = new Border
        {
            Width = 200,
            BorderBrush = Palette.Ink,
            BorderThickness = new Thickness(0, 0, 3, 0),
            Padding = new Thickness(14, 16, 14, 16),
            Child = _nav,
        };
        var body = new DockPanel();
        DockPanel.SetDock(nav, Dock.Left);
        body.Children.Add(nav);
        body.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(24, 18, 24, 24),
            Content = _page,
        });

        var layout = new DockPanel();
        DockPanel.SetDock(titleBar, Dock.Top);
        layout.Children.Add(new Border { BorderBrush = Palette.Ink, BorderThickness = new Thickness(0, 0, 0, 3), Child = titleBar, CornerRadius = new CornerRadius(13, 13, 0, 0), ClipToBounds = true });
        DockPanel.SetDock(layout.Children[0], Dock.Top);
        layout.Children.Add(body);

        var card = new Border
        {
            Background = Palette.Cream,
            BorderBrush = Palette.Ink,
            BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(16),
            Child = layout,
        };
        var root = new Grid { Margin = new Thickness(0, 0, 8, 8) };
        root.Children.Add(new Border { Background = Palette.Ink, CornerRadius = new CornerRadius(16), Margin = new Thickness(8, 8, -8, -8) });
        root.Children.Add(card);
        Content = root;

        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && Keyboard.FocusedElement is not HotkeyField) Close(); };
        state.Changed += Refresh;
        Closed += (_, _) => state.Changed -= Refresh;
        Show(Sections[0]);
    }

    public static string Version =>
        typeof(SettingsWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev";

    private void Refresh() => Show(_current);

    internal void Show(string section)
    {
        _current = section;
        _nav.Children.Clear();
        foreach (string name in Sections)
        {
            var item = new ToggleButton
            {
                Content = name,
                Style = (Style)FindResource("SwivelChip"),
                IsChecked = name == section,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 10),
            };
            item.Click += (_, _) => Show(name);
            _nav.Children.Add(item);
        }

        var page = new StackPanel();
        page.Children.Add(Heading(section.ToUpperInvariant()));
        switch (section)
        {
            case "General": General(page); break;
            case "Shortcuts": Shortcuts(page); break;
            case "Wheels": Wheels(page); break;
            case "Output": Output(page); break;
            case "Formats and engines": Engines(page); break;
            case "About": About(page); break;
        }
        _page.Content = page;
    }

    // ---- Sections -------------------------------------------------------------------------------

    private void General(StackPanel page)
    {
        var s = _state.Settings;
        page.Children.Add(Switch("Swivel is on", s.Enabled, on => _state.SetEnabled(on), "Off, the trigger and shortcuts are left alone."));
        page.Children.Add(Switch("Launch at sign-in", s.LaunchAtSignIn, on => _state.Update(x => x with { LaunchAtSignIn = on }), "Starts quietly in the tray."));
        page.Children.Add(Switch("Explorer menu", s.ExplorerMenu, on => _state.Update(x => x with { ExplorerMenu = on }), "Adds \"Spin with Swivel\" when you right-click files."));
        page.Children.Add(Switch("Animations", s.Animations, on => _state.Update(x => x with { Animations = on }), "Follows Windows' own animation setting as well."));
    }

    private void Shortcuts(StackPanel page)
    {
        var s = _state.Settings;
        page.Children.Add(Label("Trigger key"));
        page.Children.Add(Note("Tap it while dragging a file to open the wheel. Tap again to switch modes."));
        page.Children.Add(Chips(["Alt", "Ctrl", "Shift", "CapsLock"], s.Trigger, value => _state.Update(x => x with { Trigger = value })));

        page.Children.Add(Label("Wheel for selected files"));
        page.Children.Add(Note("Select files in Explorer or on the desktop, then press this."));
        page.Children.Add(new HotkeyField(s.SelectionHotkey, _hotkeys, value => _state.Update(x => x with { SelectionHotkey = value })));

        page.Children.Add(Label("Wheel for the clipboard"));
        page.Children.Add(Note("Copied files, pictures or text."));
        page.Children.Add(new HotkeyField(s.ClipboardHotkey, _hotkeys, value => _state.Update(x => x with { ClipboardHotkey = value })));
    }

    private void Wheels(StackPanel page)
    {
        var s = _state.Settings;
        page.Children.Add(Label("Wedge order"));
        page.Children.Add(Chips(["Most used first", "Fixed order"], s.Ordering == WedgeOrdering.Usage ? "Most used first" : "Fixed order",
            value => _state.Update(x => x with { Ordering = value == "Fixed order" ? WedgeOrdering.Fixed : WedgeOrdering.Usage })));

        page.Children.Add(Label("Show on the wheel"));
        page.Children.Add(Note("Untick wedges you never use. They come back any time."));
        var hidden = s.HiddenOperations.ToHashSet();
        foreach (var group in OperationCatalog.BuiltIn.GroupBy(o => o.Mode))
        {
            page.Children.Add(new TextBlock { Text = group.Key.ToString().ToUpperInvariant(), FontFamily = Palette.Mono, FontWeight = FontWeights.Bold, FontSize = 11, Foreground = Palette.Ink, Margin = new Thickness(0, 10, 0, 6) });
            var wrap = new WrapPanel();
            foreach (var op in group)
            {
                var chip = new ToggleButton
                {
                    Content = $"{op.Label} · {op.Hint}",
                    Style = (Style)FindResource("SwivelChip"),
                    IsChecked = !hidden.Contains(op.Id),
                    ToolTip = op.Id,
                };
                chip.Click += (_, _) => _state.Update(x => x with
                {
                    HiddenOperations = chip.IsChecked == true
                        ? x.HiddenOperations.Where(id => id != op.Id).ToArray()
                        : [.. x.HiddenOperations, op.Id],
                });
                wrap.Children.Add(chip);
            }
            page.Children.Add(wrap);
        }

        page.Children.Add(Label("Presets"));
        page.Children.Add(Note(s.Presets.Count == 0
            ? "None yet. A preset is a wedge with your own settings, such as WebP at 1200 px. Add them in the settings file."
            : string.Join("\n", s.Presets.Select(p => $"{p.Label} · {p.Hint}  ({p.Base})"))));
        page.Children.Add(LinkButton("Edit settings file", OpenSettingsFile));
    }

    private void Output(StackPanel page)
    {
        var rule = _state.Settings.Output;
        page.Children.Add(Label("Where new files go"));
        page.Children.Add(Chips(["Next to the original", "One folder"], rule.Location == OutputLocation.Folder ? "One folder" : "Next to the original", value =>
        {
            if (value == "One folder" && string.IsNullOrEmpty(rule.Folder)) PickOutputFolder();
            else _state.Update(x => x with { Output = x.Output with { Location = value == "One folder" ? OutputLocation.Folder : OutputLocation.NextToOriginal } });
        }));
        page.Children.Add(Note(rule.Location == OutputLocation.Folder && !string.IsNullOrEmpty(rule.Folder)
            ? $"Everything lands in {rule.Folder}."
            : "Name clashes become name (1).ext. Originals are never touched."));
        if (rule.Location == OutputLocation.Folder) page.Children.Add(LinkButton("Choose folder…", PickOutputFolder));
    }

    private void PickOutputFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Where should Swivel put new files?" };
        if (dialog.ShowDialog(this) == true)
            _state.Update(x => x with { Output = new OutputRule { Location = OutputLocation.Folder, Folder = dialog.FolderName } });
    }

    private void Engines(StackPanel page)
    {
        page.Children.Add(EngineRow("Images", "libvips, built in. HEIC photos use Windows' HEIF and HEVC extensions.", null, "Get HEVC extension",
            () => Feedback.Toast.Launch(Engines_.HevcStore)));
        page.Children.Add(EngineRow("Audio and video", EngineLocator.Find(Path.Combine("ffmpeg", "ffmpeg.exe")) != null ? "FFmpeg (LGPL), built in." : "FFmpeg is missing. Reinstall Swivel.", null, null, null));
        page.Children.Add(EngineRow("Archives", "ZIP, TAR and GZIP built in; 7z through 7-Zip's 7zr.", null, null, null));
        page.Children.Add(PackRow(EnginePacks.Pandoc, "Documents", "DOCX, Markdown, HTML, EPUB and text through Pandoc (GPL, kept separate)."));

        bool office = Type.GetTypeFromProgID("Word.Application") != null;
        string? libre = OfficeEngine.LibreOffice();
        page.Children.Add(office
            ? EngineRow("Office to PDF", "Microsoft Office found. Swivel prints through it.", "Ready", null, null)
            : PackRow(EnginePacks.LibreOffice, "Office to PDF", libre != null ? $"LibreOffice found at {libre}." : "No Microsoft Office here; LibreOffice can stand in."));
    }

    private void About(StackPanel page)
    {
        page.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 12),
            Children =
            {
                new Image { Source = Logo.CreateImage(), Width = 64, Height = 64, Margin = new Thickness(0, 0, 16, 0) },
                new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = "SWIVEL", FontFamily = Palette.Display, FontWeight = FontWeights.Black, FontSize = 28, Foreground = Palette.Ink },
                        new TextBlock { Text = $"Version {Version} · MIT licence · free and open source", FontFamily = Palette.Body, FontSize = 13, Foreground = Palette.Ink },
                    },
                },
            },
        });
        page.Children.Add(Note("Everything runs on this PC. No account, no uploads, no telemetry."));
        page.Children.Add(Label("Built with"));
        page.Children.Add(Note(string.Join("\n",
            "libvips (LGPL) via NetVips (MIT) · FFmpeg (LGPL) · Pandoc (GPL, separate executable)",
            "PDFsharp (MIT) · PDFium (Apache 2.0/BSD) · SharpCompress (MIT) · 7-Zip 7zr (LGPL)",
            "LibreOffice (MPL, optional) · SQLite (public domain) · Serilog (Apache 2.0) · H.NotifyIcon (MIT)",
            "Archivo, Space Grotesk and Space Mono fonts (SIL Open Font License)")));
        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(LinkButton("Licences", () => OpenFolder(Path.Combine(AppContext.BaseDirectory, "licenses"))));
        buttons.Children.Add(LinkButton("Logs", () => OpenFolder(AppPaths.Logs)));
        buttons.Children.Add(LinkButton("Settings file", OpenSettingsFile));
        page.Children.Add(buttons);
    }

    // ---- Building blocks ------------------------------------------------------------------------

    private static class Engines_
    {
        public const string HevcStore = "ms-windows-store://pdp/?ProductId=9NMZLZ57R3T7";
    }

    private UIElement PackRow(EnginePack pack, string title, string description)
    {
        string? location = EnginePacks.Locate(pack);
        bool removable = location != null && location.StartsWith(EngineLocator.PacksRoot, StringComparison.OrdinalIgnoreCase);
        if (location != null)
        {
            return EngineRow(title, description, "Installed", removable ? "Remove" : null, removable ? () =>
            {
                try { Directory.Delete(Path.Combine(EngineLocator.PacksRoot, pack.Id), recursive: true); }
                catch (IOException) { }
                Refresh();
            } : null);
        }

        var status = new TextBlock { FontFamily = Palette.Mono, FontSize = 11, Foreground = Palette.Ink, VerticalAlignment = VerticalAlignment.Center };
        UIElement row = null!;
        row = EngineRow(title, description, null, $"Get ({Format.Size(pack.DownloadBytes)})", async () =>
        {
            var progress = new Progress<double>(p => status.Text = p < 0.9 ? $"{p / 0.9:P0}" : "unpacking…");
            try
            {
                await EnginePacks.InstallAsync(pack, progress, CancellationToken.None);
                Refresh();
            }
            catch (Exception ex)
            {
                status.Text = ex is Core.Jobs.JobException ? ex.Message : "Download failed.";
            }
        }, status);
        return row;
    }

    private UIElement EngineRow(string title, string description, string? badge, string? action, Action? onAction, UIElement? extra = null)
    {
        var text = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = title, FontFamily = Palette.Display, FontWeight = FontWeights.ExtraBold, FontSize = 15, Foreground = Palette.Ink },
                new TextBlock { Text = description, FontFamily = Palette.Body, FontSize = 12, Foreground = Palette.Ink, TextWrapping = TextWrapping.Wrap, MaxWidth = 380, HorizontalAlignment = HorizontalAlignment.Left },
            },
        };
        var trailing = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (badge != null)
            trailing.Children.Add(new Border
            {
                Background = Palette.Mint, BorderBrush = Palette.Ink, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 8, 0),
                Child = new TextBlock { Text = badge, FontFamily = Palette.Display, FontWeight = FontWeights.ExtraBold, FontSize = 11, Foreground = Palette.Ink },
            });
        if (extra != null) trailing.Children.Add(extra);
        if (action != null && onAction != null)
        {
            var button = new Button { Content = action, Style = (Style)FindResource("SwivelButton"), Margin = new Thickness(8, 0, 0, 0) };
            button.Click += (_, _) => onAction();
            trailing.Children.Add(button);
        }
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(trailing, Dock.Right);
        row.Children.Add(trailing);
        row.Children.Add(text);
        return new Border
        {
            Background = Palette.Paper, BorderBrush = Palette.Ink, BorderThickness = new Thickness(2.5), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 10), Child = row,
        };
    }

    private UIElement Switch(string label, bool on, Action<bool> changed, string note)
    {
        var toggle = new ToggleButton { Style = (Style)FindResource("SwivelSwitch"), IsChecked = on, Content = label, Margin = new Thickness(0, 6, 0, 2) };
        toggle.Click += (_, _) => changed(toggle.IsChecked == true);
        return new StackPanel { Margin = new Thickness(0, 0, 0, 10), Children = { toggle, Note(note, 64) } };
    }

    private WrapPanel Chips(IEnumerable<string> options, string selected, Action<string> chosen)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (string option in options)
        {
            var chip = new ToggleButton
            {
                Content = option == "CapsLock" ? "Caps Lock" : option,
                Style = (Style)FindResource("SwivelChip"),
                IsChecked = string.Equals(option, selected, StringComparison.OrdinalIgnoreCase),
            };
            chip.Click += (_, _) =>
            {
                if (chip.IsChecked != true) { chip.IsChecked = true; return; } // always one choice
                chosen(option);
            };
            row.Children.Add(chip);
        }
        return row;
    }

    private Button LinkButton(string text, Action onClick)
    {
        var button = new Button { Content = text, Style = (Style)FindResource("SwivelButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 8, 8) };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = text, FontFamily = Palette.Display, FontWeight = FontWeights.Black, FontSize = 26, Foreground = Palette.Ink, Margin = new Thickness(0, 0, 0, 14),
    };

    private static TextBlock Label(string text) => new()
    {
        Text = text, FontFamily = Palette.Display, FontWeight = FontWeights.ExtraBold, FontSize = 15, Foreground = Palette.Ink, Margin = new Thickness(0, 10, 0, 2),
    };

    private static TextBlock Note(string text, double indent = 0) => new()
    {
        Text = text, FontFamily = Palette.Body, FontSize = 12.5, Foreground = Palette.Ink, Opacity = 0.85, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(indent, 2, 0, 8), MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left,
    };

    private static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private void OpenSettingsFile()
    {
        if (!File.Exists(AppPaths.Settings)) _state.Update(s => s);
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{AppPaths.Settings}\"") { UseShellExecute = true });
    }

    /// <summary>Press-to-record shortcut box with a live "free / taken" check.</summary>
    private sealed class HotkeyField : StackPanel
    {
        public HotkeyField(string current, HotkeyHost? hotkeys, Action<string> save)
        {
            Orientation = Orientation.Horizontal;
            Margin = new Thickness(0, 0, 0, 8);
            var box = new TextBox { Text = current, Width = 200, IsReadOnly = true, Style = (Style)Application.Current.FindResource("SwivelField"), Cursor = Cursors.Hand };
            System.Windows.Automation.AutomationProperties.SetName(box, "Press a new shortcut");
            var status = new TextBlock { FontFamily = Palette.Body, FontWeight = FontWeights.SemiBold, FontSize = 12, Foreground = Palette.Ink, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            void Check(string gesture)
            {
                bool taken = hotkeys?.IsTakenElsewhere(gesture) == true;
                status.Text = taken ? "Taken by another app. Try another." : "Works.";
                status.Foreground = taken ? Palette.Orange : Palette.Ink;
            }
            Check(current);
            box.GotKeyboardFocus += (_, _) => status.Text = "Press the new shortcut…";
            box.LostKeyboardFocus += (_, _) => Check(box.Text);
            box.PreviewKeyDown += (_, e) =>
            {
                e.Handled = true;
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (key == Key.Escape) { Keyboard.ClearFocus(); return; }
                if (HotkeyHost.Describe(Keyboard.Modifiers, key) is not { } gesture) return;
                box.Text = gesture;
                Check(gesture);
                if (status.Foreground != Palette.Orange) save(gesture);
            };
            Children.Add(box);
            Children.Add(status);
        }
    }
}
