using Swivel.Core.Catalog;
using Swivel.Core.Files;
using VipsImage = NetVips.Image;

namespace Swivel.Engines.Images;

/// <summary>Header facts about an input, read once per drag.</summary>
public sealed record ImageProbe(FileKind Kind, int Width, int Height, long Bytes)
{
    public long Pixels => (long)Width * Height;
}

/// <summary>
/// Rough output sizes from bits per pixel for the target format. Always shown as approximate.
/// </summary>
public static class ImageEstimator
{
    public static ImageProbe? Probe(string path)
    {
        try
        {
            var kind = FileSniffer.Detect(path);
            if (FileKinds.Family(kind) != FileFamily.Image) return null;
            long bytes = new FileInfo(path).Length;
            if (kind is FileKind.Heic or FileKind.Bmp or FileKind.Ico)
                return WicDecoder.Probe(path) is var (w, h) ? new ImageProbe(kind, w, h, bytes) : null;
            using var header = VipsImage.NewFromFile(path);
            return new ImageProbe(kind, header.Width, header.Height, bytes);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static long? Estimate(ImageProbe probe, Operation operation)
    {
        if (operation.Engine != Operation.Engines.Image) return null;
        var target = operation.OutputKind ?? probe.Kind;
        double pixels = probe.Pixels;

        if (int.TryParse(operation.Parameter("maxSize", ""), out int max))
        {
            double scale = Math.Min(1, (double)max / Math.Max(probe.Width, probe.Height));
            pixels *= scale * scale;
        }

        // Rotate and strip re-encode at the input's own density; compress lands around 60%.
        if (operation.OutputKind is null)
            return operation.Parameters.ContainsKey("quality") ? (long)(probe.Bytes * 0.6) : probe.Bytes;

        int quality = int.TryParse(operation.Parameter("quality", ""), out int q) ? q : 0;
        double bitsPerPixel = target switch
        {
            FileKind.Jpeg => quality is > 0 and < 75 ? 1.4 : 2.4,
            FileKind.WebP => quality is > 0 and < 75 ? 0.9 : 1.4,
            FileKind.Avif => 0.8,
            FileKind.Png => 10,
            FileKind.Tiff => 14,
            FileKind.Gif => 3.5,
            FileKind.Bmp => 24,
            FileKind.Pdf => 2.6,
            FileKind.Ico => 0,
            _ => 0,
        };
        if (target == FileKind.Ico) return 75_000;
        if (bitsPerPixel == 0) return null;
        long estimate = (long)(pixels * bitsPerPixel / 8);
        double shrink = pixels / probe.Pixels;

        // Lossy to lossy rarely grows: cap by what the input already achieves.
        double? lossyRatio = (probe.Kind, target) switch
        {
            (FileKind.Jpeg or FileKind.WebP or FileKind.Avif or FileKind.Heic, FileKind.WebP) => 0.75,
            (FileKind.Jpeg or FileKind.WebP or FileKind.Avif or FileKind.Heic, FileKind.Avif) => 0.55,
            (FileKind.Jpeg or FileKind.WebP or FileKind.Avif or FileKind.Heic, FileKind.Jpeg) => quality is > 0 and < 75 ? 0.6 : 1.1,
            _ => null,
        };
        if (lossyRatio is double ratio)
            estimate = Math.Min(estimate, (long)(probe.Bytes * shrink * ratio));

        // A lossless re-encode of a lossless file rarely grows past it by much.
        if (target is FileKind.Png or FileKind.Tiff && probe.Kind is FileKind.Png or FileKind.Gif or FileKind.Tiff)
            estimate = Math.Min(estimate, (long)(probe.Bytes * shrink * 1.2));
        return estimate;
    }
}
