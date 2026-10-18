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

    /// <summary>The animation preference from Swivel's own settings.</summary>
    public static bool UserAnimations { get; set; } = true;

    /// <summary>Motion plays only when both Windows "Show animations" and Swivel's setting allow it.</summary>
    public static bool Animations => UserAnimations && SystemParameters.ClientAreaAnimation;

    private static T Get<T>(string key) => (T)Application.Current.FindResource(key);

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
