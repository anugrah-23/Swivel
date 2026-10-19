using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NetVips;
using Swivel.Core.Files;
using Swivel.Core.Jobs;
using Swivel.Engines.Images;
using VipsImage = NetVips.Image;

namespace Swivel.Engines.Pdf;

/// <summary>A box on a page in points, origin top-left (the way the editor draws).</summary>
public readonly record struct PageBox(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
}

/// <summary>Something editable on a page: a line of text or an image.</summary>
public sealed record PageItem(string Id, bool IsText, string Text, PageBox Box, double FontSize, bool Bold, bool Italic, uint Colour, string Font);

/// <summary>One change. Ids refer to <see cref="PdfEditSession.Items"/> at the moment the edit is applied.</summary>
public abstract record PdfEdit(int Page);
public sealed record EditText(int Page, string Id, string Text) : PdfEdit(Page);
public sealed record MoveItem(int Page, string Id, PageBox To) : PdfEdit(Page);
public sealed record DeleteItem(int Page, string Id) : PdfEdit(Page);
public sealed record AddText(int Page, double Left, double Top, string Text, double FontSize, bool Bold) : PdfEdit(Page);
/// <summary>Adds a picture, keeping its own shape and centred inside <paramref name="At"/>.</summary>
public sealed record AddImage(int Page, string ImagePath, PageBox At) : PdfEdit(Page);
public sealed record ReplaceImage(int Page, string Id, string ImagePath) : PdfEdit(Page);

/// <summary>
/// Edits a PDF's real content through PDFium: text objects are rewritten (not covered), images moved,
/// resized, replaced, removed or added. Every edit is kept as a step on top of the original file, so
/// Undo rebuilds from the original and replays the remaining steps, which is exact. The original
/// file is never written; <see cref="Save"/> writes a new one.
/// </summary>
public sealed unsafe partial class PdfEditSession : IDisposable
{
    private const int FPDF_NO_INCREMENTAL = 2, FPDF_FONT_TRUETYPE = 2;

    private readonly byte[] _original;
    private readonly string _name;
    private readonly List<PdfEdit> _done = [];
    private readonly List<PdfEdit> _undone = [];
    private readonly Dictionary<int, nint> _pages = [];
    private readonly HashSet<int> _changed = [];
    private readonly Dictionary<int, Dictionary<string, int[]>> _itemObjects = [];
    private readonly Dictionary<string, nint> _fonts = [];
    private Dictionary<nint, HashSet<int>>? _coverage;
    private GCHandle _pin;
    private nint _doc;

    private PdfEditSession(string path)
    {
        _original = File.ReadAllBytes(path);
        _name = Path.GetFileName(path);
        lock (Pdfium.Gate) OpenDocument();
    }

    public static PdfEditSession Open(string path) => new(path);

    public int PageCount { get { lock (Pdfium.Gate) return Pdfium.FPDF_GetPageCount(_doc); } }
    public bool CanUndo => _done.Count > 0;
    public bool CanRedo => _undone.Count > 0;
    public bool HasChanges => _done.Count > 0;

    public (double Width, double Height) PageSize(int page)
    {
        lock (Pdfium.Gate)
        {
            nint p = Page(page);
            return (Pdfium.FPDF_GetPageWidthF(p), Pdfium.FPDF_GetPageHeightF(p));
        }
    }

    /// <summary>Renders the page as it currently stands, edits included.</summary>
    public PdfPreview.Thumbnail Render(int page, double dpi)
    {
        lock (Pdfium.Gate)
        {
            var rendered = Pdfium.RenderLoaded(Page(page), page, dpi);
            return new PdfPreview.Thumbnail(rendered.Bgra, rendered.Width, rendered.Height);
        }
    }

    // ---- Items --------------------------------------------------------------------------------------

