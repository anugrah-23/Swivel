namespace Swivel.Core.Files;

public enum FileKind
{
    Unknown,
    // Images
    Jpeg, Png, WebP, Avif, Heic, Gif, Tiff, Bmp, Ico, Svg,
    // Audio
    Mp3, M4a, Wav, Flac, Ogg, Opus,
    // Video
    Mp4, Mov, Mkv, WebM, Avi,
    // Documents
    Docx, Pptx, Xlsx, Markdown, Txt, Html, Epub, Pdf,
    // Archives
    Zip, SevenZip, Tar, Gzip, Rar,
    Folder,
}

public enum FileFamily { Other, Image, Audio, Video, Document, Pdf, Archive, Folder }

public static class FileKinds
{
    private static readonly Dictionary<string, FileKind> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = FileKind.Jpeg, [".jpeg"] = FileKind.Jpeg, [".jfif"] = FileKind.Jpeg,
        [".png"] = FileKind.Png, [".webp"] = FileKind.WebP, [".avif"] = FileKind.Avif,
        [".heic"] = FileKind.Heic, [".heif"] = FileKind.Heic, [".gif"] = FileKind.Gif,
        [".tif"] = FileKind.Tiff, [".tiff"] = FileKind.Tiff, [".bmp"] = FileKind.Bmp,
        [".ico"] = FileKind.Ico, [".svg"] = FileKind.Svg,
        [".mp3"] = FileKind.Mp3, [".m4a"] = FileKind.M4a, [".wav"] = FileKind.Wav,
        [".flac"] = FileKind.Flac, [".ogg"] = FileKind.Ogg, [".oga"] = FileKind.Ogg, [".opus"] = FileKind.Opus,
        [".mp4"] = FileKind.Mp4, [".m4v"] = FileKind.Mp4, [".mov"] = FileKind.Mov, [".mkv"] = FileKind.Mkv,
        [".webm"] = FileKind.WebM, [".avi"] = FileKind.Avi,
        [".docx"] = FileKind.Docx, [".pptx"] = FileKind.Pptx, [".xlsx"] = FileKind.Xlsx,
        [".md"] = FileKind.Markdown, [".markdown"] = FileKind.Markdown, [".txt"] = FileKind.Txt,
        [".html"] = FileKind.Html, [".htm"] = FileKind.Html, [".epub"] = FileKind.Epub, [".pdf"] = FileKind.Pdf,
        [".zip"] = FileKind.Zip, [".7z"] = FileKind.SevenZip, [".tar"] = FileKind.Tar,
        [".gz"] = FileKind.Gzip, [".tgz"] = FileKind.Gzip, [".rar"] = FileKind.Rar,
    };

    private static readonly Dictionary<FileKind, string> CanonicalExtension = new()
    {
        [FileKind.Jpeg] = ".jpg", [FileKind.Png] = ".png", [FileKind.WebP] = ".webp", [FileKind.Avif] = ".avif",
        [FileKind.Heic] = ".heic", [FileKind.Gif] = ".gif", [FileKind.Tiff] = ".tiff", [FileKind.Bmp] = ".bmp",
        [FileKind.Ico] = ".ico", [FileKind.Svg] = ".svg",
        [FileKind.Mp3] = ".mp3", [FileKind.M4a] = ".m4a", [FileKind.Wav] = ".wav", [FileKind.Flac] = ".flac",
        [FileKind.Ogg] = ".ogg", [FileKind.Opus] = ".opus",
        [FileKind.Mp4] = ".mp4", [FileKind.Mov] = ".mov", [FileKind.Mkv] = ".mkv", [FileKind.WebM] = ".webm", [FileKind.Avi] = ".avi",
        [FileKind.Docx] = ".docx", [FileKind.Pptx] = ".pptx", [FileKind.Xlsx] = ".xlsx", [FileKind.Markdown] = ".md",
        [FileKind.Txt] = ".txt", [FileKind.Html] = ".html", [FileKind.Epub] = ".epub", [FileKind.Pdf] = ".pdf",
        [FileKind.Zip] = ".zip", [FileKind.SevenZip] = ".7z", [FileKind.Tar] = ".tar", [FileKind.Gzip] = ".gz", [FileKind.Rar] = ".rar",
    };

    public static FileKind FromExtension(string pathOrExtension)
    {
        string ext = pathOrExtension.StartsWith('.') ? pathOrExtension : Path.GetExtension(pathOrExtension);
        return ByExtension.TryGetValue(ext, out var kind) ? kind : FileKind.Unknown;
    }

    public static string? Extension(FileKind kind) => CanonicalExtension.GetValueOrDefault(kind);

    public static FileFamily Family(FileKind kind) => kind switch
    {
        >= FileKind.Jpeg and <= FileKind.Svg => FileFamily.Image,
        >= FileKind.Mp3 and <= FileKind.Opus => FileFamily.Audio,
        >= FileKind.Mp4 and <= FileKind.Avi => FileFamily.Video,
        FileKind.Pdf => FileFamily.Pdf,
        >= FileKind.Docx and <= FileKind.Epub => FileFamily.Document,
        >= FileKind.Zip and <= FileKind.Rar => FileFamily.Archive,
        FileKind.Folder => FileFamily.Folder,
        _ => FileFamily.Other,
    };
}
