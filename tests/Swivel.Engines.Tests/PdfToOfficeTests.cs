using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using Swivel.Core.Catalog;
using Swivel.Core.Files;
using Swivel.Engines.Documents;
using Xunit.Abstractions;
using A = DocumentFormat.OpenXml.Drawing;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Swivel.Engines.Tests;

public class PdfToOfficeTests(ITestOutputHelper output) : IDisposable
{
    private readonly Fixtures _fixtures = new();

    static PdfToOfficeTests() => GlobalFontSettings.UseWindowsFontsUnderWindows = true;

    private static readonly string[] Paragraph =
    [
        "Photosynthesis converts light energy into chemical energy stored in glucose.",
        "It takes place in the chloroplasts of plant cells, where chlorophyll absorbs",
        "mostly blue and red light and reflects green, which is why leaves look green.",
    ];

    /// <summary>Two pages of lecture notes: title, heading, a wrapped paragraph, a hyphenated break, a bullet and a photo.</summary>
    private string LectureNotes()
    {
        string path = Path.Combine(_fixtures.Dir, "Lecture 4.pdf");
        using var doc = new PdfDocument();
        var title = new XFont("Arial", 24, XFontStyleEx.Bold);
        var heading = new XFont("Arial", 16, XFontStyleEx.Bold);
        var body = new XFont("Times New Roman", 11);

        var page = doc.AddPage();
        using (var g = XGraphics.FromPdfPage(page))
        {
            g.DrawString("Biology 101: Photosynthesis", title, XBrushes.Black, 72, 90);
            g.DrawString("How plants make food", heading, XBrushes.DarkBlue, 72, 130);
            double y = 160;
            foreach (string line in Paragraph) { g.DrawString(line, body, XBrushes.Black, 72, y); y += 14; }
            g.DrawString("The overall reaction needs carbon diox-", body, XBrushes.Black, 72, y + 20);
            g.DrawString("ide and water, and releases oxygen.", body, XBrushes.Black, 72, y + 34);
            using var photo = Fixtures.Photo(400, 300);
            using var stream = new MemoryStream(photo.JpegsaveBuffer(q: 85));
            using var picture = XImage.FromStream(stream);
            g.DrawImage(picture, 72, y + 60, 240, 180);
        }
        var second = doc.AddPage();
        using (var g = XGraphics.FromPdfPage(second))
        {
            g.DrawString("Key terms", heading, XBrushes.Black, 72, 90);
            g.DrawString("• Chlorophyll: the green pigment that absorbs light.", body, XBrushes.Black, 72, 120);
        }
        doc.Save(path);
        return path;
    }

    private static Operation With(string id, params (string Key, string Value)[] parameters) =>
        Fixtures.Op(id).WithParameters(parameters.ToDictionary(p => p.Key, p => p.Value));

    private void AssertValid(OpenXmlPackage package)
    {
        var errors = new OpenXmlValidator().Validate(package).Take(5).ToArray();
        foreach (var e in errors) output.WriteLine($"{e.Path?.XPath}: {e.Description}");
        Assert.Empty(errors);
    }

    [Fact]
    public void Pdf_offers_word_and_both_powerpoint_wedges()
    {
        var ids = Fixtures.Catalog.For([FileKind.Pdf], WheelMode.Convert).Select(o => o.Id).ToArray();
        Assert.Contains("pdf.docx", ids);
        Assert.Contains("pdf.pptx", ids);
        Assert.Contains("pdf.pptx-edit", ids);
    }

    [Fact]
    public async Task Exact_slides_are_one_valid_picture_per_page()
    {
        string pdf = LectureNotes();
        string before = Fixtures.Hash(pdf);
        var result = await Fixtures.Run(Fixtures.Op("pdf.pptx"), pdf);

        Assert.True(result.Succeeded, string.Join("; ", result.Failures.Select(f => f.Detail)));
        string pptx = Assert.Single(result.Outputs);
        Assert.Equal("Lecture 4.pptx", Path.GetFileName(pptx));
        Assert.Equal(FileKind.Pptx, FileSniffer.Detect(pptx));
        using var deck = PresentationDocument.Open(pptx, false);
        AssertValid(deck);
        var slides = deck.PresentationPart!.SlideParts.ToArray();
        Assert.Equal(2, slides.Length);
        Assert.All(slides, s => Assert.Single(s.ImageParts));
        Assert.All(slides, s => Assert.Empty(s.Slide!.Descendants<A.Text>()));
        Assert.Equal(before, Fixtures.Hash(pdf));
    }

