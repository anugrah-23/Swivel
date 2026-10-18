using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.App.Feedback;
using Swivel.App.Theme;

namespace Swivel.App.Actions;

/// <summary>
/// Float: pins files as a small card that stays on top, ready to drag into any app later. Drag the
/// header to move it; drag the body to drop the files somewhere; double-click opens; ✕ closes.
/// </summary>
internal sealed class FloatWindow : FloatingWindow
{
    private static int _cascade;
    private readonly IReadOnlyList<string> _paths;
    private Point? _pressed;

    public FloatWindow(IReadOnlyList<string> paths)
    {
        _paths = paths;
        Width = 200;
        SizeToContent = SizeToContent.Height;
        var area = SystemParameters.WorkArea;
        int step = _cascade++ % 6;
        Left = area.Right - 230 - step * 24;
        Top = area.Top + 40 + step * 24;
        Title = "Swivel float";

        string first = Path.GetFileName(paths[0].TrimEnd('\\'));
        var close = new Button { Content = "✕", Style = (Style)FindResource("SwivelButton"), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0) };
        close.Click += (_, _) => Close();
        var header = new DockPanel { Background = Palette.Lilac, Cursor = Cursors.SizeAll, LastChildFill = true };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(new TextBlock
        {
            Text = paths.Count == 1 ? "FLOAT" : $"FLOAT ×{paths.Count}",
            FontFamily = Palette.Display,
            FontWeight = FontWeights.Black,
            FontSize = 13,
            Foreground = Palette.Ink,
            VerticalAlignment = VerticalAlignment.Center,
        });
        header.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not Button) DragMove(); };

        var preview = new Border
        {
            Height = 110,
            Background = Palette.Cream,
            BorderBrush = Palette.Ink,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 8, 0, 6),
            Child = Thumbnail(paths[0]) is { } thumb
                ? new Image { Source = thumb, Stretch = Stretch.Uniform, Margin = new Thickness(4) }
                : new TextBlock
                {
                    Text = Path.GetExtension(first).TrimStart('.').ToUpperInvariant() is { Length: > 0 } ext ? ext : "DIR",
                    FontFamily = Palette.Mono, FontWeight = FontWeights.Bold, FontSize = 24,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
        };
        var body = new StackPanel
        {
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Children =
            {
                preview,
                new TextBlock { Text = paths.Count == 1 ? first : $"{first} + {paths.Count - 1} more", FontFamily = Palette.Body, FontWeight = FontWeights.Bold, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Palette.Ink },
                new TextBlock { Text = "Drag me anywhere", FontFamily = Palette.Mono, FontSize = 10, Foreground = Palette.Ink, Opacity = 0.7 },
            },
        };
        body.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { foreach (var p in _paths) Toast.Launch(p); return; }
            _pressed = e.GetPosition(this);
        };
        body.PreviewMouseMove += (_, e) =>
        {
            if (_pressed is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
            var now = e.GetPosition(this);
            if (Math.Abs(now.X - start.X) < 6 && Math.Abs(now.Y - start.Y) < 6) return;
            _pressed = null;
            var data = new DataObject(DataFormats.FileDrop, _paths.ToArray());
            DragDrop.DoDragDrop(this, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        };
        body.PreviewMouseLeftButtonUp += (_, _) => _pressed = null;

        var card = new Border
        {
            Background = Palette.Paper,
            BorderBrush = Palette.Ink,
            BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 8, 10, 10),
            Child = new StackPanel { Children = { header, body } },
        };
        var root = new Grid { Margin = new Thickness(0, 0, 5, 5) };
        root.Children.Add(new Border { Background = Palette.Ink, CornerRadius = new CornerRadius(12), Margin = new Thickness(5, 5, -5, -5) });
        root.Children.Add(card);
        Content = root;
    }

    private static BitmapSource? Thumbnail(string path)
    {
        if (Core.Files.FileKinds.Family(Core.Files.FileSniffer.Detect(path)) != Core.Files.FileFamily.Image) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.DecodePixelHeight = 200;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception) { return null; }
    }
}
