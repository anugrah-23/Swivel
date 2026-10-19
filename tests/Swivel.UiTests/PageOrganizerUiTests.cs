using FlaUI.Core.Capturing;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Swivel.UiTests;

[Collection("Desktop")]
public class PageOrganizerUiTests
{
    /// <summary>A three-page PDF with pages 300, 320 and 340 points wide, so the result's order can be read.</summary>
    private static string ThreePages(SwivelSession session)
    {
        string path = Path.Combine(session.Work, "handout.pdf");
        using var doc = new PdfDocument();
        for (int i = 0; i < 3; i++)
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(300 + i * 20);
            page.Height = XUnit.FromPoint(420);
            using var g = XGraphics.FromPdfPage(page);
            g.DrawRectangle(new XSolidBrush(XColor.FromArgb(255, 60 + i * 80, 120, 200)), 30, 30, 200, 120);
        }
        doc.Save(path);
        return path;
    }

    [Fact]
    public void Organiser_deletes_a_page_and_saves_a_new_pdf()
    {
        using var session = new SwivelSession();
        string pdf = ThreePages(session);

        session.Launch("--wheel", pdf);
        Assert.NotNull(session.Find("Convert to DOCX, editable"));
        Keyboard.Type(VirtualKeyShort.ALT); // to Tools
        var wedge = session.Find("Pages, organise");
        Assert.NotNull(wedge);
        wedge.Patterns.Invoke.Pattern.Invoke();

        var first = session.Find("Page 1");
        Assert.NotNull(first);
        Thread.Sleep(800); // let the thumbnails render
        // The capture is the screen area under the window, so it can include whatever is behind it.
        if (Environment.GetEnvironmentVariable("SWIVEL_KEEP_OUTPUT") is { Length: > 0 } keep)
        {
            Directory.CreateDirectory(keep);
            var window = first.Parent;
            while (window?.Parent != null && window.ControlType != FlaUI.Core.Definitions.ControlType.Window) window = window.Parent;
            if (window != null) Capture.Element(window).ToFile(Path.Combine(keep, "organiser.png"));
        }

        Mouse.Click(first.GetClickablePoint());
        Keyboard.Type(VirtualKeyShort.DELETE);
        var save = session.Find("Save as new PDF");
        Assert.NotNull(save);
        save.Patterns.Invoke.Pattern.Invoke();

        string output = Path.Combine(session.Work, "handout edited.pdf");
        Assert.True(SwivelSession.WaitFor(() => File.Exists(output)), "handout edited.pdf never appeared");
        Thread.Sleep(300);
        using var result = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.Equal([320, 340], result.Pages.Cast<PdfPage>().Select(p => (int)Math.Round(p.MediaBox.Width)));
        using var original = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        Assert.Equal(3, original.PageCount);
    }
}