    /// <summary>Lines of text (neighbouring text objects on one baseline in one font) and images.</summary>
    public IReadOnlyList<PageItem> Items(int page)
    {
        lock (Pdfium.Gate)
        {
            nint p = Page(page);
            double height = Pdfium.FPDF_GetPageHeightF(p);
            var items = new List<PageItem>();
            var map = new Dictionary<string, int[]>();
            nint textPage = Pdfium.FPDFText_LoadPage(p);
            try
            {
                var run = new List<(int Index, string Text, PageBox Box, float Size, nint Font)>();
                void FlushRun()
                {
                    if (run.Count == 0) return;
                    string id = $"t{map.Count(k => k.Key[0] == 't')}";
                    var text = new System.Text.StringBuilder(run[0].Text);
                    for (int k = 1; k < run.Count; k++)
                    {
                        if (run[k].Box.Left - run[k - 1].Box.Right > run[k].Size * 0.15 && !text.ToString().EndsWith(' ')) text.Append(' ');
                        text.Append(run[k].Text);
                    }
                    var box = new PageBox(run.Min(r => r.Box.Left), run.Min(r => r.Box.Top), run.Max(r => r.Box.Right), run.Max(r => r.Box.Bottom));
                    var (font, bold, italic) = Describe(run[0].Font);
                    uint colour = FillColour(Pdfium.FPDFPage_GetObject(p, run[0].Index));
                    items.Add(new PageItem(id, true, text.ToString().Trim(), box, run[0].Size, bold, italic, colour, font));
                    map[id] = run.Select(r => r.Index).ToArray();
                    run.Clear();
                }

                int count = Pdfium.FPDFPage_CountObjects(p);
                for (int i = 0; i < count; i++)
                {
                    nint obj = Pdfium.FPDFPage_GetObject(p, i);
                    int type = Pdfium.FPDFPageObj_GetType(obj);
                    if (type == Pdfium.FPDF_PAGEOBJ_IMAGE)
                    {
                        FlushRun();
                        string id = $"i{i}";
                        items.Add(new PageItem(id, false, "", Bounds(obj, height), 0, false, false, 0, ""));
                        map[id] = [i];
                        continue;
                    }
                    if (type != Pdfium.FPDF_PAGEOBJ_TEXT) continue;
                    string content = ObjectText(obj, textPage);
                    if (content.Trim().Length == 0) continue;
                    var b = Bounds(obj, height);
                    FPDFTextObj_GetFontSize(obj, out float size);
                    nint font = FPDFTextObj_GetFont(obj);
                    if (run.Count > 0)
                    {
                        var last = run[^1];
                        bool sameLine = last.Font == font && Math.Abs(last.Size - size) < 0.5
                            && Math.Abs(last.Box.Bottom - b.Bottom) < size * 0.35
                            && b.Left - last.Box.Right > -size * 0.5 && b.Left - last.Box.Right < size * 1.2;
                        if (!sameLine) FlushRun();
                    }
                    run.Add((i, content, b, size, font));
                }
                FlushRun();
            }
            finally { Pdfium.FPDFText_ClosePage(textPage); }
            _itemObjects[page] = map;
            return items;
        }
    }

    // ---- Edits --------------------------------------------------------------------------------------

    /// <summary>Applies an edit. Returns a note for the user when a line had to change font, else null.</summary>
    public string? Apply(PdfEdit edit)
    {
        string? note;
        lock (Pdfium.Gate) note = Execute(edit);
        _done.Add(edit);
        _undone.Clear();
        return note;
    }

    /// <summary>Takes back the last edit. Returns the page it was on, or -1 if there was nothing to undo.</summary>
    public int Undo()
    {
        if (_done.Count == 0) return -1;
        var edit = _done[^1];
        _undone.Add(edit);
        _done.RemoveAt(_done.Count - 1);
        Rebuild();
        return edit.Page;
    }

    /// <summary>Puts back the last undone edit. Returns the page it was on, or -1 if there was nothing to redo.</summary>
    public int Redo()
    {
        if (_undone.Count == 0) return -1;
        var edit = _undone[^1];
        _undone.RemoveAt(_undone.Count - 1);
        lock (Pdfium.Gate) Execute(edit);
        _done.Add(edit);
        return edit.Page;
    }

    private void Rebuild()
    {
        lock (Pdfium.Gate)
        {
            CloseDocument();
            OpenDocument();
            foreach (var edit in _done) Execute(edit);
        }
    }

