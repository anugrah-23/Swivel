using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core.Jobs;
using VipsImage = NetVips.Image;

namespace Swivel.Engines.Images;

/// <summary>
/// Thrown when Windows lacks the codec for a format (HEIC needs the HEIF and HEVC extensions).
/// Swivel never ships an HEVC decoder itself.
/// </summary>
public sealed class CodecMissingException(string message, string storeUrl) : JobException(message)
{
    public override string? HelpUrl => storeUrl;
}

/// <summary>Decodes through Windows Imaging Component, which picks up Store codecs such as HEIF.</summary>
internal static class WicDecoder
{
    public const string HevcStoreUrl = "ms-windows-store://pdp/?ProductId=9NMZLZ57R3T7";

    public static VipsImage Decode(string path)
    {
        var (pixels, width, height) = RunOnSta(() =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames[0];
            if (frame.Format != PixelFormats.Bgra32)
                frame = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var buffer = new byte[frame.PixelWidth * frame.PixelHeight * 4];
            frame.CopyPixels(buffer, frame.PixelWidth * 4, 0);
            return (buffer, frame.PixelWidth, frame.PixelHeight);
        }, path);

        using var bgra = VipsImage.NewFromMemory(pixels, width, height, 4, NetVips.Enums.BandFormat.Uchar);
        using var b = bgra[0];
        using var g = bgra[1];
        using var r = bgra[2];
        using var a = bgra[3];
        using var rgba = r.Bandjoin(g, b, a);
        // Copy so the result owns its pixels once the managed buffer goes away.
        return rgba.Copy(interpretation: NetVips.Enums.Interpretation.Srgb).CopyMemory();
    }

    /// <summary>Reads only the header, for size estimates.</summary>
    public static (int Width, int Height)? Probe(string path)
    {
        try
        {
            return RunOnSta(() =>
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                return (decoder.Frames[0].PixelWidth, decoder.Frames[0].PixelHeight);
            }, path);
        }
        catch (JobException) { return null; }
    }

    private static T RunOnSta<T>(Func<T> work, string path)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { error = ex; }
        })
        { IsBackground = true, Name = "Swivel.Wic" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error is null) return result;
        if (IsCodecMissing(error) && FileNameIsHeif(path))
            throw new CodecMissingException("Windows needs the HEIF and HEVC extensions to open HEIC photos.", HevcStoreUrl);
        if (error is NotSupportedException or FileFormatException || IsCodecMissing(error))
            throw new JobException($"Couldn't read {Path.GetFileName(path)} as an image.", error);
        throw new JobException($"Couldn't read {Path.GetFileName(path)}.", error);
    }

    // WINCODEC_ERR_COMPONENTNOTFOUND, WINCODEC_ERR_UNKNOWNIMAGEFORMAT and MF_E_TOPO_CODEC_NOT_FOUND
    // (the HEIF container extension is present but no HEVC decoder is).
    private static readonly int[] CodecMissingResults = [unchecked((int)0x88982F50), unchecked((int)0x88982F07), unchecked((int)0xC00D5212)];

    private static bool IsCodecMissing(Exception error)
    {
        for (Exception? e = error; e != null; e = e.InnerException)
        {
            if (e is NotSupportedException) return true;
            if (Array.IndexOf(CodecMissingResults, e.HResult) >= 0) return true;
        }
        return false;
    }

    private static bool FileNameIsHeif(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".heic" or ".heif" || Core.Files.FileSniffer.Detect(path) == Core.Files.FileKind.Heic;
}
