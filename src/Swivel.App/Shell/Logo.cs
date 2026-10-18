using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.App.Theme;

namespace Swivel.App.Shell;

/// <summary>The Swivel mark: a sunflower circle, ink outline, and an ink quarter wedge.</summary>
internal static class Logo
{
    public static DrawingImage CreateImage()
    {
        var group = new DrawingGroup();
        using (var dc = group.Open()) Draw(dc, 32);
        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    public static System.Drawing.Icon CreateIcon(int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) Draw(dc, size);
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var png = new MemoryStream();
        encoder.Save(png);

        // A single-image ICO whose entry is the PNG itself (supported since Windows Vista).
        using var ico = new MemoryStream();
        using (var w = new BinaryWriter(ico, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)1);
            w.Write((byte)(size >= 256 ? 0 : size)); w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32);
            w.Write((int)png.Length); w.Write(22);
            w.Write(png.ToArray());
        }
        ico.Position = 0;
        return new System.Drawing.Icon(ico);
    }

    private static void Draw(DrawingContext dc, double size)
    {
        double s = size / 32.0, c = 16 * s, r = 13.5 * s;
        var outline = new Pen(Palette.Ink, 2.5 * s);
        dc.DrawEllipse(Palette.Sunflower, outline, new Point(c, c), r, r);

        var wedge = new StreamGeometry();
        using (var g = wedge.Open())
        {
            g.BeginFigure(new Point(c, c), isFilled: true, isClosed: true);
            g.LineTo(new Point(c, c - r), true, false);
            g.ArcTo(new Point(c + r, c), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
        }
        wedge.Freeze();
        dc.DrawGeometry(Palette.Ink, null, wedge);
    }
}
