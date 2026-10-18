using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Swivel.Core.Catalog;
using Swivel.Core.Files;
using VipsImage = NetVips.Image;

namespace Swivel.Engines.Tests;

public class PdfEngineTests : IDisposable
{
    private readonly Fixtures _fixtures = new();

    /// <summary>A Letter-size PDF with one coloured shape per page (no fonts needed).</summary>
    private string MakePdf(string name, int pages, byte[]? photoJpeg = null)
    {
        string path = Path.Combine(_fixtures.Dir, name);
        using var doc = new PdfDocument();
        for (int i = 0; i < pages; i++)
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(612);
            page.Height = XUnit.FromPoint(792);
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(255, 40 * i % 255, 120, 200)), 72, 72, 300, 200);
            if (photoJpeg != null)
            {
                using var stream = new MemoryStream(photoJpeg);
                using var picture = XImage.FromStream(stream);
                gfx.DrawImage(picture, 72, 300, 468, 351);
            }
        }
        doc.Save(path);
        return path;
    }

    private static int Pages(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return doc.PageCount;
    }

    [Fact]
    public async Task Merge_joins_in_name_order()
    {
        string two = MakePdf("part 2.pdf", 2), ten = MakePdf("part 10.pdf", 3), one = MakePdf("part 1.pdf", 1);
        var result = await Fixtures.Run(Fixtures.Op("pdf.merge"), ten, two, one);

        string merged = Assert.Single(result.Outputs);
        Assert.Equal(Path.Combine(_fixtures.Dir, Path.GetFileName(_fixtures.Dir) + " merged.pdf"), merged);
        Assert.Equal(6, Pages(merged));
    }

    [Fact]
    public async Task Merge_needs_two_files() =>
        Assert.DoesNotContain(Fixtures.Catalog.For([FileKind.Pdf], WheelMode.Tools), o => o.Id == "pdf.merge");

    [Fact]
    public async Task Split_makes_one_file_per_page()
    {
        string input = MakePdf("report.pdf", 3);
        var result = await Fixtures.Run(Fixtures.Op("pdf.split"), input);

        string folder = Assert.Single(result.Outputs);
        Assert.Equal(Path.Combine(_fixtures.Dir, "report pages"), folder);
        string[] pages = Directory.GetFiles(folder).Select(p => Path.GetFileName(p)).Order().ToArray();
        Assert.Equal(["Page 01.pdf", "Page 02.pdf", "Page 03.pdf"], pages);
        Assert.All(Directory.GetFiles(folder), p => Assert.Equal(1, Pages(p)));
    }

    [Theory]
    [InlineData("pdf.png", ".png")]
    [InlineData("pdf.jpg", ".jpg")]
    public async Task Pages_render_to_pictures_at_150_dpi(string op, string ext)
    {
        string input = MakePdf("slides.pdf", 2);
        var result = await Fixtures.Run(Fixtures.Op(op), input);

        Assert.True(result.Succeeded, string.Join("; ", result.Failures.Select(f => f.Detail)));
        var files = Directory.GetFiles(Assert.Single(result.Outputs), "*" + ext);
        Assert.Equal(2, files.Length);
        using var image = VipsImage.NewFromFile(files[0]);
        Assert.Equal((1275, 1650), (image.Width, image.Height));
    }

    [Fact]
    public async Task Images_combine_into_one_pdf()
    {
        var images = new[] { _fixtures.Write("b.jpg"), _fixtures.Write("a.png"), _fixtures.Write("c.webp") };
        var result = await Fixtures.Run(Fixtures.Op("image.combine-pdf"), images);

        Assert.True(result.Succeeded, string.Join("; ", result.Failures.Select(f => f.Detail)));
        Assert.Equal(3, Pages(Assert.Single(result.Outputs)));
    }

    [Fact]
    public async Task Compress_shrinks_photo_heavy_pdfs()
    {
        using var photo = Fixtures.Photo(3000, 2250);
        string input = MakePdf("scan.pdf", 3, photo.JpegsaveBuffer(q: 95));
        string before = Fixtures.Hash(input);

        var result = await Fixtures.Run(Fixtures.Op("pdf.compress"), input);

        string produced = Assert.Single(result.Outputs);
        Assert.Equal(3, Pages(produced));
        Assert.True(new FileInfo(produced).Length < new FileInfo(input).Length * 0.6,
            $"{new FileInfo(produced).Length} vs {new FileInfo(input).Length}");
        Assert.Equal(before, Fixtures.Hash(input));
    }

    [Fact]
    public async Task Broken_pdf_fails_cleanly()
    {
        string input = Path.Combine(_fixtures.Dir, "broken.pdf");
        File.WriteAllText(input, "%PDF-1.7 nope");
        var result = await Fixtures.Run(Fixtures.Op("pdf.split"), input);
        Assert.StartsWith("Couldn't crack broken.pdf", Assert.Single(result.Failures).Message);
        Fixtures.AssertNoTemp(_fixtures.Dir);
    }

    public void Dispose() => _fixtures.Dispose();
}
