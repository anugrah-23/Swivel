using Swivel.Core.Catalog;
using Swivel.Core.Files;
using Swivel.Core.Jobs;

namespace Swivel.App.Feedback;

/// <summary>
/// Swivel's copy: short, cheeky, plain. Errors always say the original is safe and never joke
/// inside the reason itself.
/// </summary>
public static class Voice
{
    public static string FormatName(FileKind? kind) => kind switch
    {
        FileKind.WebP => "WebP",
        FileKind.Jpeg => "JPG",
        FileKind.Ico => "icon",
        FileKind.Opus => "Opus",
        FileKind.WebM => "WebM",
        FileKind.Markdown => "Markdown file",
        FileKind.Txt => "text file",
        FileKind.Docx => "Word doc",
        FileKind.Epub => "e-book",
        FileKind.Html => "web page",
        FileKind.SevenZip => "7z",
        FileKind.Gzip => "GZIP",
        null => "file",
        var other => other.ToString()!.ToUpperInvariant(),
    };

    private static string Plural(string name) => name + "s";

    private static string Article(string name) =>
        name.Length > 0 && ("AEIOU".Contains(char.ToUpperInvariant(name[0])) && !name.StartsWith("Uni", StringComparison.Ordinal)
            || name is "MP3" or "MP4" or "M4A" or "MKV" or "MOV" or "HTML") ? "an" : "a";

    public static string Success(JobGroupResult result)
    {
        var op = result.Operation;
        int n = result.Outputs.Count;
        switch (op.Id)
        {
            case var id when id.StartsWith("preset.", StringComparison.Ordinal):
                return n == 1 ? "Boom. Web-ready." : $"Boom. {n} web-ready images.";
            case "pdf.merge" or "image.combine-pdf": return "Boom. One tidy PDF.";
            case "pdf.split": return n == 1 ? "Boom. Split into pages." : $"Boom. {n} PDFs split.";
            case "pdf.png" or "pdf.jpg": return "Boom. Pages are pictures now.";
            case "archive.extract": return n == 1 ? "Boom. Unpacked." : $"Boom. {n} unpacked.";
            case "video.frames": return "Boom. Frames pulled.";
            case "video.mute": return n == 1 ? "Boom. Silence." : $"Boom. {n} muted.";
            case "tool.compress" or "media.compress" or "pdf.compress": return n == 1 ? "Boom. Squished." : $"Boom. Squished, all {n}.";
            case "tool.rotate-right" or "tool.rotate-left": return n == 1 ? "Boom. Turned." : $"Boom. Turned, all {n}.";
            case "tool.strip": return n == 1 ? "Boom. Scrubbed clean." : $"Boom. Scrubbed clean, all {n}.";
        }
        if (op.Engine == Operation.Engines.Archive) return $"Boom. Packed into a {FormatName(op.OutputKind)}.";

        string name = FormatName(op.OutputKind);
        return n == 1 ? $"Boom. It's {Article(name)} {name}." : $"Boom. {n} {Plural(name)}.";
    }

    public static string Action(Operation op, int count) => op.Id switch
    {
        "action.copy-path" => count == 1 ? "Path's on your clipboard." : $"{count} paths on your clipboard.",
        _ => "Done.",
    };

    public static string Failure(JobGroupResult result) => result.Failures.Count == 1
        ? $"Couldn't crack {Path.GetFileName(result.Failures[0].Input.TrimEnd('\\'))}."
        : $"Couldn't crack those {result.Failures.Count} files.";

    public static string Safe(int count) => count == 1 ? "Your original is safe." : "Your originals are safe.";

    public static string Partial(JobGroupResult result) =>
        $"{result.Outputs.Count} of {result.Inputs.Count} done. {result.Failures.Count} didn't make it.";
}
