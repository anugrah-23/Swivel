using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Swivel.Core.Catalog;
using Swivel.Core.Files;
using Swivel.Core.Jobs;
using Swivel.Engines.Images;
using NetVips;
using Operation = Swivel.Core.Catalog.Operation;
using VipsImage = NetVips.Image;

namespace Swivel.Engines.Pdf;

/// <summary>Merge, split, compress, pages to pictures, and pictures to one PDF.</summary>
public sealed class PdfEngine : IEngine
{
    private const double RenderDpi = 150;
    private const int CompressMaxSide = 2000;
    private const int CompressQuality = 60;

    public string Name => Operation.Engines.Pdf;

    /// <summary>Raised when Word couldn't reflow a PDF and the built-in rebuild ran instead (for the log).</summary>
    public static event Action<string, Exception>? WordFallback;

    public async Task RunAsync(Job job, IProgress<double> progress, CancellationToken ct)
    {
        if (job.Operation.Id == "pdf.docx")
        {
            await ToDocxAsync(job, progress, ct);
            return;
        }
        await RunSync(job, progress, ct);
    }

    /// <summary>Microsoft Word's own PDF reflow when it's installed; Swivel's basic rebuild otherwise.</summary>
    private static async Task ToDocxAsync(Job job, IProgress<double> progress, CancellationToken ct)
    {
        string input = job.Inputs[0];
        if (job.Operation.Parameter("engine", "auto") != "basic" && Documents.OfficeEngine.HasWord)
        {
            try
            {
                progress.Report(0.1);
                await Documents.OfficeEngine.ReflowPdfWithWordAsync(Path.GetFullPath(input), job.TempPath!, ct);
                progress.Report(1);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                WordFallback?.Invoke(input, ex);
            }
        }
        await Task.Run(() =>
        {
            _ = ImageEngine.Configured.Value;
            PdfToOffice.ToDocx(input, job.TempPath!, progress, ct);
            progress.Report(1);
        }, ct);
    }

    private static Task RunSync(Job job, IProgress<double> progress, CancellationToken ct) =>
        Task.Run(() =>
        {
            _ = ImageEngine.Configured.Value;
            try
            {
                switch (job.Operation.Id)
                {
                    case "pdf.merge": Merge(NaturalOrder(job.Inputs), job.TempPath!, progress, ct); break;
                    case "pdf.split": Split(job.Inputs[0], job.TempPath!, progress, ct); break;
                    case "pdf.compress": Compress(job.Inputs[0], job.TempPath!, progress, ct); break;
                    case "pdf.png" or "pdf.jpg": ToImages(job.Inputs[0], job.TempPath!, job.Operation.Parameter("format", "png"), progress, ct); break;
                    case "image.combine-pdf": Combine(NaturalOrder(job.Inputs), job.TempPath!, progress, ct); break;
                    case "pdf.organize": Organize(job, progress, ct); break;
                    case "pdf.pptx": PdfToOffice.ToPptx(job.Inputs[0], job.TempPath!, editable: false, progress, ct); break;
                    case "pdf.pptx-edit": PdfToOffice.ToPptx(job.Inputs[0], job.TempPath!, editable: true, progress, ct); break;
                    default: throw new JobException($"{job.Operation.Label} isn't built yet.");
                }
            }
            catch (PdfSharp.PdfSharpException ex)
            {
                throw new JobException($"Couldn't crack {Path.GetFileName(job.Inputs[0])}.", ex);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
            {
                throw new JobException($"{Path.GetFileName(job.Inputs[0])} is password-protected.", ex);
            }
            progress.Report(1);
        }, ct);

    /// <summary>Merges in name order ("2.pdf" before "10.pdf"), which is what people expect from a selection.</summary>
    private static IReadOnlyList<string> NaturalOrder(IReadOnlyList<string> paths) =>
        paths.Order(Comparer<string>.Create((a, b) => NaturalCompare(Path.GetFileName(a), Path.GetFileName(b)))).ToArray();

    private static void Merge(IReadOnlyList<string> inputs, string output, IProgress<double> progress, CancellationToken ct)
    {
        using var merged = new PdfDocument();
        for (int i = 0; i < inputs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var source = Open(inputs[i], PdfDocumentOpenMode.Import);
            foreach (PdfPage page in source.Pages) merged.AddPage(page);
            progress.Report((i + 1.0) / inputs.Count * 0.9);
        }
        merged.Save(output);
    }

    /// <summary>
    /// Builds a new PDF from the organiser's page plan: any pages of the dropped PDFs (plus any added
    /// in the organiser), in any order, each with its own extra rotation. Pages left out are dropped.
    /// </summary>
    private static void Organize(Job job, IProgress<double> progress, CancellationToken ct)
    {
        var sources = job.Inputs.Concat(job.Operation.Parameter("sources", "")
            .Split('|', StringSplitOptions.RemoveEmptyEntries)).ToArray();
        IReadOnlyList<PlannedPage> plan;
        try { plan = PagePlan.Parse(job.Operation.Parameter("plan", "")); }
        catch (FormatException ex) { throw new JobException("The page plan didn't make sense.", ex); }
        if (plan.Count == 0) throw new JobException("There are no pages left to save.");

        var opened = new Dictionary<int, PdfDocument>();
        try
        {
            using var output = new PdfDocument();
            for (int i = 0; i < plan.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var item = plan[i];
                if (item.Source >= sources.Length) throw new JobException("The page plan refers to a missing file.");
                if (!opened.TryGetValue(item.Source, out var source))
                    opened[item.Source] = source = Open(sources[item.Source], PdfDocumentOpenMode.Import);
                if (item.Page >= source.PageCount)
                    throw new JobException($"{Path.GetFileName(sources[item.Source])} has no page {item.Page + 1}.");

                var page = output.AddPage(source.Pages[item.Page]);
                page.Rotate = PagePlan.Normalise(page.Rotate + item.Rotation);
                progress.Report((i + 1.0) / plan.Count * 0.9);
            }
            output.Save(job.TempPath!);
        }
        finally
        {
            foreach (var document in opened.Values) document.Dispose();
        }
    }

    private static void Split(string input, string folder, IProgress<double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        using var source = Open(input, PdfDocumentOpenMode.Import);
        int count = source.PageCount;
        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var single = new PdfDocument();
            single.AddPage(source.Pages[i]);
            single.Save(Path.Combine(folder, PageName(i, count, ".pdf")));
            progress.Report((i + 1.0) / count);
        }
    }

