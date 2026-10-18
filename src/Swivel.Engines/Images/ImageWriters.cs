using PdfSharp.Drawing;
using PdfSharp.Pdf;
using NetVips;
using VipsImage = NetVips.Image;

namespace Swivel.Engines.Images;

/// <summary>Formats libvips doesn't write itself: BMP, ICO and single-page PDF.</summary>
internal static class ImageWriters
{
    private static readonly int[] IconSizes = [16, 32, 48, 256];
    private const double PdfDpi = 150;

    /// <summary>24-bit bottom-up BMP, alpha flattened onto white.</summary>
    public static void WriteBmp(VipsImage image, string path)
    {
        using var rgb = Opaque(image);
        int width = rgb.Width, height = rgb.Height;
        byte[] pixels = rgb.WriteToMemory<byte>();
        int stride = (width * 3 + 3) & ~3;
        int imageSize = stride * height;

        using var file = File.Create(path);
        using var w = new BinaryWriter(file);
        w.Write((byte)'B'); w.Write((byte)'M');
        w.Write(14 + 40 + imageSize); w.Write(0); w.Write(14 + 40);
        w.Write(40); w.Write(width); w.Write(height); w.Write((short)1); w.Write((short)24);
        w.Write(0); w.Write(imageSize); w.Write(3780); w.Write(3780); w.Write(0); w.Write(0);

        var row = new byte[stride];
        for (int y = height - 1; y >= 0; y--)
        {
            int source = y * width * 3;
            for (int x = 0; x < width; x++)
            {
                row[x * 3] = pixels[source + x * 3 + 2];
                row[x * 3 + 1] = pixels[source + x * 3 + 1];
                row[x * 3 + 2] = pixels[source + x * 3];
            }
            w.Write(row);
        }
    }

    /// <summary>A multi-size ICO (16, 32, 48 and 256 px), each entry a square transparent PNG.</summary>
    public static void WriteIco(VipsImage image, string path)
    {
        var entries = new List<(int Size, byte[] Png)>();
        foreach (int size in IconSizes)
        {
            using var thumb = image.ThumbnailImage(size, height: size);
            using var alpha = thumb.HasAlpha() ? thumb.Copy() : thumb.Bandjoin(255);
            using var square = alpha.Gravity(Enums.CompassDirection.Centre, size, size,
                extend: Enums.Extend.Background, background: [0, 0, 0, 0]);
            entries.Add((size, square.PngsaveBuffer()));
        }

        using var file = File.Create(path);
        using var w = new BinaryWriter(file);
        w.Write((short)0); w.Write((short)1); w.Write((short)entries.Count);
        int offset = 6 + 16 * entries.Count;
        foreach (var (size, png) in entries)
        {
            byte dimension = (byte)(size >= 256 ? 0 : size);
            w.Write(dimension); w.Write(dimension); w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32); w.Write(png.Length); w.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in entries) w.Write(png);
    }

    /// <summary>One page sized to the image at 150 DPI.</summary>
    public static void WritePdf(VipsImage image, string path)
    {
        using var document = new PdfDocument();
        AddPage(document, image);
        using var file = File.Create(path);
        document.Save(file);
    }

    /// <summary>Appends a page sized to the image at 150 DPI.</summary>
    public static void AddPage(PdfDocument document, VipsImage image)
    {
        byte[] encoded = image.HasAlpha() ? image.PngsaveBuffer() : image.JpegsaveBuffer(q: 90);
        var page = document.AddPage();
        page.Width = XUnit.FromPoint(image.Width * 72 / PdfDpi);
        page.Height = XUnit.FromPoint(image.Height * 72 / PdfDpi);

        using var stream = new MemoryStream(encoded);
        using var picture = XImage.FromStream(stream);
        using var gfx = XGraphics.FromPdfPage(page);
        gfx.DrawImage(picture, 0, 0, page.Width.Point, page.Height.Point);
    }

    /// <summary>Three-band 8-bit sRGB with any alpha flattened onto white.</summary>
    public static VipsImage Opaque(VipsImage image)
    {
        using var flat = image.HasAlpha() ? image.Flatten(background: [255, 255, 255]) : image.Copy();
        return flat.Bands >= 3 ? flat.Cast(Enums.BandFormat.Uchar) : flat.Colourspace(Enums.Interpretation.Srgb);
    }
}