    private string? Execute(PdfEdit edit)
    {
        nint page = Page(edit.Page);
        _changed.Add(edit.Page);
        double height = Pdfium.FPDF_GetPageHeightF(page);
        string? note = null;
        switch (edit)
        {
            case EditText e: note = ReplaceText(page, Objects(e.Page, e.Id), e.Text); break;
            case MoveItem e: Move(page, Objects(e.Page, e.Id), e.To, height); break;
            case DeleteItem e: Remove(page, Objects(e.Page, e.Id)); break;
            case AddText e: note = InsertText(page, e, height); break;
            case AddImage e: InsertImage(page, e.ImagePath, e.At, height, index: null, fitInside: true); break;
            case ReplaceImage e: Replace(page, Objects(e.Page, e.Id), e.ImagePath, height); break;
        }
        _itemObjects.Remove(edit.Page); // object indices changed; recomputed on next Items()
        return note;
    }

    private int[] Objects(int page, string id)
    {
        if (!_itemObjects.TryGetValue(page, out var map)) { Items(page); map = _itemObjects[page]; }
        return map.TryGetValue(id, out var objects) ? objects : throw new JobException("That item isn't on the page any more.");
    }

    private string? ReplaceText(nint page, int[] objects, string text)
    {
        if (text.Trim().Length == 0)
        {
            Remove(page, objects);
            return null;
        }
        nint first = Pdfium.FPDFPage_GetObject(page, objects[0]);
        nint font = FPDFTextObj_GetFont(first);
        FPDFTextObj_GetFontSize(first, out float size);
        FPDFPageObj_GetMatrix(first, out var matrix);
        uint colour = FillColour(first);
        bool covered = Covers(font, text);

        if (objects.Length == 1 && covered)
        {
            fixed (char* chars = text) FPDFText_SetText(first, chars);
            return null;
        }

        var (name, bold, italic) = Describe(font);
        string usedName = name;
        nint useFont = covered ? font : Substitute(name, FPDFFont_GetFlags(font), bold, italic, text, out usedName);
        nint replacement = FPDFPageObj_CreateTextObj(_doc, useFont, size);
        fixed (char* chars = text) FPDFText_SetText(replacement, chars);
        FPDFPageObj_SetMatrix(replacement, in matrix);
        FPDFPageObj_SetFillColor(replacement, (colour >> 16) & 0xFF, (colour >> 8) & 0xFF, colour & 0xFF, 255);
        int at = objects[0];
        Remove(page, objects);
        FPDFPage_InsertObjectAtIndex(page, replacement, (nuint)at);
        _coverage = null; // the document's fonts changed
        return covered ? null : $"That line now uses {usedName}: the PDF's own font didn't include every letter you typed.";
    }

    private static void Move(nint page, int[] objects, PageBox to, double height)
    {
        var old = UnionBounds(page, objects, height);
        double sx = old.Width > 0.01 ? to.Width / old.Width : 1, sy = old.Height > 0.01 ? to.Height / old.Height : 1;
        // PDF space is bottom-up: map the old box onto the new one.
        double oldBottom = height - old.Bottom, newBottom = height - to.Bottom;
        double tx = to.Left - old.Left * sx, ty = newBottom - oldBottom * sy;
        foreach (int index in objects)
            FPDFPageObj_Transform(Pdfium.FPDFPage_GetObject(page, index), sx, 0, 0, sy, tx, ty);
    }

    private static void Remove(nint page, int[] objects)
    {
        foreach (int index in objects.OrderDescending())
        {
            nint obj = Pdfium.FPDFPage_GetObject(page, index);
            if (Pdfium.FPDFPage_RemoveObject(page, obj) != 0) Pdfium.FPDFPageObj_Destroy(obj);
        }
    }

