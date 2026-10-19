using System.Runtime.InteropServices;
using Swivel.Core.Jobs;

namespace Swivel.Engines.Pdf;

/// <summary>
/// The handful of PDFium calls Swivel needs to render pages. PDFium isn't thread-safe, so every
/// call goes through one lock.
/// </summary>
internal static partial class Pdfium
{
    private const string Dll = "pdfium";
    private const int FPDF_ANNOT = 0x01, FPDF_PRINTING = 0x800;
    private const int MaxSide = 8000;

    private static readonly Lock Gate = new();
    private static bool _initialised;

    /// <summary>A rendered page as tightly packed BGRA rows.</summary>
    public sealed record Page(byte[] Bgra, int Width, int Height);

    public static int PageCount(byte[] pdf, string name)
    {
        lock (Gate)
        {
            using var doc = Open(pdf, name);
            return FPDF_GetPageCount(doc.Handle);
        }
    }

    /// <summary>Renders each page in turn; <paramref name="onPage"/> runs outside the lock with the page index.</summary>
    public static void Render(byte[] pdf, string name, double dpi, Action<Page, int, int> onPage, CancellationToken ct)
    {
        int count = PageCount(pdf, name);
        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            Page page;
            lock (Gate)
            {
                using var doc = Open(pdf, name);
                page = RenderPage(doc.Handle, i, dpi);
            }
            onPage(page, i, count);
        }
    }

    private static Page RenderPage(nint doc, int index, double dpi)
    {
        nint page = FPDF_LoadPage(doc, index);
        if (page == 0) throw new JobException($"Page {index + 1} wouldn't load.");
        try { return RenderLoaded(page, index, dpi); }
        finally { FPDF_ClosePage(page); }
    }

    private static Page RenderLoaded(nint page, int index, double dpi)
    {
        {
            double scale = dpi / 72.0;
            int width = (int)Math.Round(FPDF_GetPageWidthF(page) * scale);
            int height = (int)Math.Round(FPDF_GetPageHeightF(page) * scale);
            double shrink = Math.Min(1, (double)MaxSide / Math.Max(width, height));
            width = Math.Max(1, (int)(width * shrink));
            height = Math.Max(1, (int)(height * shrink));

            nint bitmap = FPDFBitmap_Create(width, height, 0);
            if (bitmap == 0) throw new JobException($"Page {index + 1} is too big to render.");
            try
            {
                FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
                FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, FPDF_ANNOT | FPDF_PRINTING);
                nint buffer = FPDFBitmap_GetBuffer(bitmap);
                int stride = FPDFBitmap_GetStride(bitmap);
                var pixels = new byte[width * height * 4];
                for (int y = 0; y < height; y++)
                    Marshal.Copy(buffer + y * stride, pixels, y * width * 4, width * 4);
                return new Page(pixels, width, height);
            }
            finally { FPDFBitmap_Destroy(bitmap); }
        }
    }

    // ---- Page content for PDF → Word / PowerPoint ------------------------------------------------

    /// <summary>One line of text in page points, origin top-left.</summary>
    public sealed record TextLine(string Text, double Left, double Top, double Right, double Bottom,
        double FontSize, bool Bold, bool Italic, uint Colour, string Font)
    {
        public double Width => Right - Left;
        public double Height => Bottom - Top;
    }

    /// <summary>An image placed on the page, as tightly packed BGRA, with its box in page points.</summary>
    public sealed record PlacedImage(byte[] Bgra, int PixelWidth, int PixelHeight, double Left, double Top, double Right, double Bottom);

    public sealed record PageContent(double Width, double Height, IReadOnlyList<TextLine> Lines, IReadOnlyList<PlacedImage> Images);

    /// <summary>Reads a page's text lines and images (in content order, which is usually reading order).</summary>
    public static PageContent ReadPage(byte[] pdf, string name, int index)
    {
        lock (Gate)
        {
            using var doc = Open(pdf, name);
            nint page = FPDF_LoadPage(doc.Handle, index);
            if (page == 0) throw new JobException($"Page {index + 1} wouldn't load.");
            try
            {
                double width = FPDF_GetPageWidthF(page), height = FPDF_GetPageHeightF(page);
                return new PageContent(width, height, ReadLines(page, height), ReadImages(doc.Handle, page, height));
            }
            finally { FPDF_ClosePage(page); }
        }
    }

    /// <summary>Renders a page with every text object removed: the backdrop for editable slides.</summary>
    public static Page RenderWithoutText(byte[] pdf, string name, int index, double dpi)
    {
        lock (Gate)
        {
            using var doc = Open(pdf, name);
            nint page = FPDF_LoadPage(doc.Handle, index);
            if (page == 0) throw new JobException($"Page {index + 1} wouldn't load.");
            try
            {
                for (int i = FPDFPage_CountObjects(page) - 1; i >= 0; i--)
                {
                    nint obj = FPDFPage_GetObject(page, i);
                    if (FPDFPageObj_GetType(obj) == FPDF_PAGEOBJ_TEXT && FPDFPage_RemoveObject(page, obj) != 0)
                        FPDFPageObj_Destroy(obj);
                }
                return RenderLoaded(page, index, dpi);
            }
            finally { FPDF_ClosePage(page); }
        }
    }

    /// <summary>Renders one page (with its text) at a DPI.</summary>
    public static Page RenderOne(byte[] pdf, string name, int index, double dpi)
    {
        lock (Gate)
        {
            using var doc = Open(pdf, name);
            return RenderPage(doc.Handle, index, dpi);
        }
    }

    private const int FPDF_PAGEOBJ_TEXT = 1, FPDF_PAGEOBJ_IMAGE = 3;

    private static unsafe List<TextLine> ReadLines(nint page, double pageHeight)
    {
        var lines = new List<TextLine>();
        nint text = FPDFText_LoadPage(page);
        if (text == 0) return lines;
        try
        {
            int count = FPDFText_CountChars(text);
            var builder = new System.Text.StringBuilder();
            double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
            var sizes = new List<double>();
            int boldChars = 0, visible = 0;
            bool boldByName = false;
            bool italic = false;
            uint colour = 0;
            string font = "";
            byte* fontName = stackalloc byte[256];

            void Flush()
            {
                string content = builder.ToString().Trim();
                if (content.Length > 0 && visible > 0)
                {
                    sizes.Sort();
                    lines.Add(new TextLine(content, l, t, r, b, Math.Round(sizes[sizes.Count / 2] * 2) / 2,
                        boldByName || boldChars * 2 > visible, italic, colour, font));
                }
                builder.Clear();
                sizes.Clear();
                l = t = double.MaxValue;
                r = b = double.MinValue;
                boldChars = visible = 0;
                boldByName = false;
                italic = false;
                font = "";
            }

            for (int i = 0; i < count; i++)
            {
                uint ch = FPDFText_GetUnicode(text, i);
                if (ch == '\r') continue;
                if (ch == '\n') { Flush(); continue; }
                // PDFium marks a hyphen that ends a line as 0x02 (or a soft hyphen); keep it as a real one.
                if (ch is 0x02 or 0xAD) ch = '-';
                builder.Append(ch <= 0xFFFF ? ((char)ch).ToString() : char.ConvertFromUtf32((int)ch));
                if (ch == ' ' || FPDFText_GetCharBox(text, i, out double left, out double right, out double bottom, out double top) == 0)
                    continue;

                // A big jump left or down within one "line" means a new line (or column) started.
                if (visible > 0 && (left < l - 1 || pageHeight - top > b + 2))
                {
                    char last = builder[^1];
                    builder.Length--;
                    Flush();
                    builder.Append(last);
                }

                l = Math.Min(l, left);
                r = Math.Max(r, right);
                t = Math.Min(t, pageHeight - top);
                b = Math.Max(b, pageHeight - bottom);
                sizes.Add(FPDFText_GetFontSize(text, i));
                if (FPDFText_GetFontWeight(text, i) >= 600) boldChars++;
                if (visible == 0)
                {
                    if (FPDFText_GetFillColor(text, i, out uint cr, out uint cg, out uint cb, out _) != 0)
                        colour = (cr << 16) | (cg << 8) | cb;
                    uint length = (uint)FPDFText_GetFontInfo(text, i, fontName, 256, out int flags);
                    string raw = length > 1 ? System.Text.Encoding.UTF8.GetString(fontName, (int)Math.Min(length - 1, 255)) : "";
                    font = CleanFontName(raw);
                    // Weight is often unknown (-1); the PostScript name ("Arial,Bold", "Times-Italic") still says it.
                    boldByName = raw.Contains("Bold", StringComparison.OrdinalIgnoreCase) || raw.Contains("Black", StringComparison.OrdinalIgnoreCase) || raw.Contains("Heavy", StringComparison.OrdinalIgnoreCase);
                    italic = (flags & 0x40) != 0 || raw.Contains("Italic", StringComparison.OrdinalIgnoreCase) || raw.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
                }
                visible++;
            }
            Flush();
        }
        finally { FPDFText_ClosePage(text); }
        return lines;
    }

    /// <summary>"ABCDEF+TimesNewRomanPS-BoldMT" → "Times New Roman"; unknown faces keep their family name.</summary>
    internal static string CleanFontName(string raw)
    {
        string name = raw.Contains('+') ? raw[(raw.IndexOf('+') + 1)..] : raw;
        name = name.Split('-', ',')[0];
        string lower = name.ToLowerInvariant();
        return lower switch
        {
            _ when lower.Contains("times") => "Times New Roman",
            _ when lower.Contains("arial") || lower.Contains("helvetica") => "Arial",
            _ when lower.Contains("courier") => "Courier New",
            _ when lower.Contains("calibri") => "Calibri",
            _ when lower.Contains("cambria") => "Cambria",
            _ when lower.Contains("georgia") => "Georgia",
            _ when lower.Contains("verdana") => "Verdana",
            _ when lower.Contains("segoe") => "Segoe UI",
            _ when lower.Contains("symbol") => "Symbol",
            _ => name.Replace("PSMT", "").Replace("MT", "").Trim(),
        };
    }

    private static List<PlacedImage> ReadImages(nint doc, nint page, double pageHeight)
    {
        var images = new List<PlacedImage>();
        int count = FPDFPage_CountObjects(page);
        for (int i = 0; i < count; i++)
        {
            nint obj = FPDFPage_GetObject(page, i);
            if (FPDFPageObj_GetType(obj) != FPDF_PAGEOBJ_IMAGE) continue;
            if (FPDFPageObj_GetBounds(obj, out float left, out float bottom, out float right, out float top) == 0) continue;
            if (right - left < 8 || top - bottom < 8) continue; // specks and rules, not pictures
            nint bitmap = FPDFImageObj_GetRenderedBitmap(doc, page, obj);
            if (bitmap == 0) continue;
            try
            {
                int w = FPDFBitmap_GetWidth(bitmap), h = FPDFBitmap_GetHeight(bitmap);
                if (w < 2 || h < 2) continue;
                images.Add(new PlacedImage(ToBgra(bitmap, w, h), w, h, left, pageHeight - top, right, pageHeight - bottom));
            }
            finally { FPDFBitmap_Destroy(bitmap); }
        }
        return images;
    }

    private static byte[] ToBgra(nint bitmap, int width, int height)
    {
        int format = FPDFBitmap_GetFormat(bitmap); // 1 gray, 2 BGR, 3 BGRx, 4 BGRA
        int stride = FPDFBitmap_GetStride(bitmap);
        int bytes = format switch { 1 => 1, 2 => 3, _ => 4 };
        var row = new byte[stride];
        var output = new byte[width * height * 4];
        nint buffer = FPDFBitmap_GetBuffer(bitmap);
        for (int y = 0; y < height; y++)
        {
            Marshal.Copy(buffer + y * stride, row, 0, stride);
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4, s = x * bytes;
                if (bytes == 1) { output[o] = output[o + 1] = output[o + 2] = row[s]; output[o + 3] = 255; }
                else { output[o] = row[s]; output[o + 1] = row[s + 1]; output[o + 2] = row[s + 2]; output[o + 3] = format == 4 ? row[s + 3] : (byte)255; }
            }
        }
        return output;
    }

    private sealed class Document(nint handle, GCHandle pin) : IDisposable
    {
        public nint Handle { get; } = handle;

        public void Dispose()
        {
            FPDF_CloseDocument(Handle);
            pin.Free();
        }
    }

    private static Document Open(byte[] pdf, string name)
    {
        if (!_initialised)
        {
            FPDF_InitLibrary();
            _initialised = true;
        }
        var pin = GCHandle.Alloc(pdf, GCHandleType.Pinned);
        nint doc = FPDF_LoadMemDocument64(pin.AddrOfPinnedObject(), (nuint)pdf.Length, null);
        if (doc == 0)
        {
            pin.Free();
            uint error = FPDF_GetLastError();
            throw new JobException(error == 4 // FPDF_ERR_PASSWORD
                ? $"{name} is password-protected."
                : $"Couldn't crack {name}.");
        }
        return new Document(doc, pin);
    }

    [LibraryImport(Dll)] private static partial void FPDF_InitLibrary();
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)] private static partial nint FPDF_LoadMemDocument64(nint data, nuint size, string? password);
    [LibraryImport(Dll)] private static partial uint FPDF_GetLastError();
    [LibraryImport(Dll)] private static partial int FPDF_GetPageCount(nint document);
    [LibraryImport(Dll)] private static partial nint FPDF_LoadPage(nint document, int index);
    [LibraryImport(Dll)] private static partial float FPDF_GetPageWidthF(nint page);
    [LibraryImport(Dll)] private static partial float FPDF_GetPageHeightF(nint page);
    [LibraryImport(Dll)] private static partial nint FPDFBitmap_Create(int width, int height, int alpha);
    [LibraryImport(Dll)] private static partial int FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint color);
    [LibraryImport(Dll)] private static partial void FPDF_RenderPageBitmap(nint bitmap, nint page, int x, int y, int width, int height, int rotate, int flags);
    [LibraryImport(Dll)] private static partial nint FPDFBitmap_GetBuffer(nint bitmap);
    [LibraryImport(Dll)] private static partial int FPDFBitmap_GetStride(nint bitmap);
    [LibraryImport(Dll)] private static partial void FPDFBitmap_Destroy(nint bitmap);
    [LibraryImport(Dll)] private static partial void FPDF_ClosePage(nint page);
    [LibraryImport(Dll)] private static partial void FPDF_CloseDocument(nint document);
    [LibraryImport(Dll)] private static partial int FPDFBitmap_GetWidth(nint bitmap);
    [LibraryImport(Dll)] private static partial int FPDFBitmap_GetHeight(nint bitmap);
    [LibraryImport(Dll)] private static partial int FPDFBitmap_GetFormat(nint bitmap);

    [LibraryImport(Dll)] private static partial nint FPDFText_LoadPage(nint page);
    [LibraryImport(Dll)] private static partial void FPDFText_ClosePage(nint textPage);
    [LibraryImport(Dll)] private static partial int FPDFText_CountChars(nint textPage);
    [LibraryImport(Dll)] private static partial uint FPDFText_GetUnicode(nint textPage, int index);
    [LibraryImport(Dll)] private static partial int FPDFText_GetCharBox(nint textPage, int index, out double left, out double right, out double bottom, out double top);
    [LibraryImport(Dll)] private static partial double FPDFText_GetFontSize(nint textPage, int index);
    [LibraryImport(Dll)] private static partial int FPDFText_GetFontWeight(nint textPage, int index);
    [LibraryImport(Dll)] private static partial int FPDFText_GetFillColor(nint textPage, int index, out uint r, out uint g, out uint b, out uint a);
    [LibraryImport(Dll)] private static unsafe partial nuint FPDFText_GetFontInfo(nint textPage, int index, byte* buffer, nuint length, out int flags);

    [LibraryImport(Dll)] private static partial int FPDFPage_CountObjects(nint page);
    [LibraryImport(Dll)] private static partial nint FPDFPage_GetObject(nint page, int index);
    [LibraryImport(Dll)] private static partial int FPDFPageObj_GetType(nint pageObject);
    [LibraryImport(Dll)] private static partial int FPDFPageObj_GetBounds(nint pageObject, out float left, out float bottom, out float right, out float top);
    [LibraryImport(Dll)] private static partial int FPDFPage_RemoveObject(nint page, nint pageObject);
    [LibraryImport(Dll)] private static partial void FPDFPageObj_Destroy(nint pageObject);
    [LibraryImport(Dll)] private static partial nint FPDFImageObj_GetRenderedBitmap(nint document, nint page, nint imageObject);
}
