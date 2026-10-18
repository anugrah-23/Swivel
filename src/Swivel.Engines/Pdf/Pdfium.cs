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
        try
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
        finally { FPDF_ClosePage(page); }
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
}