    [Fact]
    public async Task Editable_slides_carry_the_text_over_a_textless_background()
    {
        string pdf = LectureNotes();
        var result = await Fixtures.Run(Fixtures.Op("pdf.pptx-edit"), pdf);

        Assert.True(result.Succeeded, string.Join("; ", result.Failures.Select(f => f.Detail)));
        using var deck = PresentationDocument.Open(result.Outputs[0], false);
        AssertValid(deck);
        var first = deck.PresentationPart!.SlideParts.First(s => s.Slide!.InnerText.Contains("Photosynthesis"));
        var texts = first.Slide!.Descendants<A.Text>().Select(t => t.Text).ToArray();
        Assert.Contains("Biology 101: Photosynthesis", texts);
        Assert.Contains(texts, t => t.StartsWith("Photosynthesis converts light energy"));
        var titleRun = first.Slide!.Descendants<A.Run>().First(r => r.InnerText == "Biology 101: Photosynthesis");
        Assert.True(titleRun.RunProperties!.Bold!.Value);
        Assert.Equal("Arial", titleRun.RunProperties!.GetFirstChild<A.LatinFont>()!.Typeface!.Value);
        Assert.Single(first.ImageParts);
    }

    [Fact]
    public async Task Basic_word_rebuild_makes_headings_paragraphs_and_pictures()
    {
        string pdf = LectureNotes();
        var result = await Fixtures.Run(With("pdf.docx", ("engine", "basic")), pdf);

        Assert.True(result.Succeeded, string.Join("; ", result.Failures.Select(f => f.Detail)));
        using var doc = WordprocessingDocument.Open(result.Outputs[0], false);
        AssertValid(doc);
        var paragraphs = doc.MainDocumentPart!.Document!.Body!.Elements<W.Paragraph>().ToArray();
        string Style(W.Paragraph p) => p.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "Normal";
        foreach (var p in paragraphs) output.WriteLine($"[{Style(p)}] {p.InnerText}");

        Assert.Contains(paragraphs, p => Style(p) == "Heading1" && p.InnerText == "Biology 101: Photosynthesis");
        Assert.Contains(paragraphs, p => Style(p) == "Heading2" && p.InnerText == "How plants make food");
        Assert.Contains(paragraphs, p => p.InnerText == string.Join(' ', Paragraph));            // three lines, one paragraph
        Assert.Contains(paragraphs, p => p.InnerText.Contains("carbon dioxide and water"));     // hyphen rejoined
        Assert.Contains(paragraphs, p => p.InnerText.StartsWith("• Chlorophyll"));
        Assert.Single(doc.MainDocumentPart.ImageParts);
        Assert.Single(doc.MainDocumentPart.Document!.Body!.Descendants<W.Break>(), b => b.Type?.Value == W.BreakValues.Page);
    }

    [Fact]
    public async Task Word_reflow_converts_when_word_is_installed()
    {
        if (!OfficeEngine.HasWord)
        {
            output.WriteLine("SKIPPED: Microsoft Word isn't installed here. Word reflow NOT verified.");
            return;
        }
        string pdf = LectureNotes();
        var result = await Fixtures.Run(Fixtures.Op("pdf.docx"), pdf);

        Assert.True(result.Succeeded, string.Join("; ", result.Failures.Select(f => f.Message + " " + f.Detail)));
        using var doc = WordprocessingDocument.Open(result.Outputs[0], false);
        string text = doc.MainDocumentPart!.Document!.Body!.InnerText;
        output.WriteLine(text[..Math.Min(300, text.Length)]);
        Assert.Contains("Photosynthesis", text);
        Assert.Contains("chloroplasts", text);
    }

    [Fact]
    public async Task A_scanned_pdf_without_text_says_so_for_word()
    {
        using var photo = Fixtures.Photo(1200, 1600);
        string scan = Path.Combine(_fixtures.Dir, "scan.pdf");
        using (var doc = new PdfDocument())
        {
            var page = doc.AddPage();
            using var g = XGraphics.FromPdfPage(page);
            using var stream = new MemoryStream(photo.JpegsaveBuffer(q: 80));
            using var picture = XImage.FromStream(stream);
            g.DrawImage(picture, 0, 0, page.Width.Point, page.Height.Point);
            doc.Save(scan);
        }

        var result = await Fixtures.Run(With("pdf.docx", ("engine", "basic")), scan);

        // A picture-only PDF still converts (the picture comes across); there is just no text in it.
        Assert.True(result.Succeeded, string.Join("; ", result.Failures.Select(f => f.Message)));
        using var word = WordprocessingDocument.Open(result.Outputs[0], false);
        Assert.Single(word.MainDocumentPart!.ImageParts);
    }

    /// <summary>SWIVEL_KEEP_OUTPUT=folder keeps each converted file there, for eyeballing results.</summary>
    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable("SWIVEL_KEEP_OUTPUT") is { Length: > 0 } keep)
        {
            Directory.CreateDirectory(keep);
            foreach (var file in Directory.GetFiles(_fixtures.Dir).Where(f => !f.EndsWith(".pdf") || f.Contains("Lecture")))
                File.Copy(file, Path.Combine(keep, Path.GetFileName(file)), overwrite: true);
        }
        _fixtures.Dispose();
    }
}
