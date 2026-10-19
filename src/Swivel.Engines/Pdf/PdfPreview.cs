namespace Swivel.Engines.Pdf;

/// <summary>Read-only page previews for the app's windows (the page organiser and, later, the editor).</summary>
public static class PdfPreview
{
    /// <summary>A rendered page as tightly packed BGRx rows (alpha unused).</summary>
    public sealed record Thumbnail(byte[] Bgrx, int Width, int Height);

    public static int PageCount(string path) => Pdfium.PageCount(File.ReadAllBytes(path), Path.GetFileName(path));

    /// <summary>Renders every page to fit <paramref name="maxSide"/> pixels, one callback per page as it's ready.</summary>
    public static void Thumbnails(string path, int maxSide, Action<int, Thumbnail> ready, CancellationToken ct)
    {
        byte[] pdf = File.ReadAllBytes(path);
        string name = Path.GetFileName(path);
        var sizes = Pdfium.PageSizes(pdf, name);
        for (int i = 0; i < sizes.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (width, height) = sizes[i];
            double dpi = maxSide / Math.Max(width, height) * 72;
            var page = Pdfium.RenderOne(pdf, name, i, dpi);
            ready(i, new Thumbnail(page.Bgra, page.Width, page.Height));
        }
    }
}
