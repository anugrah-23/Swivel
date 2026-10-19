using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Swivel.Engines.Pdf;
using Xunit.Abstractions;

namespace Swivel.Engines.Tests;

public class PdfEditSessionTests(ITestOutputHelper output) : IDisposable
{
    private readonly Fixtures _fixtures = new();

    static PdfEditSessionTests() => GlobalFontSettings.UseWindowsFontsUnderWindows = true;

    /// <summary>A handout: a title, a date line, a body line, and a photo.</summary>
    private string Handout()
    {
        string path = Path.Combine(_fixtures.Dir, "handout.pdf");
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(612);
        page.Height = XUnit.FromPoint(792);
        using (var g = XGraphics.FromPdfPage(page))
        {
            g.DrawString("Assignment 3: Cell Biology", new XFont("Arial", 20, XFontStyleEx.Bold), XBrushes.Black, 72, 100);
            g.DrawString("Due date: 12 March 2026", new XFont("Arial", 12), XBrushes.Black, 72, 140);
            g.DrawString("Submit your answers on the portal.", new XFont("Times New Roman", 12), XBrushes.Black, 72, 170);
            using var photo = Fixtures.Photo(400, 300);
            using var stream = new MemoryStream(photo.JpegsaveBuffer(q: 85));
            using var picture = XImage.FromStream(stream);
            g.DrawImage(picture, 72, 220, 200, 150);
        }
        doc.Save(path);
        return path;
    }

    private string Picture(string name, int width, int height)
    {
        string path = Path.Combine(_fixtures.Dir, name);
        using var image = Fixtures.Photo(width, height);
        image.Pngsave(path);
        return path;
    }

    /// <summary>All text in a saved PDF, as PDFium reads it back.</summary>
    private static string TextOf(string path)
    {
        using var session = PdfEditSession.Open(path);
        return string.Join("\n", session.Items(0).Where(i => i.IsText).Select(i => i.Text));
    }

    private string Saved(PdfEditSession session)
    {
        string path = Path.Combine(_fixtures.Dir, $"out-{Guid.NewGuid():N}.pdf");
        session.Save(path);
        using var check = PdfReader.Open(path, PdfDocumentOpenMode.Import); // must stay a valid PDF
        Assert.Equal(1, check.PageCount);
        return path;
    }

    [Fact]
    public void Lists_lines_of_text_and_images()
    {
        using var session = PdfEditSession.Open(Handout());
        var items = session.Items(0);
        foreach (var item in items) output.WriteLine($"{item.Id} {item.Text} {item.Box} {item.Font} {item.FontSize} bold={item.Bold}");

        Assert.Contains(items, i => i.IsText && i.Text == "Assignment 3: Cell Biology" && i.Bold && i.Font == "Arial");
        Assert.Contains(items, i => i.IsText && i.Text == "Due date: 12 March 2026");
        Assert.Single(items, i => !i.IsText);
    }

    [Fact]
    public void A_typo_fix_keeps_the_original_font()
    {
        string source = Handout();
        string before = Fixtures.Hash(source);
        using var session = PdfEditSession.Open(source);
        var date = session.Items(0).Single(i => i.Text.StartsWith("Due date"));

        // Every letter here already appears in the Arial text, so no font change is needed.
        string? note = session.Apply(new EditText(0, date.Id, "Due date: 21 March 2026"));

        Assert.Null(note);
        string saved = Saved(session);
        Assert.Contains("Due date: 21 March 2026", TextOf(saved));
        Assert.DoesNotContain("12 March", TextOf(saved));
        Assert.Equal(before, Fixtures.Hash(source));
    }

    [Fact]
    public void New_letters_switch_that_line_to_a_standard_font_and_say_so()
    {
        using var session = PdfEditSession.Open(Handout());
        var body = session.Items(0).Single(i => i.Text.StartsWith("Submit"));

        string? note = session.Apply(new EditText(0, body.Id, "Upload PDF answers via Quizwiz by Friday!"));

        Assert.NotNull(note);
        Assert.Contains("Times", note);
        var edited = session.Items(0).Single(i => i.Text.StartsWith("Upload"));
        Assert.Equal("Upload PDF answers via Quizwiz by Friday!", edited.Text);
        Assert.InRange(edited.Box.Left, body.Box.Left - 1, body.Box.Left + 1);   // stays where it was
        Assert.Contains("Quizwiz", TextOf(Saved(session)));
    }

