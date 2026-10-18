using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Swivel.App.Theme;

/// <summary>Typed access to the design tokens in Tokens.xaml, plus the shared motion helper.</summary>
internal static class Palette
{
    public static Brush Ink => Get<Brush>("Ink");
    public static Brush Cream => Get<Brush>("Cream");
    public static Brush Paper => Get<Brush>("Paper");
    public static Brush Sunflower => Get<Brush>("Sunflower");
    public static Brush Pink => Get<Brush>("Pink");
    public static Brush Sky => Get<Brush>("Sky");
    public static Brush Mint => Get<Brush>("Mint");
    public static Brush Orange => Get<Brush>("Orange");
    public static Brush Lilac => Get<Brush>("Lilac");

    public static FontFamily Display => Get<FontFamily>("DisplayFont");
    public static FontFamily Body => Get<FontFamily>("BodyFont");
    public static FontFamily Mono => Get<FontFamily>("MonoFont");

    /// <summary>True once the high-contrast palette is in force.</summary>
    public static bool HighContrast { get; private set; }

    /// <summary>Token brushes are created fresh per use, so compare them by colour, not reference.</summary>
    public static bool Same(Brush? a, Brush? b) =>
        a is SolidColorBrush x && b is SolidColorBrush y && x.Color == y.Color;

    /// <summary>The animation preference from Swivel's own settings.</summary>
    public static bool UserAnimations { get; set; } = true;

    /// <summary>Motion plays only when both Windows "Show animations" and Swivel's setting allow it.</summary>
    public static bool Animations => UserAnimations && SystemParameters.ClientAreaAnimation;

    private static T Get<T>(string key) => (T)Application.Current.FindResource(key);

    private static readonly string[] Tokens = ["Ink", "Cream", "Paper", "Sunflower", "Pink", "Sky", "Mint", "Orange", "Lilac"];

    /// <summary>
    /// Creates the token brushes in the application's resources. Must run before any window or style
    /// loads (styles and templates pick the brushes up on first use). In high-contrast mode the palette
    /// collapses to system colours while outlines, shapes and labels stay, so state is still carried by
    /// fill and shape rather than hue.
    /// </summary>
    public static void InstallBrushes(ResourceDictionary resources, bool highContrast)
    {
        HighContrast = highContrast;
        // Tokens.xaml styles use {DynamicResource Ink}; windows use {StaticResource Ink} when they load.
        var tokens = resources;
        foreach (string token in Tokens)
        {
            Color colour = !highContrast ? (Color)tokens[token + "Color"] : token switch
            {
                "Ink" => SystemColors.WindowTextColor,
                "Pink" => SystemColors.HighlightColor,
                _ => SystemColors.WindowColor,
            };
            var brush = new SolidColorBrush(colour);
            brush.Freeze();
            tokens[token] = brush;
        }
    }

    /// <summary>Animates a double property, or snaps it when animations are off.</summary>
    public static void To(Animatable target, DependencyProperty property, double value, int ms,
        IEasingFunction? easing = null, TimeSpan? delay = null, double? from = null)
    {
        if (!Animations || ms <= 0)
        {
            target.BeginAnimation(property, null);
            target.SetValue(property, value);
            return;
        }
        var animation = new DoubleAnimation(value, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = easing ?? new QuadraticEase { EasingMode = EasingMode.EaseOut },
            BeginTime = delay ?? TimeSpan.Zero,
        };
        if (from is double start) animation.From = start;
        target.BeginAnimation(property, animation);
    }
}