    private static void ToImages(string input, string folder, string format, IProgress<double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        byte[] pdf = File.ReadAllBytes(input);
        Pdfium.Render(pdf, Path.GetFileName(input), RenderDpi, (page, index, count) =>
        {
            using var bgra = VipsImage.NewFromMemory(page.Bgra, page.Width, page.Height, 4, Enums.BandFormat.Uchar);
            using var b = bgra[0];
            using var g = bgra[1];
            using var r = bgra[2];
            using var rgb = r.Bandjoin(g, b);
            using var srgb = rgb.Copy(interpretation: Enums.Interpretation.Srgb);
            string path = Path.Combine(folder, PageName(index, count, "." + format));
            if (format == "jpg") srgb.Jpegsave(path, q: 88, optimizeCoding: true);
            else srgb.Pngsave(path, compression: 6);
            progress.Report((index + 1.0) / count);
        }, ct);
    }

    private static void Combine(IReadOnlyList<string> images, string output, IProgress<double> progress, CancellationToken ct)
    {
        using var document = new PdfDocument();
        for (int i = 0; i < images.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var loaded = ImageEngine.Load(images[i], FileSniffer.Detect(images[i]));
            using var upright = loaded.Autorot();
            using var image = ImageEngine.ToEightBitSrgb(upright);
            ImageWriters.AddPage(document, image);
            progress.Report((i + 1.0) / images.Count * 0.9);
        }
        document.Save(output);
    }

    /// <summary>
    /// Re-encodes the big JPEG images inside a PDF at a lower quality and at most 2000 px, and
    /// compresses content streams. Pages, text and vector art are left exactly as they were.
    /// </summary>
    private static void Compress(string input, string output, IProgress<double> progress, CancellationToken ct)
    {
        using var document = Open(input, PdfDocumentOpenMode.Modify);
        document.Options.CompressContentStreams = true;
        document.Options.NoCompression = false;
        document.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;

        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        int pageIndex = 0;
        foreach (PdfPage page in document.Pages)
        {
            ct.ThrowIfCancellationRequested();
            var xobjects = page.Elements.GetDictionary("/Resources")?.Elements.GetDictionary("/XObject");
            if (xobjects != null)
            {
                foreach (var item in xobjects.Elements.Values)
                {
                    if ((item as PdfReference)?.Value is PdfDictionary image && seen.Add(image)
                        && image.Elements.GetName("/Subtype") == "/Image")
                        ShrinkJpeg(image);
                }
            }
            progress.Report(++pageIndex / (double)document.PageCount * 0.9);
        }
        document.Save(output);
    }

    private static void ShrinkJpeg(PdfDictionary image)
    {
        var filter = image.Elements["/Filter"];
        bool isJpeg = filter is PdfName { Value: "/DCTDecode" }
            || filter is PdfArray { Elements.Count: 1 } array && array.Elements[0] is PdfName { Value: "/DCTDecode" };
        string colour = image.Elements.GetName("/ColorSpace");
        if (!isJpeg || colour is not ("/DeviceRGB" or "/DeviceGray") || image.Stream?.Value is not { Length: > 50_000 } original)
            return;

        try
        {
            using var decoded = VipsImage.NewFromBuffer(original);
            int longest = Math.Max(decoded.Width, decoded.Height);
            using var sized = longest > CompressMaxSide
                ? decoded.ThumbnailImage(CompressMaxSide, height: CompressMaxSide, size: Enums.Size.Down)
                : decoded.Copy();
            byte[] smaller = sized.JpegsaveBuffer(q: CompressQuality, optimizeCoding: true, keep: Enums.ForeignKeep.None);
            if (smaller.Length >= original.Length * 0.9) return;

            image.Stream.Value = smaller;
            image.Elements.SetInteger("/Width", sized.Width);
            image.Elements.SetInteger("/Height", sized.Height);
            image.Elements.SetInteger("/BitsPerComponent", 8);
            image.Elements.Remove("/DecodeParms");
        }
        catch (VipsException)
        {
            // An unusual JPEG variant: leave this image as it was.
        }
    }

    private static PdfDocument Open(string path, PdfDocumentOpenMode mode)
    {
        try
        {
            return PdfReader.Open(path, mode);
        }
        catch (PdfReaderException ex)
        {
            throw new JobException(ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase)
                ? $"{Path.GetFileName(path)} is password-protected."
                : $"Couldn't crack {Path.GetFileName(path)}.", ex);
        }
    }

    private static string PageName(int index, int count, string extension) =>
        $"Page {(index + 1).ToString().PadLeft(Math.Max(2, count.ToString().Length), '0')}{extension}";

    private static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                string x = a[si..i].TrimStart('0'), y = b[sj..j].TrimStart('0');
                int cmp = x.Length != y.Length ? x.Length.CompareTo(y.Length) : string.CompareOrdinal(x, y);
                if (cmp != 0) return cmp;
            }
            else
            {
                int cmp = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                if (cmp != 0) return cmp;
                i++;
                j++;
            }
        }
        return a.Length.CompareTo(b.Length);
    }
}
