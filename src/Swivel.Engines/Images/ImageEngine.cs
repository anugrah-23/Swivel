using NetVips;
using Operation = Swivel.Core.Catalog.Operation;
using Swivel.Core.Catalog;
using Swivel.Core.Files;
using Swivel.Core.Jobs;
using VipsImage = NetVips.Image;

namespace Swivel.Engines.Images;

/// <summary>Image conversions and tools on libvips, with HEIC decoded through WIC.</summary>
public sealed class ImageEngine : IEngine
{
    internal static readonly Lazy<bool> Configured = new(() =>
    {
        // Every job is a fresh file, so libvips' operation cache only costs memory. Done lazily so
        // libvips isn't loaded into the tray process until the first image job.
        Cache.Max = 0;
        Cache.MaxFiles = 0;
        return true;
    });

    public string Name => Operation.Engines.Image;

    public Task RunAsync(Job job, IProgress<double> progress, CancellationToken cancellationToken) =>
        Task.Run(() => Run(job, progress, cancellationToken), cancellationToken);

    private static void Run(Job job, IProgress<double> progress, CancellationToken ct)
    {
        _ = Configured.Value;
        string input = job.Inputs[0];
        var operation = job.Operation;
        var kind = FileSniffer.Detect(input);
        var owned = new List<VipsImage>();
        VipsImage Own(VipsImage image) { owned.Add(image); return image; }

        try
        {
            var image = Own(Load(input, kind));
            progress.Report(0.15);
            ct.ThrowIfCancellationRequested();

            image = Own(image.Autorot());

            if (int.TryParse(operation.Parameter("maxSize", ""), out int max) && Math.Max(image.Width, image.Height) > max)
                image = Own(image.ThumbnailImage(max, height: max, size: Enums.Size.Down));

            // Resize panel: a scale ("0.5"), a longest side ("1920"), or width and/or height in pixels
            // (0 = follow the other side). Each image in a batch keeps its own shape.
            int.TryParse(operation.Parameter("width", "0"), out int width);
            int.TryParse(operation.Parameter("height", "0"), out int height);
            if (double.TryParse(operation.Parameter("scale", ""), System.Globalization.CultureInfo.InvariantCulture, out double scale) && scale > 0)
                width = Math.Max(1, (int)Math.Round(image.Width * scale));
            if (int.TryParse(operation.Parameter("longest", ""), out int longest) && longest > 0)
                (width, height) = image.Width >= image.Height ? (longest, 0) : (0, longest);
            if (width > 0 || height > 0)
            {
                if (width <= 0) width = (int)Math.Round(image.Width * (double)height / image.Height);
                if (height <= 0) height = (int)Math.Round(image.Height * (double)width / image.Width);
                image = Own(image.ThumbnailImage(width, height: height, size: Enums.Size.Force));
            }

            // Crop panel: an aspect such as "16:9", cut where the interesting part of the picture is.
            if (ParseAspect(operation.Parameter("aspect", "")) is double aspect)
            {
                (int cw, int ch) = image.Width / (double)image.Height > aspect
                    ? ((int)Math.Round(image.Height * aspect), image.Height)
                    : (image.Width, (int)Math.Round(image.Width / aspect));
                if (cw < image.Width || ch < image.Height)
                    image = Own(image.Smartcrop(Math.Max(1, cw), Math.Max(1, ch), interesting: Enums.Interesting.Attention));
            }

            image = operation.Parameter("rotate", "") switch
            {
                "90" => Own(image.Rot(Enums.Angle.D90)),
                "180" => Own(image.Rot(Enums.Angle.D180)),
                "270" => Own(image.Rot(Enums.Angle.D270)),
                _ => image,
            };

            image = Own(ToEightBitSrgb(image));
            progress.Report(0.4);
            ct.ThrowIfCancellationRequested();

            var target = operation.OutputKind ?? kind;
            bool strip = operation.Parameter("strip", "false") == "true";
            int? quality = int.TryParse(operation.Parameter("quality", ""), out int q) ? q : null;
            Save(image, target, job.TempPath!, strip ? Enums.ForeignKeep.Icc : Enums.ForeignKeep.All, quality);
            progress.Report(1);
        }
        catch (VipsException ex)
        {
            throw new JobException($"Couldn't crack {Path.GetFileName(input)}.", ex);
        }
        finally
        {
            foreach (var image in owned) image.Dispose();
        }
    }

    private static double? ParseAspect(string text)
    {
        var parts = text.Split(':');
        return parts.Length == 2 && double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out double w)
            && double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out double h) && w > 0 && h > 0
            ? w / h : null;
    }

    internal static VipsImage Load(string path, FileKind kind)
    {
        if (kind == FileKind.Heic) return WicDecoder.Decode(path);
        try
        {
            return VipsImage.NewFromFile(path, access: Enums.Access.Random, failOn: Enums.FailOn.Error);
        }
        catch (VipsException) when (kind is FileKind.Bmp or FileKind.Ico or FileKind.Tiff)
        {
            // libvips has no BMP or ICO loader in this build; WIC reads both.
            return WicDecoder.Decode(path);
        }
    }

    internal static VipsImage ToEightBitSrgb(VipsImage image)
    {
        bool colour = image.Bands >= 3 || image.Interpretation is Enums.Interpretation.Cmyk or Enums.Interpretation.Lab;
        var space = colour ? Enums.Interpretation.Srgb : Enums.Interpretation.Bw;
        using var converted = image.Interpretation == space ? image.Copy() : image.Colourspace(space);
        return converted.Format == Enums.BandFormat.Uchar ? converted.Copy() : converted.Cast(Enums.BandFormat.Uchar);
    }

    private static void Save(VipsImage image, FileKind target, string path, Enums.ForeignKeep keep, int? quality)
    {
        switch (target)
        {
            case FileKind.WebP:
                image.Webpsave(path, q: quality ?? 80, effort: 4, keep: keep);
                break;
            case FileKind.Jpeg:
                using (var flat = ImageWriters.Opaque(image))
                    flat.Jpegsave(path, q: quality ?? 85, optimizeCoding: true, keep: keep);
                break;
            case FileKind.Png:
                image.Pngsave(path, compression: 6, keep: keep);
                break;
            case FileKind.Avif:
                image.Heifsave(path, q: quality ?? 55, compression: Enums.ForeignHeifCompression.Av1, effort: 4, keep: keep);
                break;
            case FileKind.Gif:
                image.Gifsave(path, keep: keep);
                break;
            case FileKind.Tiff:
                image.Tiffsave(path, compression: Enums.ForeignTiffCompression.Deflate, keep: keep);
                break;
            case FileKind.Bmp:
                ImageWriters.WriteBmp(image, path);
                break;
            case FileKind.Ico:
                ImageWriters.WriteIco(image, path);
                break;
            case FileKind.Pdf:
                ImageWriters.WritePdf(image, path);
                break;
            default:
                throw new JobException($"Swivel can't write {target} images yet.");
        }
    }
}
