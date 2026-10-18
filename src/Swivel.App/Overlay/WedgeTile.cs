using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Swivel.App.Theme;

namespace Swivel.App.Overlay;

/// <summary>One 76 × 60 wheel tile: flat colour when idle; ink, tilted and enlarged when active.</summary>
internal sealed class WedgeTile : Grid
{
    public const double TileWidth = 76, TileHeight = 60;

    private readonly Border _shadow;
    private readonly Border _face;
    private readonly TextBlock _label;
    private readonly TextBlock _hint;
    private readonly ScaleTransform _pop = new(1, 1);
    private readonly ScaleTransform _grow = new(1, 1);
    private readonly RotateTransform _tilt = new(0);
    private Brush _idleFill = Brushes.White;
    private Brush _activeShadow = Palette.Pink;
    private bool _active;

    /// <summary>The active tile casts a pink shadow, or sunflower when the disc itself is pink.</summary>
    public void SetDisc(Brush disc)
    {
        _activeShadow = disc == Palette.Pink ? Palette.Sunflower : Palette.Pink;
        ApplyFill();
    }

    public WedgeTile()
    {
        Width = TileWidth;
        Height = TileHeight;
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = new TransformGroup { Children = { _pop, _grow, _tilt } };

        _shadow = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = Palette.Ink,
            RenderTransform = new TranslateTransform(5, 5),
        };
        _label = new TextBlock
        {
            FontFamily = Palette.Display,
            FontWeight = FontWeights.Black,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = TileWidth - 8,
        };
        _hint = new TextBlock
        {
            FontFamily = Palette.Body,
            FontWeight = FontWeights.SemiBold,
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _face = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderBrush = Palette.Ink,
            BorderThickness = new Thickness(2.5),
            Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children = { _label, _hint },
            },
        };
        Children.Add(_shadow);
        Children.Add(_face);
    }

    /// <summary>The blank tile shown on the first frame, before the drag's contents are known.</summary>
    public void SetNeutral()
    {
        Visibility = Visibility.Visible;
        _label.Text = "";
        _hint.Text = "";
        _idleFill = Palette.Paper;
        AutomationProperties.SetName(this, "");
        ApplyFill();
    }

    public void SetContent(string label, string hint, Brush idleFill, string automationName)
    {
        Visibility = Visibility.Visible;
        _label.Text = label;
        _label.FontSize = label.Length > 6 ? 12 : 16;
        _hint.Text = hint;
        _idleFill = idleFill;
        AutomationProperties.SetName(this, automationName);
        ApplyFill();
    }

    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        ApplyFill();
        Panel.SetZIndex(this, active ? 1 : 0);
        double scale = active ? 1.12 : 1;
        Palette.To(_grow, ScaleTransform.ScaleXProperty, scale, 80);
        Palette.To(_grow, ScaleTransform.ScaleYProperty, scale, 80);
        Palette.To(_tilt, RotateTransform.AngleProperty, active ? -5 : 0, 80);
    }

    public void PopIn(TimeSpan delay)
    {
        var ease = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut };
        Palette.To(_pop, ScaleTransform.ScaleXProperty, 1, 120, ease, delay, from: 0);
        Palette.To(_pop, ScaleTransform.ScaleYProperty, 1, 120, ease, delay, from: 0);
    }

    /// <summary>The drop punch: the face presses 2 px into a shrinking shadow.</summary>
    public void Punch()
    {
        _face.RenderTransform = new TranslateTransform(2, 2);
        _shadow.RenderTransform = new TranslateTransform(1, 1);
    }

    public void ResetPunch()
    {
        _face.RenderTransform = Transform.Identity;
        _shadow.RenderTransform = new TranslateTransform(5, 5);
    }

    private void ApplyFill()
    {
        _face.Background = _active ? Palette.Ink : _idleFill;
        _label.Foreground = _hint.Foreground = _active ? Palette.Sunflower : Palette.Ink;
        _shadow.Background = _active ? _activeShadow : Palette.Ink;
    }
}
