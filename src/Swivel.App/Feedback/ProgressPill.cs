using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Swivel.App.Theme;
using Swivel.Core.Jobs;

namespace Swivel.App.Feedback;

/// <summary>
/// The pill the wheel shrinks into: a pie, the target and a percentage; batches add a count, a bar
/// and Cancel. It only appears if the job outlasts a blink, so quick jobs go straight to the toast.
/// </summary>
public sealed class ProgressPill : FloatingWindow
{
    private const double PillWidth = 300, PillHeight = 64;
    private static readonly TimeSpan ShowAfter = TimeSpan.FromMilliseconds(150);

    private readonly JobGroup _group;
    private readonly DispatcherTimer _tick;
    private readonly System.Windows.Shapes.Path _pie = new() { Fill = Palette.Ink };
    private readonly TextBlock _percent;
    private readonly TextBlock _count;
    private readonly Border _barFill;
    private readonly DateTime _started = DateTime.UtcNow;

    public ProgressPill(JobGroup group, Point screenCentre, double scale, int stackIndex)
    {
        _group = group;
        Width = PillWidth;
        Height = PillHeight;
        Left = Top = -32000;
        bool batch = group.Inputs.Count > 1;

        var pieFace = new Grid { Width = 26, Height = 26, VerticalAlignment = VerticalAlignment.Center };
        pieFace.Children.Add(new Ellipse { Fill = Palette.Paper, Stroke = Palette.Ink, StrokeThickness = 2.5 });
        pieFace.Children.Add(_pie);

        var title = new TextBlock
        {
            Text = group.Operation.Mode == Core.Catalog.WheelMode.Convert ? $"TO {group.Operation.Label}" : group.Operation.Label.ToUpperInvariant(),
            FontFamily = Palette.Display,
            FontWeight = FontWeights.Black,
            FontSize = 15,
            Foreground = Palette.Ink,
        };
        _count = new TextBlock { FontFamily = Palette.Mono, FontWeight = FontWeights.Bold, FontSize = 11, Foreground = Palette.Ink };
        _barFill = new Border { Background = Palette.Ink, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        var bar = new Border
        {
            Height = 8,
            Width = 110,
            Margin = new Thickness(0, 4, 0, 0),
            CornerRadius = new CornerRadius(4),
            BorderBrush = Palette.Ink,
            BorderThickness = new Thickness(1.5),
            Background = Palette.Paper,
            Child = _barFill,
            Visibility = batch ? Visibility.Visible : Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var middle = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0), Children = { title, _count, bar } };

        _percent = new TextBlock
        {
            FontFamily = Palette.Mono,
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            Foreground = Palette.Ink,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("SwivelButton"), Margin = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        cancel.Click += (_, _) => { _group.Cancel(); cancel.IsEnabled = false; };

        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(pieFace, Dock.Left);
        DockPanel.SetDock(cancel, Dock.Right);
        DockPanel.SetDock(_percent, Dock.Right);
        row.Children.Add(pieFace);
        row.Children.Add(cancel);
        row.Children.Add(_percent);
        row.Children.Add(middle);

        var face = new Border
        {
            Background = Palette.Paper,
            BorderBrush = Palette.Ink,
            BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(26),
            Padding = new Thickness(12, 6, 10, 6),
            Child = row,
        };
        var root = new Grid { Margin = new Thickness(0, 0, 5, 5) };
        root.Children.Add(new Border { Background = Palette.Ink, CornerRadius = new CornerRadius(26), Margin = new Thickness(5, 5, -5, -5) });
        root.Children.Add(face);
        Content = root;

        var at = new Point(screenCentre.X, screenCentre.Y + stackIndex * (PillHeight + 10) * scale);
        SourceInitialized += (_, _) => CentreOn(at, PillWidth, PillHeight, scale);

        _tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(80) };
        _tick.Tick += (_, _) => Refresh();
        _tick.Start();
        group.Completion.ContinueWith(_ => Dispatcher.InvokeAsync(Finish), TaskScheduler.Default);
    }

    private void Refresh()
    {
        if (!IsVisible && DateTime.UtcNow - _started >= ShowAfter && !_group.Completion.IsCompleted) Show();
        if (!IsVisible) return;
        double progress = _group.Progress;
        _percent.Text = $"{progress * 100:F0}%";
        _count.Text = _group.Inputs.Count > 1 ? $"{_group.Completed} of {_group.Inputs.Count}" : "working…";
        _barFill.Width = 107 * progress;
        _pie.Data = Pie(progress);
    }

    private void Finish()
    {
        _tick.Stop();
        Close();
    }

    private static Geometry Pie(double fraction)
    {
        const double c = 13, r = 10.5;
        if (fraction >= 0.999) return new EllipseGeometry(new Point(c, c), r, r);
        if (fraction <= 0.001) return Geometry.Empty;
        double angle = fraction * 2 * Math.PI;
        var end = new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = new Point(c, c), IsClosed = true };
        figure.Segments.Add(new LineSegment(new Point(c, c - r), true));
        figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0, fraction > 0.5, SweepDirection.Clockwise, true));
        return new PathGeometry([figure]);
    }
}
