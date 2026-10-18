using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Swivel.App.Services;
using Swivel.App.Theme;
using Swivel.Core.Catalog;

namespace Swivel.App.Shell;

/// <summary>
/// First run in three steps: what Swivel does, pick the trigger and practise on a drop zone (the
/// real wheel opens, but nothing is converted), then the Explorer menu and launch at sign-in.
/// </summary>
public sealed class OnboardingWindow : Window
{
    private readonly ShellState _state;
    private readonly ContentControl _step = new();
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _back;
    private readonly Button _next;
    private int _index;
    private Border? _practice;
    private TextBlock? _practiceText;

    public OnboardingWindow(ShellState state)
    {
        _state = state;
        Title = "Welcome to Swivel";
        Width = 600;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        Icon = Logo.CreateImage();

        _back = new Button { Content = "Back", Style = (Style)FindResource("SwivelButton") };
        _next = new Button { Content = "Next", Style = (Style)FindResource("SwivelPrimaryButton"), IsDefault = true, Margin = new Thickness(0) };
        var skip = new Button { Content = "Skip", Style = (Style)FindResource("SwivelButton") };
        _back.Click += (_, _) => Go(_index - 1);
        _next.Click += (_, _) => { if (_index == 2) Finish(); else Go(_index + 1); };
        skip.Click += (_, _) => Finish();

        var footer = new DockPanel { Margin = new Thickness(24, 0, 24, 22) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { skip, _back, _next } };
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(_dots);

        var card = new Border
        {
            Background = Palette.Cream,
            BorderBrush = Palette.Ink,
            BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(16),
            Child = new StackPanel { Children = { new Border { Padding = new Thickness(28, 26, 28, 18), Child = _step }, footer } },
        };
        card.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is Border or StackPanel) DragMove(); };
        var root = new Grid { Margin = new Thickness(0, 0, 8, 8) };
        root.Children.Add(new Border { Background = Palette.Ink, CornerRadius = new CornerRadius(16), Margin = new Thickness(8, 8, -8, -8) });
        root.Children.Add(card);
        Content = root;

        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Finish(); };
        Closed += (_, _) => { if (!_state.Settings.OnboardingDone) _state.Update(s => s with { OnboardingDone = true }); };
        Go(0);
    }

    /// <summary>True while the practice step is showing: drops there are caught, not converted.</summary>
    public bool Practising => IsVisible && _index == 1;

    /// <summary>The wheel opened during practice.</summary>
    public void WheelOpened()
    {
        if (!Practising || _practice is null || _practiceText is null) return;
        _practice.Background = Palette.Sunflower;
        _practiceText.Text = "There it is. Tap the trigger again to see Tools and Actions, then let go on any wedge.";
    }

    /// <summary>A practice drop: nothing runs, the zone just cheers.</summary>
    public void Caught(Operation operation)
    {
        if (_practice is null || _practiceText is null) return;
        _practice.Background = Palette.Mint;
        string what = operation.Mode == WheelMode.Convert ? $"a {operation.Label}" : operation.Label.ToLowerInvariant();
        _practiceText.Text = $"Nailed it. Outside this window, that would have made {what}. Nothing was changed.";
        _next.Content = "Next";
    }

    internal void Go(int index)
    {
        _index = Math.Clamp(index, 0, 2);
        _back.Visibility = _index == 0 ? Visibility.Hidden : Visibility.Visible;
        _next.Content = _index == 2 ? "Start spinning" : "Next";
        _step.Content = _index switch { 0 => Meet(), 1 => Trigger(), _ => FitIn() };
        _dots.Children.Clear();
        for (int i = 0; i < 3; i++)
            _dots.Children.Add(new Border
            {
                Width = i == _index ? 26 : 12, Height = 12, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 6, 0),
                Background = i == _index ? Palette.Ink : Palette.Paper, BorderBrush = Palette.Ink, BorderThickness = new Thickness(2),
            });
    }

    private void Finish()
    {
        _state.Update(s => s with { OnboardingDone = true });
        Close();
    }

    private UIElement Meet() => new StackPanel
    {
        Children =
        {
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new Image { Source = Logo.CreateImage(), Width = 64, Height = 64, Margin = new Thickness(0, 0, 16, 0) },
                    Big("MEET SWIVEL"),
                },
            },
            Body("Convert, edit and move files without opening anything. It lives in your tray and wakes up mid-drag."),
            Step("1", "Start dragging any file."),
            Step("2", $"Tap {TriggerKeys.Label(_state.Settings.Trigger)} while you're still holding it."),
            Step("3", "Let go on a wedge. Done, and undoable."),
            Body("Everything stays on this PC. Your originals are never touched."),
        },
    };

    private UIElement Trigger()
    {
        var chips = new WrapPanel { Margin = new Thickness(0, 6, 0, 10) };
        foreach (string key in new[] { "Alt", "Ctrl", "Shift", "CapsLock" })
        {
            var chip = new ToggleButton
            {
                Content = key == "CapsLock" ? "Caps Lock" : key,
                Style = (Style)FindResource("SwivelChip"),
                IsChecked = string.Equals(key, _state.Settings.Trigger, StringComparison.OrdinalIgnoreCase),
            };
            chip.Click += (_, _) => { _state.Update(s => s with { Trigger = key }); Go(1); };
            chips.Children.Add(chip);
        }

        _practiceText = new TextBlock
        {
            Text = $"Practise here: drag any file over this box and tap {TriggerKeys.Label(_state.Settings.Trigger)}.",
            FontFamily = Palette.Display, FontWeight = FontWeights.ExtraBold, FontSize = 15, Foreground = Palette.Ink,
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18),
        };
        _practice = new Border
        {
            Height = 150, Background = Palette.Paper, BorderBrush = Palette.Ink, BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(14), Child = _practiceText, AllowDrop = true,
        };
        _practice.DragEnter += (_, e) =>
        {
            if (_practice.Background == Palette.Paper)
                _practiceText.Text = $"Now tap {TriggerKeys.Label(_state.Settings.Trigger)}, keep holding the file.";
            e.Effects = DragDropEffects.None;
            e.Handled = true;
        };
        return new StackPanel
        {
            Children =
            {
                Big("PICK YOUR TRIGGER"),
                Body("The key you tap mid-drag. Alt is the default; choose another if an app you use needs it."),
                chips,
                _practice,
            },
        };
    }

    private UIElement FitIn()
    {
        var s = _state.Settings;
        var menu = new ToggleButton { Style = (Style)FindResource("SwivelSwitch"), IsChecked = s.ExplorerMenu, Content = "Add \"Spin with Swivel\" to the right-click menu", Margin = new Thickness(0, 4, 0, 12) };
        menu.Click += (_, _) => _state.Update(x => x with { ExplorerMenu = menu.IsChecked == true });
        var signIn = new ToggleButton { Style = (Style)FindResource("SwivelSwitch"), IsChecked = s.LaunchAtSignIn, Content = "Start Swivel when I sign in", Margin = new Thickness(0, 0, 0, 12) };
        signIn.Click += (_, _) => _state.Update(x => x with { LaunchAtSignIn = signIn.IsChecked == true });
        return new StackPanel
        {
            Children =
            {
                Big("FIT IT IN"),
                Body("No drag needed for these:"),
                Step("⌨", $"{s.SelectionHotkey} opens the wheel for files selected in Explorer."),
                Step("⌨", $"{s.ClipboardHotkey} opens it for whatever you copied."),
                Step("🖱", "Right-click files and pick Spin with Swivel."),
                new Border { Height = 8 },
                menu,
                signIn,
                Body("Everything else lives in the tray icon. Click it any time."),
            },
        };
    }

    private static TextBlock Big(string text) => new()
    {
        Text = text, FontFamily = Palette.Display, FontWeight = FontWeights.Black, FontSize = 30, Foreground = Palette.Ink,
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 10),
    };

    private static TextBlock Body(string text) => new()
    {
        Text = text, FontFamily = Palette.Body, FontSize = 14, Foreground = Palette.Ink, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12),
    };

    private static UIElement Step(string number, string text) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Margin = new Thickness(0, 0, 0, 10),
        Children =
        {
            new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Background = Palette.Ink, Margin = new Thickness(0, 0, 12, 0),
                Child = new TextBlock { Text = number, FontFamily = Palette.Mono, FontWeight = FontWeights.Bold, FontSize = 14, Foreground = Palette.Sunflower, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            },
            new TextBlock { Text = text, FontFamily = Palette.Body, FontWeight = FontWeights.SemiBold, FontSize = 14, Foreground = Palette.Ink, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 470 },
        },
    };
}