    private string? InsertText(nint page, AddText add, double height)
    {
        nint font = Substitute(add.Bold ? "Helvetica-Bold" : "Helvetica", 0, add.Bold, false, add.Text, out _);
        nint obj = FPDFPageObj_CreateTextObj(_doc, font, (float)add.FontSize);
        fixed (char* chars = add.Text) FPDFText_SetText(obj, chars);
        var matrix = new Matrix { a = 1, d = 1, e = (float)add.Left, f = (float)(height - add.Top - add.FontSize * 0.8) };
        FPDFPageObj_SetMatrix(obj, in matrix);
        FPDFPageObj_SetFillColor(obj, 0, 0, 0, 255);
        FPDFPage_InsertObject(page, obj);
        return null;
    }

    private void Replace(nint page, int[] objects, string imagePath, double height)
    {
        var old = UnionBounds(page, objects, height);
        int at = objects[0];
        Remove(page, objects);
        InsertImage(page, imagePath, old, height, at, fitInside: true);
    }

    /// <summary>
    /// Adds a picture. Opaque pictures are embedded as JPEG (small); ones with transparency as a bitmap
    /// with its alpha. With <paramref name="fitInside"/>, it keeps its shape inside the box, centred.
    /// </summary>
    private void InsertImage(nint page, string path, PageBox box, double height, int? index, bool fitInside = false)
    {
        _ = ImageEngine.Configured.Value;
        VipsImage loaded;
        try { loaded = ImageEngine.Load(path, FileSniffer.Detect(path)); }
        catch (Exception ex) when (ex is not JobException) { throw new JobException($"Couldn't read {Path.GetFileName(path)} as a picture.", ex); }
        using var source = loaded;
        using var upright = source.Autorot();
        using var srgb = ImageEngine.ToEightBitSrgb(upright);
        using var image = Math.Max(srgb.Width, srgb.Height) > 2400 ? srgb.ThumbnailImage(2400, height: 2400, size: Enums.Size.Down) : srgb.Copy();

        var target = box;
        if (fitInside)
        {
            double scale = Math.Min(box.Width / image.Width, box.Height / image.Height);
            double w = image.Width * scale, h = image.Height * scale;
            double left = box.Left + (box.Width - w) / 2, top = box.Top + (box.Height - h) / 2;
            target = new PageBox(left, top, left + w, top + h);
        }

        nint obj = FPDFPageObj_NewImageObj(_doc);
        nint* pages = stackalloc nint[1];
        pages[0] = page;
        bool ok;
        if (image.HasAlpha())
        {
            using var rgba = image.Bands == 4 ? image.Copy() : image.Colourspace(Enums.Interpretation.Srgb);
            using var r = rgba[0]; using var g = rgba[1]; using var b = rgba[2]; using var a = rgba[3];
            using var bgra = b.Bandjoin(g, r, a);
            byte[] pixels = bgra.WriteToMemory<byte>();
            fixed (byte* data = pixels)
            {
                nint bitmap = FPDFBitmap_CreateEx(bgra.Width, bgra.Height, 4, data, bgra.Width * 4);
                ok = FPDFImageObj_SetBitmap(pages, 1, obj, bitmap) != 0;
                Pdfium.FPDFBitmap_Destroy(bitmap);
            }
        }
        else
        {
            using var flat = ImageWriters.Opaque(image);
            byte[] jpeg = flat.JpegsaveBuffer(q: 90, optimizeCoding: true);
            var handle = GCHandle.Alloc(jpeg);
            try
            {
                var access = new FileAccess { Length = (uint)jpeg.Length, GetBlock = &ReadBlock, Param = GCHandle.ToIntPtr(handle) };
                ok = FPDFImageObj_LoadJpegFileInline(pages, 1, obj, &access) != 0;
            }
            finally { handle.Free(); }
        }
        if (!ok)
        {
            Pdfium.FPDFPageObj_Destroy(obj);
            throw new JobException($"Couldn't place {Path.GetFileName(path)} on the page.");
        }
        FPDFImageObj_SetMatrix(obj, target.Width, 0, 0, target.Height, target.Left, height - target.Bottom);
        if (index is int at) FPDFPage_InsertObjectAtIndex(page, obj, (nuint)at);
        else FPDFPage_InsertObject(page, obj);
    }

    // ---- Fonts --------------------------------------------------------------------------------------

