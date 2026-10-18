using Swivel.Core.Catalog;
using Swivel.Core.Files;
using Swivel.Core.Jobs;
using Swivel.Engines.Infrastructure;

namespace Swivel.Engines.Documents;

/// <summary>DOCX, Markdown, HTML, EPUB and plain text through Pandoc, a separate GPL executable.</summary>
public sealed class DocumentEngine : IEngine
{
    public string Name => Operation.Engines.Document;

    public static string PandocPath() =>
        EnginePacks.Require(EnginePacks.Pandoc, "Document conversion needs Pandoc, a free 40 MB download.");

    public async Task RunAsync(Job job, IProgress<double> progress, CancellationToken ct)
    {
        var target = job.Operation.OutputKind ?? throw new JobException("Pick a document format to convert to.");
        await ConvertAsync(job.Inputs[0], target, job.TempPath!, ct);
        progress.Report(1);
    }

    /// <summary>Converts <paramref name="input"/> into <paramref name="output"/> as <paramref name="target"/>.</summary>
    public static async Task ConvertAsync(string input, FileKind target, string output, CancellationToken ct)
    {
        string pandoc = PandocPath();
        var source = FileSniffer.Detect(input);
        string from = source switch
        {
            FileKind.Docx => "docx",
            FileKind.Markdown or FileKind.Txt => "markdown",
            FileKind.Html => "html",
            FileKind.Epub => "epub",
            _ => throw new JobException($"Swivel can't read {Path.GetFileName(input)} as a document."),
        };
        (string to, string ext) = target switch
        {
            FileKind.Docx => ("docx", ".docx"),
            FileKind.Markdown => ("gfm", ".md"),
            FileKind.Html => ("html5", ".html"),
            FileKind.Epub => ("epub3", ".epub"),
            FileKind.Txt => ("plain", ".txt"),
            _ => throw new JobException($"Swivel can't write {target} documents."),
        };

        using var scratch = new Scratch();
        string result = scratch.File("out" + ext);
        string title = Path.GetFileNameWithoutExtension(input);
        var args = new List<string> { input, "--from", from, "--to", to, "--output", result };
        switch (target)
        {
            case FileKind.Html:
                args.AddRange(["--standalone", "--embed-resources", "--metadata", $"pagetitle={title}"]);
                break;
            case FileKind.Epub:
                args.AddRange(["--metadata", $"title={title}"]);
                break;
            case FileKind.Markdown or FileKind.Txt:
                args.Add("--wrap=none");
                break;
        }

        // Pandoc resolves relative images against the working directory: use the input's folder.
        var run = await ProcessRunner.RunAsync(pandoc, args, ct, workingDirectory: Path.GetDirectoryName(Path.GetFullPath(input)));
        if (run.ExitCode != 0 || !File.Exists(result))
            throw new JobException($"Couldn't crack {Path.GetFileName(input)}.", new InvalidOperationException(run.StandardError));
        File.Move(result, output, overwrite: true);
    }
}
