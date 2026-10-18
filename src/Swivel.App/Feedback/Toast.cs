using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Serilog;
using Swivel.App.Theme;

namespace Swivel.App.Feedback;

public enum ToastKind { Done, Failed, Info }

/// <summary>A button on a toast. Returning false from the handler keeps the toast open.</summary>
public sealed record ToastAction(string Label, Func<Toast, bool> Run);

/// <summary>
/// A neo-brutalist toast: mint for done, orange for failed, paper for info. It never takes focus,
/// hides itself after a few seconds, and waits while the pointer is over it.
/// </summary>
public sealed class Toast : FloatingWindow
{
    public const double ToastWidth = 360;

    private readonly DispatcherTimer _hide;
    private readonly TextBlock _title;
    private readonly TextBlock _body;
    private readonly TextBlock _detail;
    private readonly WrapPanel _buttons = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly Border _header;

    public Toast(ToastKind kind, string title, string body, string? detail, IEnumerable<ToastAction> actions, TimeSpan life)
    {
        Width = ToastWidth;
        SizeToContent = SizeToContent.Height;
        Left = Top = -32000;
        AutomationProperties.SetName(this, $"{title} {body}");

        _title = new TextBlock
        {
            Text = title,
            FontFamily = Palette.Display,
            FontWeight = FontWeights.Black,
            FontSize = 17,
            Foreground = Palette.Ink,
            TextWrapping = TextWrapping.Wrap,
        };
        _header = new Border
        {
            Background = kind switch { ToastKind.Done => Palette.Mint, ToastKind.Failed => Palette.Orange, _ => Palette.Sunflower },
            BorderBrush = Palette.Ink,
            BorderThickness = new Thickness(0, 0, 0, 3),
            CornerRadius = new CornerRadius(9, 9, 0, 0),
            Padding = new Thickness(14, 8, 14, 8),
            Child = _title,
        };
        _body = new TextBlock
        {
            Text = body,
            FontFamily = Palette.Body,
            FontWeight = FontWeights.Medium,
            FontSize = 13,
            Foreground = Palette.Ink,
            TextWrapping = TextWrapping.Wrap,
        };
        _detail = new TextBlock
        {
            Text = detail ?? "",
            FontFamily = Palette.Mono,
            FontSize = 11,
            Foreground = Palette.Ink,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible,
        };
        SetActions(actions);

        var content = new StackPanel { Margin = new Thickness(14, 10, 14, 12), Children = { _body, _detail, _buttons } };
        var face = new Border
        {
            Background = Palette.Paper,
            BorderBrush = Palette.Ink,
            BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(12),
            Child = new StackPanel { Children = { _header, content } },
        };
        var root = new Grid { Margin = new Thickness(0, 0, 5, 5) };
        root.Children.Add(new Border { Background = Palette.Ink, CornerRadius = new CornerRadius(12), Margin = new Thickness(5, 5, -5, -5) });
        root.Children.Add(face);
        Content = root;

        _hide = new DispatcherTimer { Interval = life };
        _hide.Tick += (_, _) => { if (!IsMouseOver) Close(); };
        MouseLeave += (_, _) => { _hide.Stop(); _hide.Start(); };
        Loaded += (_, _) => _hide.Start();
    }

    /// <summary>Swaps the toast's message in place, for example after Undo.</summary>
    public void Update(ToastKind kind, string title, string body, IEnumerable<ToastAction>? actions = null, TimeSpan? life = null)
    {
        _header.Background = kind switch { ToastKind.Done => Palette.Mint, ToastKind.Failed => Palette.Orange, _ => Palette.Sunflower };
        _title.Text = title;
        _body.Text = body;
        _detail.Visibility = Visibility.Collapsed;
        SetActions(actions ?? []);
        if (life is { } l) _hide.Interval = l;
        _hide.Stop();
        _hide.Start();
    }

    public void ShowDetail(string text)
    {
        _detail.Text = text;
        _detail.Visibility = Visibility.Visible;
    }

    private void SetActions(IEnumerable<ToastAction> actions)
    {
        _buttons.Children.Clear();
        foreach (var action in actions)
        {
            var button = new Button { Content = action.Label, Style = (Style)FindResource("SwivelButton"), Margin = new Thickness(0, 0, 8, 4) };
            AutomationProperties.SetName(button, action.Label);
            button.Click += (_, _) =>
            {
                try
                {
                    if (action.Run(this)) Close();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Toast action {Action} failed", action.Label);
                }
            };
            _buttons.Children.Add(button);
        }
        _buttons.Visibility = _buttons.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public static void Launch(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
}

/// <summary>Stacks toasts in the bottom-right corner of the primary screen's work area.</summary>
public sealed class ToastHost
{
    private const double Margin = 16, Gap = 12;
    private const int MaxToasts = 4;
    private readonly List<Toast> _toasts = [];

    public Toast Show(Toast toast)
    {
        while (_toasts.Count >= MaxToasts) _toasts[0].Close();
        _toasts.Add(toast);
        toast.Closed += (_, _) => { _toasts.Remove(toast); Layout(); };
        toast.SizeChanged += (_, _) => Layout();
        toast.Show();
        Layout();
        return toast;
    }

    private void Layout()
    {
        var area = SystemParameters.WorkArea;
        double bottom = area.Bottom - Margin;
        for (int i = _toasts.Count - 1; i >= 0; i--)
        {
            var toast = _toasts[i];
            double height = toast.ActualHeight > 0 ? toast.ActualHeight : 120;
            toast.Left = area.Right - Margin - Toast.ToastWidth;
            toast.Top = bottom - height;
            bottom -= height + Gap;
        }
    }
}