    /// <summary>
    /// True when every letter of <paramref name="text"/> already appears somewhere in the document in
    /// that font. Embedded fonts are usually subsets, so this is the safe test for "will it render".
    /// </summary>
    private bool Covers(nint font, string text)
    {
        if (_coverage is null)
        {
            _coverage = [];
            int pages = Pdfium.FPDF_GetPageCount(_doc);
            for (int p = 0; p < pages; p++)
            {
                nint page = Page(p);
                nint textPage = Pdfium.FPDFText_LoadPage(page);
                try
                {
                    int count = Pdfium.FPDFPage_CountObjects(page);
                    for (int i = 0; i < count; i++)
                    {
                        nint obj = Pdfium.FPDFPage_GetObject(page, i);
                        if (Pdfium.FPDFPageObj_GetType(obj) != Pdfium.FPDF_PAGEOBJ_TEXT) continue;
                        nint f = FPDFTextObj_GetFont(obj);
                        if (!_coverage.TryGetValue(f, out var set)) _coverage[f] = set = [];
                        foreach (char c in ObjectText(obj, textPage)) set.Add(c);
                    }
                }
                finally { Pdfium.FPDFText_ClosePage(textPage); }
            }
        }
        return _coverage.TryGetValue(font, out var chars) && text.All(c => c == ' ' || chars.Contains(c));
    }

