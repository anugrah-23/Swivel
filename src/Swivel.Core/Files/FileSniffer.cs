using System.Text;

namespace Swivel.Core.Files;

/// <summary>Identifies a file from its magic bytes first and its extension second.</summary>
public static class FileSniffer
{
    private const int HeaderLength = 512;

    public static FileKind Detect(string path)
    {
        if (Directory.Exists(path)) return FileKind.Folder;
        var byExtension = FileKinds.FromExtension(path);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
            Span<byte> header = stackalloc byte[HeaderLength];
            int read = stream.ReadAtLeast(header, HeaderLength, throwOnEndOfStream: false);
            return Detect(header[..read], byExtension);
        }
        catch (IOException) { return byExtension; }
        catch (UnauthorizedAccessException) { return byExtension; }
    }

    public static FileKind Detect(ReadOnlySpan<byte> h, FileKind byExtension)
    {
        if (Has(h, 0xFF, 0xD8, 0xFF)) return FileKind.Jpeg;
        if (Has(h, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)) return FileKind.Png;
        if (h.StartsWith("GIF8"u8)) return FileKind.Gif;
        if (h.StartsWith("II*\0"u8) || h.StartsWith("MM\0*"u8)) return FileKind.Tiff;
        if (h.StartsWith("%PDF"u8)) return FileKind.Pdf;
        if (h.StartsWith("fLaC"u8)) return FileKind.Flac;
        if (h.StartsWith("Rar!\x1A\x07"u8)) return FileKind.Rar;
        if (Has(h, 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C)) return FileKind.SevenZip;
        if (Has(h, 0x1F, 0x8B)) return FileKind.Gzip;
        if (Has(h, 0x00, 0x00, 0x01, 0x00) && byExtension is FileKind.Ico or FileKind.Unknown) return FileKind.Ico;
        if (h.StartsWith("BM"u8) && byExtension is FileKind.Bmp or FileKind.Unknown) return FileKind.Bmp;

        if (h.Length >= 12 && h.StartsWith("RIFF"u8))
        {
            var form = h.Slice(8, 4);
            if (form.SequenceEqual("WEBP"u8)) return FileKind.WebP;
            if (form.SequenceEqual("WAVE"u8)) return FileKind.Wav;
            if (form.SequenceEqual("AVI "u8)) return FileKind.Avi;
        }

        if (h.Length >= 12 && h.Slice(4, 4).SequenceEqual("ftyp"u8)) return FromFtyp(h, byExtension);

        if (Has(h, 0x1A, 0x45, 0xDF, 0xA3))
            return h.IndexOf("webm"u8) >= 0 ? FileKind.WebM : FileKind.Mkv;

        if (h.StartsWith("OggS"u8))
            return h.IndexOf("OpusHead"u8) >= 0 ? FileKind.Opus : FileKind.Ogg;

        if (h.StartsWith("ID3"u8) || (h.Length >= 2 && h[0] == 0xFF && (h[1] & 0xE0) == 0xE0 && byExtension is FileKind.Mp3 or FileKind.Unknown))
            return FileKind.Mp3;

        if (h.StartsWith("PK\x03\x04"u8))
            return byExtension is FileKind.Docx or FileKind.Pptx or FileKind.Xlsx or FileKind.Epub ? byExtension : FileKind.Zip;

        if (h.Length >= 262 && h.Slice(257, 5).SequenceEqual("ustar"u8)) return FileKind.Tar;

        if (byExtension is FileKind.Svg or FileKind.Unknown or FileKind.Html or FileKind.Txt && LooksLikeSvg(h))
            return FileKind.Svg;

        return byExtension;
    }

    private static FileKind FromFtyp(ReadOnlySpan<byte> h, FileKind byExtension)
    {
        // Major brand at 8; compatible brands follow the minor version at 16 until the box ends.
        int boxSize = Math.Min((h[0] << 24) | (h[1] << 16) | (h[2] << 8) | h[3], h.Length);
        var brands = new List<string> { Encoding.ASCII.GetString(h.Slice(8, 4)) };
        for (int i = 16; i + 4 <= boxSize; i += 4) brands.Add(Encoding.ASCII.GetString(h.Slice(i, 4)));

        if (brands.Any(b => b is "avif" or "avis")) return FileKind.Avif;
        if (brands.Any(b => b is "heic" or "heix" or "heim" or "heis" or "hevc" or "hevx" or "mif1" or "msf1")) return FileKind.Heic;
        if (brands[0] is "M4A " or "M4B ") return FileKind.M4a;
        if (brands[0] == "qt  ") return FileKind.Mov;
        return byExtension is FileKind.M4a or FileKind.Mov ? byExtension : FileKind.Mp4;
    }

    private static bool Has(ReadOnlySpan<byte> header, params ReadOnlySpan<byte> signature) => header.StartsWith(signature);

    private static bool LooksLikeSvg(ReadOnlySpan<byte> h)
    {
        string text = Encoding.UTF8.GetString(h);
        return text.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }
}
