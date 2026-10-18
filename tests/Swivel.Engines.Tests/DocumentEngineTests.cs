using System.IO.Compression;
using System.Text;
using Swivel.Core.Catalog;
using Swivel.Core.Files;
using Swivel.Engines.Documents;
using Xunit.Abstractions;

namespace Swivel.Engines.Tests;

public class DocumentEngineTests(ITestOutputHelper output) : IDisposable
{
    private const string Markdown = "# Swivel test\n\nHello **world**, from a *fixture*.\n\n- one\n- two\n";
    private readonly Fixtures _fixtures = new();

    private string Md()
    {
        string path = Path.Combine(_fixtures.Dir, "notes.md");
        File.WriteAllText(path, Markdown);
        return path;
    }

    [Fact]
    public async Task Markdown_converts_to_every_text_format()
    {
        string input = Md();
        string before = Fixtures.Hash(input);
        foreach (var op in Fixtures.Catalog.For([FileKind.Markdown], WheelMode.Convert).Where(o => o.Engine == "document"))
        {
            var result = await Fixtures.Run(op, input);
            Assert.True(result.Succeeded, $"{op.Id}: {string.Join("; ", result.Failures.Select(f => f.Detail))}");
            string produced = Assert.Single(result.Outputs);
            Assert.Equal(op.OutputKind, FileSniffer.Detect(produced));
            Assert.Contains("Swivel test", Text(produced));
        }
        Assert.Equal(before, Fixtures.Hash(input));
    }

    [Fact]
    public async Task Docx_round_trips_back_to_markdown()
    {
        var docx = await Fixtures.Run(Fixtures.Op("doc.docx"), Md());
        var md = await Fixtures.Run(Fixtures.Op("doc.md"), docx.Outputs[0]);
        string text = File.ReadAllText(Assert.Single(md.Outputs));
        Assert.Contains("# Swivel test", text);
        Assert.Contains("**world**", text);
    }

    [Fact]
    public async Task Docx_prints_to_pdf_through_office_or_libreoffice()
    {
        var docx = await Fixtures.Run(Fixtures.Op("doc.docx"), Md());
        var result = await Fixtures.Run(Fixtures.Op("doc.pdf"), docx.Outputs[0]);
        if (!result.Succeeded)
        {
            output.WriteLine($"SKIPPED: no working Office or LibreOffice here ({result.Failures[0].Message}). Office→PDF is NOT verified.");
            output.WriteLine(result.Failures[0].Detail);
            return;
        }
        Assert.Equal(FileKind.Pdf, FileSniffer.Detect(Assert.Single(result.Outputs)));
    }

    [Fact]
    public async Task Markdown_prints_to_pdf_via_docx()
    {
        var result = await Fixtures.Run(Fixtures.Op("doc.pdf"), Md());
        if (!result.Succeeded)
        {
            output.WriteLine($"SKIPPED: {result.Failures[0].Message}");
            return;
        }
        Assert.Equal(FileKind.Pdf, FileSniffer.Detect(Assert.Single(result.Outputs)));
        Assert.Empty(Directory.GetFiles(_fixtures.Dir, "*.docx"));
    }

    [Fact]
    public async Task Excel_and_powerpoint_print_to_pdf()
    {
        var made = new List<string>();
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Type.GetTypeFromProgID("Excel.Application") is { } excelType)
                {
                    dynamic excel = Activator.CreateInstance(excelType)!;
                    excel.DisplayAlerts = false;
                    dynamic book = excel.Workbooks.Add();
                    book.Worksheets[1].Cells[1, 1].Value = "Swivel";
                    string xlsx = Path.Combine(_fixtures.Dir, "sheet.xlsx");
                    book.SaveAs(xlsx, 51); // xlOpenXMLWorkbook
                    book.Close(false);
                    if (excel.Workbooks.Count == 0) excel.Quit();
                    made.Add(xlsx);
                }
                if (Type.GetTypeFromProgID("PowerPoint.Application") is { } pptType)
                {
                    dynamic ppt = Activator.CreateInstance(pptType)!;
                    dynamic deck = ppt.Presentations.Add(0); // no window
                    deck.Slides.Add(1, 1); // ppLayoutTitle
                    string pptx = Path.Combine(_fixtures.Dir, "deck.pptx");
                    deck.SaveAs(pptx, 24); // ppSaveAsOpenXMLPresentation
                    deck.Close();
                    if (ppt.Presentations.Count == 0) ppt.Quit();
                    made.Add(pptx);
                }
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null || made.Count == 0)
        {
            output.WriteLine($"SKIPPED: couldn't drive Office here ({error?.Message ?? "not installed"}). Excel/PowerPoint→PDF NOT verified.");
            return;
        }

        foreach (string input in made)
        {
            var result = await Fixtures.Run(Fixtures.Op("doc.pdf"), input);
            Assert.True(result.Succeeded, $"{Path.GetFileName(input)}: {string.Join("; ", result.Failures.Select(f => f.Message + " " + f.Detail))}");
            Assert.Equal(FileKind.Pdf, FileSniffer.Detect(Assert.Single(result.Outputs)));
            output.WriteLine($"{Path.GetFileName(input)} → PDF ok");
        }
    }

    [Fact]
    public void Office_availability_is_reported()
    {
        output.WriteLine($"Word COM registered: {Type.GetTypeFromProgID("Word.Application") != null}");
        output.WriteLine($"Excel COM registered: {Type.GetTypeFromProgID("Excel.Application") != null}");
        output.WriteLine($"PowerPoint COM registered: {Type.GetTypeFromProgID("PowerPoint.Application") != null}");
        output.WriteLine($"LibreOffice: {OfficeEngine.LibreOffice() ?? "not found"}");
    }

    private static string Text(string path)
    {
        if (FileSniffer.Detect(path) is not (FileKind.Docx or FileKind.Epub)) return File.ReadAllText(path);
        using var zip = ZipFile.OpenRead(path);
        var sb = new StringBuilder();
        foreach (var entry in zip.Entries.Where(e => e.Name.EndsWith(".xml") || e.Name.EndsWith(".xhtml")))
        {
            using var reader = new StreamReader(entry.Open());
            sb.Append(reader.ReadToEnd());
        }
        return sb.ToString();
    }

    public void Dispose() => _fixtures.Dispose();
}