    [Fact]
    public void Non_latin_text_embeds_a_unicode_font()
    {
        using var session = PdfEditSession.Open(Handout());
        var body = session.Items(0).Single(i => i.Text.StartsWith("Submit"));
        string? note = session.Apply(new EditText(0, body.Id, "Сдайте ответы на портале"));
        Assert.Contains("Arial", note);
        Assert.Contains("Сдайте ответы на портале", TextOf(Saved(session)));
        Assert.Contains("Сдайте ответы на портале", TextOf(Saved(session))); // saving twice gives the same file
    }

    [Fact]
    public void Images_move_resize_replace_delete_and_add()
    {
        using var session = PdfEditSession.Open(Handout());
        var photo = session.Items(0).Single(i => !i.IsText);

        session.Apply(new MoveItem(0, photo.Id, new PageBox(300, 400, 500, 550)));
        var moved = session.Items(0).Single(i => !i.IsText);
        Assert.Equal(300, moved.Box.Left, 1);
        Assert.Equal(400, moved.Box.Top, 1);
        Assert.Equal(200, moved.Box.Width, 1);
        Assert.Equal(150, moved.Box.Height, 1);

        // A tall picture replacing a wide one keeps its own shape, centred in the old box.
        session.Apply(new ReplaceImage(0, moved.Id, Picture("tall.png", 300, 600)));
        var replaced = session.Items(0).Single(i => !i.IsText);
        Assert.Equal(75, replaced.Box.Width, 1);
        Assert.Equal(150, replaced.Box.Height, 1);
        Assert.Equal(362.5, replaced.Box.Left, 1);

        session.Apply(new AddImage(0, Picture("logo.png", 200, 200), new PageBox(420, 60, 520, 160)));
        Assert.Equal(2, session.Items(0).Count(i => !i.IsText));

        session.Apply(new DeleteItem(0, session.Items(0).First(i => !i.IsText).Id));
        Assert.Single(session.Items(0), i => !i.IsText);
        Saved(session);
    }

    [Fact]
    public void New_text_lands_where_it_was_placed()
    {
        using var session = PdfEditSession.Open(Handout());
        session.Apply(new AddText(0, 72, 700, "Name: ____________", 14, Bold: true));

        var added = session.Items(0).Single(i => i.Text.StartsWith("Name:"));
        Assert.InRange(added.Box.Left, 70, 76);
        Assert.InRange(added.Box.Top, 695, 712);
        Assert.Contains("Name: ____________", TextOf(Saved(session)));
    }

    [Fact]
    public void Undo_and_redo_replay_exactly()
    {
        using var session = PdfEditSession.Open(Handout());
        var date = session.Items(0).Single(i => i.Text.StartsWith("Due date"));
        session.Apply(new EditText(0, date.Id, "Due date: 21 March 2026"));
        session.Apply(new DeleteItem(0, session.Items(0).Single(i => !i.IsText).Id));
        Assert.True(session.CanUndo);

        session.Undo();
        Assert.Single(session.Items(0), i => !i.IsText);                       // photo back
        Assert.Contains(session.Items(0), i => i.Text == "Due date: 21 March 2026"); // first edit kept
        session.Undo();
        Assert.Contains(session.Items(0), i => i.Text == "Due date: 12 March 2026");
        Assert.False(session.CanUndo);

        session.Redo();
        session.Redo();
        Assert.Contains(session.Items(0), i => i.Text == "Due date: 21 March 2026");
        Assert.DoesNotContain(session.Items(0), i => !i.IsText);
    }

    [Fact]
    public void Edited_pages_render()
    {
        using var session = PdfEditSession.Open(Handout());
        session.Apply(new EditText(0, session.Items(0)[0].Id, "Assignment 4: Cell Biology"));
        var render = session.Render(0, 72);
        Assert.Equal((612, 792), (render.Width, render.Height));
    }

    public void Dispose() => _fixtures.Dispose();
}
