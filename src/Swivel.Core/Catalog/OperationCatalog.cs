using Swivel.Core.Files;
using static Swivel.Core.Catalog.Operation;

namespace Swivel.Core.Catalog;

/// <summary>The static table of wedges plus user presets, filtered and ranked per selection.</summary>
public sealed class OperationCatalog
{
    public const int WedgesPerRing = 8;

    private static readonly HashSet<FileKind> RasterIn =
        [FileKind.Jpeg, FileKind.Png, FileKind.WebP, FileKind.Avif, FileKind.Heic, FileKind.Gif, FileKind.Tiff, FileKind.Bmp];
    private static readonly HashSet<FileKind> ImageIn = [.. RasterIn, FileKind.Svg];
    private static readonly HashSet<FileKind> Lossy = [FileKind.Jpeg, FileKind.WebP, FileKind.Avif];
    private static readonly HashSet<FileKind> Rotatable = [FileKind.Jpeg, FileKind.Png, FileKind.WebP, FileKind.Avif, FileKind.Tiff];

    private static readonly HashSet<FileKind> Audio = [FileKind.Mp3, FileKind.M4a, FileKind.Wav, FileKind.Flac, FileKind.Ogg, FileKind.Opus];
    private static readonly HashSet<FileKind> Video = [FileKind.Mp4, FileKind.Mov, FileKind.Mkv, FileKind.WebM, FileKind.Avi];
    private static readonly HashSet<FileKind> Media = [.. Audio, .. Video];

    private static readonly HashSet<FileKind> Text = [FileKind.Docx, FileKind.Markdown, FileKind.Html, FileKind.Epub, FileKind.Txt];
    private static readonly HashSet<FileKind> Printable = [.. Text, FileKind.Pptx, FileKind.Xlsx];
    private static readonly HashSet<FileKind> Pdf = [FileKind.Pdf];
    private static readonly HashSet<FileKind> Archives = [FileKind.Zip, FileKind.SevenZip, FileKind.Tar, FileKind.Gzip, FileKind.Rar];

    private static readonly HashSet<FileKind> Anything = [.. Enum.GetValues<FileKind>()];
    private static readonly HashSet<FileKind> AnyFile = [.. Anything.Where(k => k != FileKind.Folder)];