    /// <summary>
    /// The nearest standard PDF font (Helvetica, Times or Courier, with weight and slant), or Arial from
    /// Windows, embedded, when the text needs letters beyond Western European.
    /// </summary>
    private nint Substitute(string name, int flags, bool bold, bool italic, string text, out string usedName)
    {
        bool needsUnicode = text.Any(c => c > 0xFF && "€‘’“”–—•…™ŠšŽžŒœŸ".IndexOf(c) < 0);
        if (needsUnicode)
        {
            string file = bold ? "arialbd.ttf" : "arial.ttf";
            usedName = "Arial";
            return Font("ttf:" + file, () =>
            {
                byte[] data = File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), file));
                fixed (byte* bytes = data) return FPDFText_LoadFont(_doc, bytes, (uint)data.Length, FPDF_FONT_TRUETYPE, 1); // PDFium copies the bytes
            });
        }

        string lower = name.ToLowerInvariant();
        bool mono = (flags & 1) != 0 || lower.Contains("courier") || lower.Contains("mono") || lower.Contains("consol");
        bool serif = !mono && ((flags & 2) != 0 || lower.Contains("times") || lower.Contains("georgia") || lower.Contains("garamond")
            || lower.Contains("cambria") || lower.Contains("palatino") || lower.Contains("book") || (lower.Contains("serif") && !lower.Contains("sans")));
        string family = mono ? "Courier" : serif ? "Times" : "Helvetica";
        string standard = family switch
        {
            "Times" => bold && italic ? "Times-BoldItalic" : bold ? "Times-Bold" : italic ? "Times-Italic" : "Times-Roman",
            _ => bold && italic ? $"{family}-BoldOblique" : bold ? $"{family}-Bold" : italic ? $"{family}-Oblique" : family,
        };
        usedName = family;
        return Font("std:" + standard, () => FPDFText_LoadStandardFont(_doc, standard));
    }

    private nint Font(string key, Func<nint> load)
    {
        if (_fonts.TryGetValue(key, out nint font)) return font;
        font = load();
        if (font == 0) throw new JobException("Couldn't load a font for that text.");
        return _fonts[key] = font;
    }

    private static (string Name, bool Bold, bool Italic) Describe(nint font)
    {
        byte* buffer = stackalloc byte[256];
        nuint length = FPDFFont_GetBaseFontName(font, buffer, 256);
        string raw = length > 1 ? System.Text.Encoding.UTF8.GetString(buffer, (int)Math.Min((long)length - 1, 255)) : "";
        int flags = FPDFFont_GetFlags(font);
        bool bold = raw.Contains("Bold", StringComparison.OrdinalIgnoreCase) || raw.Contains("Black", StringComparison.OrdinalIgnoreCase);
        bool italic = (flags & 0x40) != 0 || raw.Contains("Italic", StringComparison.OrdinalIgnoreCase) || raw.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
        return (Pdfium.CleanFontName(raw), bold, italic);
    }

    // ---- Saving -------------------------------------------------------------------------------------

    /// <summary>Writes the edited document to a new file.</summary>
    public void Save(string path)
    {
        lock (Pdfium.Gate)
        {
            // Text set in a newly embedded Unicode font only serialises correctly after its page's text is laid out.
            foreach (int page in _changed)
            {
                if (!_pages.TryGetValue(page, out nint handle)) continue;
                Pdfium.FPDFText_ClosePage(Pdfium.FPDFText_LoadPage(handle));
                FPDFPage_GenerateContent(handle);
            }
            using var output = File.Create(path);
            var handleToStream = GCHandle.Alloc(output);
            try
            {
                var writer = new FileWrite { Version = 1, WriteBlock = &WriteBlock, Stream = GCHandle.ToIntPtr(handleToStream) };
                if (FPDF_SaveAsCopy(_doc, &writer, FPDF_NO_INCREMENTAL) == 0)
                    throw new JobException("Couldn't save the edited PDF.");
            }
            finally { handleToStream.Free(); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileWrite
    {
        public int Version;
        public delegate* unmanaged<FileWrite*, void*, uint, int> WriteBlock;
        public nint Stream; // ours: PDFium passes the struct back, so the stream travels with it
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAccess
    {
        public uint Length;
        public delegate* unmanaged<nint, uint, byte*, uint, int> GetBlock;
        public nint Param;
    }

    [UnmanagedCallersOnly]
    private static int WriteBlock(FileWrite* self, void* data, uint size)
    {
        try
        {
            var stream = (Stream)GCHandle.FromIntPtr(self->Stream).Target!;
            stream.Write(new ReadOnlySpan<byte>(data, (int)size));
            return 1;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly]
    private static int ReadBlock(nint param, uint position, byte* buffer, uint size)
    {
        try
        {
            var data = (byte[])GCHandle.FromIntPtr(param).Target!;
            if (position + size > data.Length) return 0;
            new ReadOnlySpan<byte>(data, (int)position, (int)size).CopyTo(new Span<byte>(buffer, (int)size));
            return 1;
        }
        catch { return 0; }
    }

    // ---- Plumbing -----------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct Matrix { public float a, b, c, d, e, f; }

    private void OpenDocument()
    {
        Pdfium.EnsureInitialised();
        _pin = GCHandle.Alloc(_original, GCHandleType.Pinned);
        _doc = Pdfium.FPDF_LoadMemDocument64(_pin.AddrOfPinnedObject(), (nuint)_original.Length, null);
        if (_doc == 0)
        {
            _pin.Free();
            throw new JobException(Pdfium.FPDF_GetLastError() == 4 ? $"{_name} is password-protected." : $"Couldn't crack {_name}.");
        }
    }

    private void CloseDocument()
    {
        foreach (nint page in _pages.Values) Pdfium.FPDF_ClosePage(page);
        _pages.Clear();
        _changed.Clear();
        _itemObjects.Clear();
        foreach (nint font in _fonts.Values) FPDFFont_Close(font); // loaded fonts are ours to release, before their document
        _fonts.Clear();
        _coverage = null;
        if (_doc != 0) Pdfium.FPDF_CloseDocument(_doc);
        _doc = 0;
        if (_pin.IsAllocated) _pin.Free();
    }

    private nint Page(int index)
    {
        if (_pages.TryGetValue(index, out nint page)) return page;
        page = Pdfium.FPDF_LoadPage(_doc, index);
        if (page == 0) throw new JobException($"Page {index + 1} wouldn't load.");
        return _pages[index] = page;
    }

    private static PageBox Bounds(nint obj, double pageHeight)
    {
        Pdfium.FPDFPageObj_GetBounds(obj, out float left, out float bottom, out float right, out float top);
        return new PageBox(left, pageHeight - top, right, pageHeight - bottom);
    }

    private static PageBox UnionBounds(nint page, int[] objects, double height)
    {
        var boxes = objects.Select(i => Bounds(Pdfium.FPDFPage_GetObject(page, i), height)).ToArray();
        return new PageBox(boxes.Min(b => b.Left), boxes.Min(b => b.Top), boxes.Max(b => b.Right), boxes.Max(b => b.Bottom));
    }

    private static string ObjectText(nint obj, nint textPage)
    {
        uint bytes = FPDFTextObj_GetText(obj, textPage, null, 0);
        if (bytes <= 2) return "";
        var buffer = new char[bytes / 2];
        fixed (char* chars = buffer) FPDFTextObj_GetText(obj, textPage, chars, bytes);
        return new string(buffer, 0, Math.Max(0, buffer.Length - 1)).Replace('\u0002', '-').Replace(' ', ' '); // embedded fonts often map space to NBSP
    }

    private static uint FillColour(nint obj) =>
        FPDFPageObj_GetFillColor(obj, out uint r, out uint g, out uint b, out _) != 0 ? (r << 16) | (g << 8) | b : 0;

    public void Dispose()
    {
        lock (Pdfium.Gate) CloseDocument();
    }

    [LibraryImport("pdfium")] private static partial uint FPDFTextObj_GetText(nint textObject, nint textPage, char* buffer, uint length);
    [LibraryImport("pdfium")] private static partial int FPDFTextObj_GetFontSize(nint textObject, out float size);
    [LibraryImport("pdfium")] private static partial nint FPDFTextObj_GetFont(nint textObject);
    [LibraryImport("pdfium")] private static partial nuint FPDFFont_GetBaseFontName(nint font, byte* buffer, nuint length);
    [LibraryImport("pdfium")] private static partial int FPDFFont_GetFlags(nint font);
    [LibraryImport("pdfium")] private static partial int FPDFPageObj_GetMatrix(nint obj, out Matrix matrix);
    [LibraryImport("pdfium")] private static partial int FPDFPageObj_SetMatrix(nint obj, in Matrix matrix);
    [LibraryImport("pdfium")] private static partial void FPDFPageObj_Transform(nint obj, double a, double b, double c, double d, double e, double f);
    [LibraryImport("pdfium")] private static partial int FPDFPageObj_GetFillColor(nint obj, out uint r, out uint g, out uint b, out uint a);
    [LibraryImport("pdfium")] private static partial int FPDFPageObj_SetFillColor(nint obj, uint r, uint g, uint b, uint a);
    [LibraryImport("pdfium")] private static partial int FPDFText_SetText(nint textObject, char* text);
    [LibraryImport("pdfium", StringMarshalling = StringMarshalling.Utf8)] private static partial nint FPDFText_LoadStandardFont(nint document, string font);
    [LibraryImport("pdfium")] private static partial nint FPDFText_LoadFont(nint document, byte* data, uint size, int fontType, int cid);
    [LibraryImport("pdfium")] private static partial void FPDFFont_Close(nint font);
    [LibraryImport("pdfium")] private static partial nint FPDFPageObj_CreateTextObj(nint document, nint font, float fontSize);
    [LibraryImport("pdfium")] private static partial nint FPDFPageObj_NewImageObj(nint document);
    [LibraryImport("pdfium")] private static partial int FPDFImageObj_SetBitmap(nint* pages, int count, nint imageObject, nint bitmap);
    [LibraryImport("pdfium")] private static partial int FPDFImageObj_LoadJpegFileInline(nint* pages, int count, nint imageObject, FileAccess* access);
    [LibraryImport("pdfium")] private static partial int FPDFImageObj_SetMatrix(nint imageObject, double a, double b, double c, double d, double e, double f);
    [LibraryImport("pdfium")] private static partial void FPDFPage_InsertObject(nint page, nint obj);
    [LibraryImport("pdfium")] private static partial int FPDFPage_InsertObjectAtIndex(nint page, nint obj, nuint index);
    [LibraryImport("pdfium")] private static partial int FPDFPage_GenerateContent(nint page);
    [LibraryImport("pdfium")] private static partial int FPDF_SaveAsCopy(nint document, FileWrite* writer, uint flags);
    [LibraryImport("pdfium")] private static partial nint FPDFBitmap_CreateEx(int width, int height, int format, byte* firstScan, int stride);
}
