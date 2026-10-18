using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.App.Services;

namespace Swivel.App.Shell;

/// <summary>Renders Swivel's windows to PNGs without showing them (<c>Swivel.exe --snapshot</c>), for design review.</summary>
internal static class Snapshots
{
    public static void Windows(string outDir, SwivelServices services, ShellState state)
    {
        var flyout = new FlyoutWindow(services, state, () => { }, () => { });
        flyout.Build();
        Render(flyout, Path.Combine(outDir, "flyout.png"));

        // A fresh window per page: detached trees don't re-run layout when their content changes.
        foreach (string section in new[] { "General", "Shortcuts", "Wheels", "Output", "Formats and engines", "About" })
        {
            var settings = new SettingsWindow(state, null);
            settings.Show(section);
            Render(settings, Path.Combine(outDir, $"settings-{section.Split(' ')[0].ToLowerInvariant()}.png"), fixedHeight: true);
        }

        for (int step = 0; step < 3; step++)
        {
            var onboarding = new OnboardingWindow(state);
            onboarding.Go(step);
            Render(onboarding, Path.Combine(outDir, $"onboarding-{step + 1}.png"));
        }
    }

    private static void Render(Window window, string path, bool fixedHeight = false)
    {
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        double width = window.Width, height = fixedHeight ? window.Height : double.PositiveInfinity;
        content.Measure(new Size(width, height));
        var size = new Size(width, fixedHeight ? height : content.DesiredSize.Height);
        content.Arrange(new Rect(size));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)(size.Width * 1.5), (int)(size.Height * 1.5), 144, 144, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(path)) encoder.Save(file);
        window.Content = content;
    }
}