    private static Dictionary<string, string> P(params (string Key, string Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    public static IReadOnlyList<Operation> BuiltIn { get; } =
    [
        // ---- Convert: images
        new("image.webp", WheelMode.Convert, "WEBP", "Smaller", ImageIn, ".webp", Engines.Image),
        new("image.png", WheelMode.Convert, "PNG", "Lossless", ImageIn, ".png", Engines.Image),
        new("image.jpg", WheelMode.Convert, "JPG", "Universal", ImageIn, ".jpg", Engines.Image),
        new("image.avif", WheelMode.Convert, "AVIF", "Tiny", ImageIn, ".avif", Engines.Image),
        new("image.pdf", WheelMode.Convert, "PDF", "Print", ImageIn, ".pdf", Engines.Image),
        new("preset.web-image", WheelMode.Convert, "WEB", "1600 px", ImageIn, ".webp", Engines.Image)
        {
            Parameters = P(("maxSize", "1600"), ("strip", "true")),
        },
        new("image.gif", WheelMode.Convert, "GIF", "Retro", ImageIn, ".gif", Engines.Image),
        new("image.tiff", WheelMode.Convert, "TIFF", "Archive", ImageIn, ".tiff", Engines.Image),
        new("image.ico", WheelMode.Convert, "ICO", "Icon", ImageIn, ".ico", Engines.Image),
        new("image.bmp", WheelMode.Convert, "BMP", "Raw", ImageIn, ".bmp", Engines.Image),

        // ---- Convert: video (heavy), then audio, which also extracts the soundtrack from video
        new("video.mp4", WheelMode.Convert, "MP4", "Universal", Video, ".mp4", Engines.Media) { Heavy = true },
        new("video.webm", WheelMode.Convert, "WEBM", "Web", Video, ".webm", Engines.Media) { Heavy = true },
        new("video.gif", WheelMode.Convert, "GIF", "Loop", Video, ".gif", Engines.Media) { Heavy = true },
        new("video.mov", WheelMode.Convert, "MOV", "Apple", Video, ".mov", Engines.Media) { Heavy = true },
        new("video.mkv", WheelMode.Convert, "MKV", "No loss", Video, ".mkv", Engines.Media) { Heavy = true },
        new("video.avi", WheelMode.Convert, "AVI", "Old-school", Video, ".avi", Engines.Media) { Heavy = true },
        new("audio.mp3", WheelMode.Convert, "MP3", "Everywhere", Media, ".mp3", Engines.Media),
        new("audio.m4a", WheelMode.Convert, "M4A", "Apple", Media, ".m4a", Engines.Media),
        new("audio.flac", WheelMode.Convert, "FLAC", "Lossless", Media, ".flac", Engines.Media),
        new("audio.wav", WheelMode.Convert, "WAV", "Raw", Media, ".wav", Engines.Media),
        new("audio.opus", WheelMode.Convert, "OPUS", "Tiny", Media, ".opus", Engines.Media),
        new("audio.ogg", WheelMode.Convert, "OGG", "Open", Media, ".ogg", Engines.Media),

        // ---- Convert: documents
        new("doc.pdf", WheelMode.Convert, "PDF", "Print", Printable, ".pdf", Engines.Office) { Heavy = true },
        new("doc.docx", WheelMode.Convert, "DOCX", "Word", Text, ".docx", Engines.Document),
        new("doc.md", WheelMode.Convert, "MD", "Plain", Text, ".md", Engines.Document),
        new("doc.html", WheelMode.Convert, "HTML", "Web", Text, ".html", Engines.Document),
        new("doc.epub", WheelMode.Convert, "EPUB", "E-book", Text, ".epub", Engines.Document),
        new("doc.txt", WheelMode.Convert, "TXT", "Bare", Text, ".txt", Engines.Document),

        // ---- Convert: PDF to Office (Word's own reflow when installed; Swivel's rebuild otherwise)
        new("pdf.docx", WheelMode.Convert, "DOCX", "Editable", Pdf, ".docx", Engines.Pdf) { Heavy = true },
        new("pdf.pptx", WheelMode.Convert, "PPTX", "Exact", Pdf, ".pptx", Engines.Pdf),
        new("pdf.pptx-edit", WheelMode.Convert, "PPTX", "Editable", Pdf, ".pptx", Engines.Pdf),

        // ---- Convert: PDF pages to pictures
        new("pdf.png", WheelMode.Convert, "PNG", "Pages", Pdf, null, Engines.Pdf)
        {
            OutputIsFolder = true, OutputSuffix = " pages", Parameters = P(("format", "png")),
        },
        new("pdf.jpg", WheelMode.Convert, "JPG", "Pages", Pdf, null, Engines.Pdf)
        {
            OutputIsFolder = true, OutputSuffix = " pages", Parameters = P(("format", "jpg")),
        },

        // ---- Convert: archives unpack
        new("archive.extract", WheelMode.Convert, "Extract", "Unpack", Archives, null, Engines.Archive) { OutputIsFolder = true },

        // ---- Tools: images
        new("tool.compress", WheelMode.Tools, "Compress", "Lighter", Lossy, null, Engines.Image)
        {
            Parameters = P(("quality", "60")),
        },
        new("tool.rotate-right", WheelMode.Tools, "Rotate", "Right", Rotatable, null, Engines.Image)
        {
            Parameters = P(("rotate", "90")),
        },
        new("tool.rotate-left", WheelMode.Tools, "Rotate", "Left", Rotatable, null, Engines.Image)
        {
            Parameters = P(("rotate", "270")),
        },
        new("tool.strip", WheelMode.Tools, "Strip", "Private", Rotatable, null, Engines.Image)
        {
            Parameters = P(("strip", "true")),
        },
        new("tool.resize", WheelMode.Tools, "Resize", "Fit", Rotatable, null, Engines.Image) { Panel = "resize" },
        new("tool.crop", WheelMode.Tools, "Crop", "Smart", Rotatable, null, Engines.Image) { Panel = "crop" },
        new("image.combine-pdf", WheelMode.Tools, "Combine", "One PDF", RasterIn, ".pdf", Engines.Pdf)
        {
            CombinesInputs = true, MinInputs = 2, OutputSuffix = " combined",
        },
        new("media.trim", WheelMode.Tools, "Trim", "Cut", Media, null, Engines.Media) { Heavy = true, Panel = "trim", MaxInputs = 1 },

        // ---- Tools: audio and video
        new("media.compress", WheelMode.Tools, "Compress", "Lighter", Media, null, Engines.Media) { Heavy = true },
        new("video.mute", WheelMode.Tools, "Mute", "Silent", Video, null, Engines.Media) { Heavy = true },
        new("video.frames", WheelMode.Tools, "Frames", "1 per sec", Video, null, Engines.Media)
        {
            Heavy = true, OutputIsFolder = true, OutputSuffix = " frames",
        },

        // ---- Tools: PDF
        new("pdf.merge", WheelMode.Tools, "Merge", "One file", Pdf, ".pdf", Engines.Pdf)
        {
            CombinesInputs = true, MinInputs = 2, OutputSuffix = " merged",
        },
        new("pdf.organize", WheelMode.Tools, "Pages", "Organise", Pdf, ".pdf", Engines.Pdf)
        {
            CombinesInputs = true, Panel = "pages", OutputSuffix = " edited",
        },
        new("pdf.edit", WheelMode.Tools, "Edit", "Text & images", Pdf, ".pdf", Engines.Pdf)
        {
            MaxInputs = 1, CombinesInputs = true, Panel = "editor", OutputSuffix = " edited",
        },
        new("pdf.split", WheelMode.Tools, "Split", "Pages", Pdf, null, Engines.Pdf) { OutputIsFolder = true, OutputSuffix = " pages" },
        new("pdf.compress", WheelMode.Tools, "Compress", "Lighter", Pdf, null, Engines.Pdf),

        // ---- Tools: archive anything, folders included
        new("archive.zip", WheelMode.Tools, "ZIP", "Share", Anything, ".zip", Engines.Archive) { CombinesInputs = true },
        new("archive.7z", WheelMode.Tools, "7Z", "Smallest", Anything, ".7z", Engines.Archive) { CombinesInputs = true },
        new("archive.tar", WheelMode.Tools, "TAR", "Unix", Anything, ".tar", Engines.Archive) { CombinesInputs = true },
        new("archive.gz", WheelMode.Tools, "GZIP", "One file", AnyFile, ".gz", Engines.Archive) { MaxInputs = 1 },

        // ---- Actions
        new("action.copy-path", WheelMode.Actions, "Copy path", "Paste", Anything, null, Engines.Action),
        new("action.copy-file", WheelMode.Actions, "Copy file", "Clipboard", Anything, null, Engines.Action),
        new("action.move-to", WheelMode.Actions, "Move to", "Folder…", Anything, null, Engines.Transfer) { Panel = "folder", MovesInput = true },
        new("action.copy-to", WheelMode.Actions, "Copy to", "Folder…", Anything, null, Engines.Transfer) { Panel = "folder" },
        new("action.reveal", WheelMode.Actions, "Reveal", "Explorer", Anything, null, Engines.Action),
        new("action.share", WheelMode.Actions, "Share", "Nearby", AnyFile, null, Engines.Action),
        new("action.float", WheelMode.Actions, "Float", "Pin", Anything, null, Engines.Action),
        new("action.terminal", WheelMode.Actions, "Terminal", "Here", Anything, null, Engines.Action),
    ];

    private readonly IReadOnlyList<Operation> _operations;

    public OperationCatalog(IEnumerable<Operation>? extra = null) =>
        _operations = [.. BuiltIn, .. extra ?? []];

    /// <summary>The built-in table plus the user's presets and a Move/Copy wedge per pinned folder.</summary>
    public static OperationCatalog FromSettings(Settings.AppSettings settings)
    {
        var extra = new List<Operation>();
        foreach (var preset in settings.Presets)
        {
            if (BuiltIn.FirstOrDefault(o => o.Id == preset.Base) is not { } baseOp || baseOp.NeedsPanel) continue;
            extra.Add(baseOp.WithParameters(preset.Parameters) with
            {
                Id = "preset.user." + preset.Id,
                Label = preset.Label.ToUpperInvariant(),
                Hint = preset.Hint,
            });
        }
        for (int i = 0; i < settings.PinnedFolders.Count; i++)
        {
            string folder = settings.PinnedFolders[i];
            string name = Path.GetFileName(folder.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : folder;
            var target = P(("target", folder));
            extra.Add(new($"action.move-to:{i}", WheelMode.Actions, "Move", name, Anything, null, Engines.Transfer) { Parameters = target, MovesInput = true });
            extra.Add(new($"action.copy-to:{i}", WheelMode.Actions, "Copy", name, Anything, null, Engines.Transfer) { Parameters = target });
        }
        return new OperationCatalog(extra);
    }

    public Operation? Find(string id) => _operations.FirstOrDefault(o => o.Id == id);

    /// <summary>
    /// Operations valid for every input in <paramref name="mode"/>, most used first, table order after.
    /// Converting a file to its own format is never offered (combining several into one, like a PDF
    /// merge, is), and the input count must fit the operation.
    /// </summary>
    public IReadOnlyList<Operation> For(IReadOnlyCollection<FileKind> inputs, WheelMode mode, IReadOnlyDictionary<string, int>? usage = null)
    {
        if (inputs.Count == 0) return [];
        return _operations
            .Select((op, index) => (op, index))
            .Where(x => x.op.Mode == mode
                && x.op.Accepts(inputs.Count)
                && inputs.All(k => x.op.InputKinds.Contains(k) && (x.op.CombinesInputs || x.op.OutputKind != k)))
            .OrderByDescending(x => usage?.GetValueOrDefault(x.op.Id) ?? 0)
            .ThenBy(x => x.index)
            .Select(x => x.op)
            .ToArray();
    }

    /// <summary>The mode the wheel opens on: Convert when anything fits, otherwise Tools, else Actions.</summary>
    public WheelMode OpeningMode(IReadOnlyCollection<FileKind> inputs)
    {
        if (For(inputs, WheelMode.Convert).Count > 0) return WheelMode.Convert;
        if (For(inputs, WheelMode.Tools).Count > 0) return WheelMode.Tools;
        return WheelMode.Actions;
    }

    /// <summary>
    /// Splits a ranked list into rings of eight. When it overflows, the eighth wedge of every ring
    /// becomes More (represented as null), which swaps to the next ring and wraps from the last.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<Operation?>> Rings(IReadOnlyList<Operation> ranked)
    {
        if (ranked.Count <= WedgesPerRing) return [ranked.Cast<Operation?>().ToArray()];
        const int perRing = WedgesPerRing - 1;
        return ranked.Chunk(perRing)
            .Select(chunk => (IReadOnlyList<Operation?>)[.. chunk, null])
            .ToArray();
    }
}
