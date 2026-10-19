using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using Swivel.Engines.Pdf;

namespace Swivel.UiTests;

[Collection("Desktop")]
public class PdfEditorUiTests
{
    static PdfEditorUiTests() => GlobalFontSettings.UseWindowsFontsUnderWindows = true;

    private static string Handout(SwivelSession session)
    {
        string path = Path.Combine(session.Work, "handout.pdf");
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(612);
        page.Height = XUnit.FromPoint(792);
        using (var g = XGraphics.FromPdfPage(page))
        {
            g.DrawString("Assignment 3: Cell Biology", new XFont("Arial", 20, XFontStyleEx.Bold), XBrushes.Black, 72, 100);
            g.DrawString("Due date: 12 March 2026", new XFont("Arial", 12), XBrushes.Black, 72, 140);
        }
        doc.Save(path);
        return path;
    }

    private static string TextOf(string pdf)
    {
        using var session = PdfEditSession.Open(pdf);
        return string.Join("\n", session.Items(0).Where(i => i.IsText).Select(i => i.Text));
    }

    [Fact]
    public void Editor_retypes_a_line_in_place_and_saves_a_new_pdf()
    {
        using var session = new SwivelSession();
        string pdf = Handout(session);
        string before = TextOf(pdf);

        session.Launch("--wheel", pdf);
        Assert.NotNull(session.Find("Convert to DOCX, editable"));
        Keyboard.Type(VirtualKeyShort.ALT); // to Tools
        var wedge = session.Find("Edit, text & images");
        Assert.NotNull(wedge);
        wedge.Patterns.Invoke.Pattern.Invoke();

        var line = session.Find("Due date: 12 March 2026");
        Assert.NotNull(line);
        Mouse.DoubleClick(line.GetClickablePoint());
        Assert.NotNull(session.Find("Edit text"));
        Thread.Sleep(200);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type("Due date: 21 March 2026");
        Keyboard.Type(VirtualKeyShort.ENTER);
        var edited = session.Find("Due date: 21 March 2026");
        Assert.NotNull(edited);
        if (Environment.GetEnvironmentVariable("SWIVEL_KEEP_OUTPUT") is { Length: > 0 } keep)
        {
            Directory.CreateDirectory(keep);
            Mouse.Click(edited.GetClickablePoint()); // selected, so the outline shows
            Thread.Sleep(300);
            var window = edited.Parent;
            while (window?.Parent != null && window.ControlType != FlaUI.Core.Definitions.ControlType.Window) window = window.Parent;
            if (window != null) FlaUI.Core.Capturing.Capture.Element(window).ToFile(Path.Combine(keep, "editor.png"));
        }

        var save = session.Find("Save as new PDF");
        Assert.NotNull(save);
        save.Patterns.Invoke.Pattern.Invoke();

        string output = Path.Combine(session.Work, "handout edited.pdf");
        Assert.True(SwivelSession.WaitFor(() => File.Exists(output)), "handout edited.pdf never appeared");
        Thread.Sleep(300);
        Assert.Contains("Due date: 21 March 2026", TextOf(output));
        Assert.Contains("Assignment 3: Cell Biology", TextOf(output));
        Assert.Equal(before, TextOf(pdf));
    }
}
