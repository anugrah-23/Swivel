using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Swivel.Core.Catalog;
using Swivel.Core.Files;

namespace Swivel.Engines.Tests;

public class PageOrganizerTests : IDisposable
{
    private readonly Fixtures _fixtures = new();

    /// <summary>A PDF whose pages are 300, 310, 320… points wide, so order can be read back.</summary>
    private string Numbered(string name, int pages, int firstWidth)
    {
        string path = Path.Combine(_fixtures.Dir, name);
        using var doc = new PdfDocument();
        for (int i = 0; i < pages; i++)
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(firstWidth + i * 10);
            page.Height = XUnit.FromPoint(500);
            using var g = XGraphics.FromPdfPage(page);
            g.DrawRectangle(XBrushes.SteelBlue, 20, 20, 100, 60);
        }
        doc.Save(path);
        return path;
    }

    private static (int Width, int Rotate)[] Pages(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return doc.Pages.Cast<PdfPage>().Select(p => ((int)Math.Round(p.MediaBox.Width), p.Rotate)).ToArray();
    }

    private static Operation Organize(string plan, string? sources = null)
    {
        var parameters = new Dictionary<string, string> { ["plan"] = plan };
        if (sources != null) parameters["sources"] = sources;
        return Fixtures.Op("pdf.organize").WithParameters(parameters);
    }

    [Fact]
    public async Task Reorders_rotates_and_drops_pages_into_a_new_file()
    {
        string pdf = Numbered("notes.pdf", 4, 300); // widths 300, 310, 320, 330
        string before = Fixtures.Hash(pdf);

        var result = await Fixtures.Run(Organize("0:3:0;0:0:90;0:2:270"), pdf);

        Assert.True(result.Succeeded, string.Join("; ", result.Failures.Select(f => f.Message)));
        string output = Assert.Single(result.Outputs);
        Assert.Equal("notes edited.pdf", Path.GetFileName(output));
        Assert.Equal([(330, 0), (300, 90), (320, 270)], Pages(output));
        Assert.Equal(before, Fixtures.Hash(pdf));
        Assert.Equal(4, Pages(pdf).Length);
    }

    [Fact]
    public async Task Mixes_pages_from_several_pdfs_including_ones_added_in_the_window()
    {
        string a = Numbered("a.pdf", 2, 300), b = Numbered("b.pdf", 2, 400), added = Numbered("extra.pdf", 1, 500);

        var result = await Fixtures.Run(Organize("1:1:0;2:0:0;0:0:0", sources: added), a, b);

        Assert.True(result.Succeeded, string.Join("; ", result.Failures.Select(f => f.Message)));
        Assert.Equal([410, 500, 300], Pages(result.Outputs[0]).Select(p => p.Width));
    }

    [Fact]
    public async Task Existing_page_rotation_is_added_to()
    {
        string pdf = Numbered("turned.pdf", 1, 300);
        using (var doc = PdfReader.Open(pdf, PdfDocumentOpenMode.Modify)) { doc.Pages[0].Rotate = 90; doc.Save(pdf); }

        var result = await Fixtures.Run(Organize("0:0:90"), pdf);

        Assert.Equal(180, Pages(result.Outputs[0])[0].Rotate);
    }

    [Fact]
    public async Task A_page_that_does_not_exist_fails_cleanly()
    {
        string pdf = Numbered("short.pdf", 2, 300);
        var result = await Fixtures.Run(Organize("0:5:0"), pdf);
        Assert.Equal("short.pdf has no page 6.", Assert.Single(result.Failures).Message);
        Fixtures.AssertNoTemp(_fixtures.Dir);
    }

    [Fact]
    public void Organiser_is_offered_for_one_or_many_pdfs()
    {
        Assert.Contains(Fixtures.Catalog.For([FileKind.Pdf], WheelMode.Tools), o => o.Id == "pdf.organize");
        Assert.Contains(Fixtures.Catalog.For([FileKind.Pdf, FileKind.Pdf], WheelMode.Tools), o => o.Id == "pdf.organize");
    }

    public void Dispose() => _fixtures.Dispose();
}
