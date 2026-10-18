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

    public static System.Drawing.Icon CreateIcon(int size) => new(new MemoryStream(IcoBytes([size])));

    /// <summary>
    /// Writes Assets\swivel.ico (embedded in Swivel.exe) and the PNG tiles the Windows 11 shell
    /// package needs. Run with <c>Swivel.exe --export-assets dir</c> after changing the mark.
    /// </summary>
    public static void ExportAssets(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "swivel.ico"), IcoBytes([16, 20, 24, 32, 40, 48, 64, 256]));
        foreach (var (name, size) in new[] { ("Logo44.png", 44), ("Logo150.png", 150), ("StoreLogo.png", 50) })
            File.WriteAllBytes(Path.Combine(directory, name), Png(size));
    }

    /// <summary>An ICO whose entries are PNGs (supported since Windows Vista).</summary>
    private static byte[] IcoBytes(int[] sizes)
    {
        var images = sizes.Select(Png).ToArray();
        using var ico = new MemoryStream();
        using (var w = new BinaryWriter(ico, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                byte dimension = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                w.Write(dimension); w.Write(dimension); w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(images[i].Length); w.Write(offset);
                offset += images[i].Length;
            }
            foreach (var png in images) w.Write(png);
        }
        return ico.ToArray();
    }

    private static byte[] Png(int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) Draw(dc, size);
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var png = new MemoryStream();
        encoder.Save(png);
        return png.ToArray();
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
