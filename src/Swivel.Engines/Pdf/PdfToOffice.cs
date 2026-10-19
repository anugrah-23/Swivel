using System.Globalization;
using System.Security;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using NetVips;
using Swivel.Core.Jobs;
using VipsImage = NetVips.Image;

namespace Swivel.Engines.Pdf;

/// <summary>
/// PDF to PowerPoint (one slide per page, exact or with editable text) and Swivel's own basic PDF to
/// Word rebuild, used when Microsoft Word isn't there to reflow the PDF itself.
/// </summary>
internal static class PdfToOffice
{
    private const long SlideWidth = 12_192_000;   // 13.33 in, PowerPoint's widescreen width
    private const long EmuPerPoint = 12_700;
    private const long WordTextWidth = 5_943_600; // 6.5 in between 1 in margins
    private const double SlideRenderWidth = 1920;

    // ---- PowerPoint --------------------------------------------------------------------------------

    public static void ToPptx(string pdfPath, string output, bool editable, IProgress<double> progress, CancellationToken ct)
    {
        byte[] pdf = File.ReadAllBytes(pdfPath);
        string name = Path.GetFileName(pdfPath);
        int pages = Pdfium.PageCount(pdf, name);
        if (pages == 0) throw new JobException($"{name} has no pages.");

        var first = Pdfium.ReadPage(pdf, name, 0);
        long slideCx = SlideWidth;
        long slideCy = Math.Clamp((long)(slideCx * first.Height / first.Width), 914_400, 51_206_400);

        using var package = PresentationDocument.Create(output, DocumentFormat.OpenXml.PresentationDocumentType.Presentation);
        var presentation = package.AddPresentationPart();
        var master = presentation.AddNewPart<SlideMasterPart>();
        var layout = master.AddNewPart<SlideLayoutPart>();
        layout.AddPart(master);
        var theme = master.AddNewPart<ThemePart>();
        presentation.AddPart(theme);
        Feed(theme, Theme);
        Feed(layout, Layout);
        Feed(master, Master(master.GetIdOfPart(layout)));

        var slideIds = new StringBuilder();
        for (int i = 0; i < pages; i++)
        {
            ct.ThrowIfCancellationRequested();
            var content = i == 0 ? first : Pdfium.ReadPage(pdf, name, i);

            // Fit the page inside the slide, centred, keeping its shape.
            double scale = Math.Min(slideCx / content.Width, slideCy / content.Height); // EMU per PDF point
            long offX = (long)((slideCx - content.Width * scale) / 2), offY = (long)((slideCy - content.Height * scale) / 2);

            double dpi = Math.Clamp(SlideRenderWidth / content.Width * 72, 72, 300);
            var render = editable ? Pdfium.RenderWithoutText(pdf, name, i, dpi) : Pdfium.RenderOne(pdf, name, i, dpi);

            var slide = presentation.AddNewPart<SlidePart>();
            slide.AddPart(layout);
            var picture = slide.AddImagePart(ImagePartType.Jpeg);
            using (var jpeg = new MemoryStream(Jpeg(render.Bgra, render.Width, render.Height))) picture.FeedData(jpeg);

            var shapes = new StringBuilder();
            shapes.Append(Picture(2, "Page", slide.GetIdOfPart(picture), offX, offY, (long)(content.Width * scale), (long)(content.Height * scale)));
            if (editable)
            {
                int id = 3;
                foreach (var line in content.Lines)
                    shapes.Append(TextBox(id++, line, offX, offY, scale));
            }
            Feed(slide, Slide(shapes.ToString()));
            slideIds.Append(CultureInfo.InvariantCulture, $"<p:sldId id=\"{256 + i}\" r:id=\"{presentation.GetIdOfPart(slide)}\"/>");
            progress.Report((i + 1.0) / pages * 0.95);
        }

        Feed(presentation, $"""
            <p:presentation {Ns} saveSubsetFonts="1">
              <p:sldMasterIdLst><p:sldMasterId id="2147483648" r:id="{presentation.GetIdOfPart(master)}"/></p:sldMasterIdLst>
              <p:sldIdLst>{slideIds}</p:sldIdLst>
              <p:sldSz cx="{slideCx}" cy="{slideCy}"/>
              <p:notesSz cx="6858000" cy="9144000"/>
            </p:presentation>
            """);
    }

    private static string TextBox(int id, Pdfium.TextLine line, long offX, long offY, double scale)
    {
        long x = offX + (long)(line.Left * scale), y = offY + (long)(line.Top * scale);
        // Substitute fonts run a little wider than the original, so the box gets slack; it never wraps.
        long cx = Math.Max(EmuPerPoint, (long)(line.Width * scale * 1.1)), cy = Math.Max(EmuPerPoint, (long)(line.Height * scale));
        int size = (int)Math.Clamp(Math.Round(line.FontSize * scale / EmuPerPoint * 100), 100, 400_000);
        return $"""
            <p:sp><p:nvSpPr><p:cNvPr id="{id}" name="Text {id}"/><p:cNvSpPr txBox="1"/><p:nvPr/></p:nvSpPr>
            <p:spPr><a:xfrm><a:off x="{x}" y="{y}"/><a:ext cx="{cx}" cy="{cy}"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom><a:noFill/></p:spPr>
            <p:txBody><a:bodyPr wrap="none" lIns="0" tIns="0" rIns="0" bIns="0" anchor="t"><a:noAutofit/></a:bodyPr><a:lstStyle/>
            <a:p><a:r><a:rPr lang="en-US" sz="{size}"{(line.Bold ? " b=\"1\"" : "")}{(line.Italic ? " i=\"1\"" : "")} dirty="0"><a:solidFill><a:srgbClr val="{line.Colour:X6}"/></a:solidFill>{Typeface(line.Font, "a")}</a:rPr><a:t>{Xml(line.Text)}</a:t></a:r></a:p></p:txBody></p:sp>
            """;
    }

    private static string Picture(int id, string name, string relationship, long x, long y, long cx, long cy) => $"""
        <p:pic><p:nvPicPr><p:cNvPr id="{id}" name="{name}"/><p:cNvPicPr><a:picLocks noChangeAspect="1"/></p:cNvPicPr><p:nvPr/></p:nvPicPr>
        <p:blipFill><a:blip r:embed="{relationship}"/><a:stretch><a:fillRect/></a:stretch></p:blipFill>
        <p:spPr><a:xfrm><a:off x="{x}" y="{y}"/><a:ext cx="{cx}" cy="{cy}"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></p:spPr></p:pic>
        """;

    private static string Slide(string shapes) => $"""
        <p:sld {Ns}><p:cSld><p:spTree>
        <p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>
        {shapes}
        </p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>
        """;

    private const string Ns =
        "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
        "xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"";

    private const string EmptyTree =
        "<p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/></p:spTree>";

    private static string Master(string layoutRelationship) => $"""
        <p:sldMaster {Ns}><p:cSld>{EmptyTree}</p:cSld>
        <p:clrMap bg1="lt1" tx1="dk1" bg2="lt2" tx2="dk2" accent1="accent1" accent2="accent2" accent3="accent3" accent4="accent4" accent5="accent5" accent6="accent6" hlink="hlink" folHlink="folHlink"/>
        <p:sldLayoutIdLst><p:sldLayoutId id="2147483649" r:id="{layoutRelationship}"/></p:sldLayoutIdLst>
        <p:txStyles><p:titleStyle/><p:bodyStyle/><p:otherStyle/></p:txStyles></p:sldMaster>
        """;

    private static readonly string Layout = $"""
        <p:sldLayout {Ns} type="blank" preserve="1"><p:cSld name="Blank">{EmptyTree}</p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>
        """;

    private const string Theme = """
        <a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" name="Swivel"><a:themeElements>
        <a:clrScheme name="Swivel"><a:dk1><a:sysClr val="windowText" lastClr="000000"/></a:dk1><a:lt1><a:sysClr val="window" lastClr="FFFFFF"/></a:lt1>
        <a:dk2><a:srgbClr val="111111"/></a:dk2><a:lt2><a:srgbClr val="FFF6E5"/></a:lt2><a:accent1><a:srgbClr val="FFD23F"/></a:accent1>
        <a:accent2><a:srgbClr val="FF7AAC"/></a:accent2><a:accent3><a:srgbClr val="5FB8FF"/></a:accent3><a:accent4><a:srgbClr val="3DDC97"/></a:accent4>
        <a:accent5><a:srgbClr val="FF8A3D"/></a:accent5><a:accent6><a:srgbClr val="C9B8FF"/></a:accent6><a:hlink><a:srgbClr val="0563C1"/></a:hlink>
        <a:folHlink><a:srgbClr val="954F72"/></a:folHlink></a:clrScheme>
        <a:fontScheme name="Swivel"><a:majorFont><a:latin typeface="Calibri"/><a:ea typeface=""/><a:cs typeface=""/></a:majorFont>
        <a:minorFont><a:latin typeface="Calibri"/><a:ea typeface=""/><a:cs typeface=""/></a:minorFont></a:fontScheme>
        <a:fmtScheme name="Swivel"><a:fillStyleLst><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:fillStyleLst>
        <a:lnStyleLst><a:ln w="6350"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln><a:ln w="12700"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln><a:ln w="19050"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln></a:lnStyleLst>
        <a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>
        <a:bgFillStyleLst><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:bgFillStyleLst></a:fmtScheme>
        </a:themeElements></a:theme>
        """;

    // ---- Word (basic rebuild) ----------------------------------------------------------------------

    private enum Block { Heading1, Heading2, Body }

    /// <summary>
    /// Rebuilds a PDF as a Word document: lines join into paragraphs in reading order, larger type
    /// becomes headings, images sit where they appear, and each PDF page starts a new Word page.
    /// Layout such as columns, tables and text boxes is flattened.
    /// </summary>
    public static void ToDocx(string pdfPath, string output, IProgress<double> progress, CancellationToken ct)
    {
        byte[] pdf = File.ReadAllBytes(pdfPath);
        string name = Path.GetFileName(pdfPath);
        int pages = Pdfium.PageCount(pdf, name);
        var content = new List<Pdfium.PageContent>();
        for (int i = 0; i < pages; i++)
        {
            ct.ThrowIfCancellationRequested();
            content.Add(Pdfium.ReadPage(pdf, name, i));
            progress.Report((i + 1.0) / pages * 0.5);
        }

        var allLines = content.SelectMany(p => p.Lines).ToArray();
        double body = allLines.Length == 0 ? 11 : allLines.Select(l => l.FontSize).Order().ElementAt(allLines.Length / 2);
        string font = allLines.GroupBy(l => l.Font).Where(g => g.Key.Length > 0).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key ?? "Calibri";

        using var package = WordprocessingDocument.Create(output, DocumentFormat.OpenXml.WordprocessingDocumentType.Document);
        var main = package.AddMainDocumentPart();
        Feed(main.AddNewPart<StyleDefinitionsPart>(), Styles(font, body));

        var xml = new StringBuilder();
        int pictureId = 1;
        for (int p = 0; p < content.Count; p++)
        {
            ct.ThrowIfCancellationRequested();
            if (p > 0) xml.Append("<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>");

            // Text paragraphs and pictures, interleaved by where they start on the page.
            var items = Paragraphs(content[p].Lines, body).Select(x => (x.Top, Xml: x.Xml))
                .Concat(content[p].Images.Select(image =>
                {
                    var part = main.AddImagePart(ImagePartType.Png);
                    using (var png = new MemoryStream(Png(image.Bgra, image.PixelWidth, image.PixelHeight))) part.FeedData(png);
                    long cx = (long)((image.Right - image.Left) * EmuPerPoint), cy = (long)((image.Bottom - image.Top) * EmuPerPoint);
                    if (cx > WordTextWidth) (cx, cy) = (WordTextWidth, cy * WordTextWidth / cx);
                    return (image.Top, Xml: InlinePicture(pictureId++, main.GetIdOfPart(part), cx, cy));
                }))
                .OrderBy(x => x.Top);
            foreach (var item in items) xml.Append(item.Xml);
            progress.Report(0.5 + (p + 1.0) / content.Count * 0.45);
        }
        if (xml.Length == 0) throw new JobException($"{name} has no text Swivel can read (it may be scanned).");

        var page = content.Count > 0 ? content[0] : null;
        int pageW = (int)Math.Round((page?.Width ?? 612) * 20), pageH = (int)Math.Round((page?.Height ?? 792) * 20);
        Feed(main, $"""
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture">
            <w:body>{xml}<w:sectPr><w:pgSz w:w="{pageW}" w:h="{pageH}"/><w:pgMar w:top="1440" w:right="1440" w:bottom="1440" w:left="1440" w:header="720" w:footer="720" w:gutter="0"/></w:sectPr></w:body></w:document>
            """);
    }

    /// <summary>Groups a page's lines into headings and paragraphs.</summary>
    private static IEnumerable<(double Top, string Xml)> Paragraphs(IReadOnlyList<Pdfium.TextLine> lines, double body)
    {
        if (lines.Count == 0) yield break;
        double typicalWidth = lines.Where(l => Kind(l, body) == Block.Body).Select(l => l.Width).DefaultIfEmpty(0).Max();

        var text = new StringBuilder();
        Pdfium.TextLine? first = null, previous = null;
        foreach (var line in lines)
        {
            if (previous is not null && !Continues(previous, line, body, typicalWidth))
            {
                yield return (first!.Top, ParagraphXml(Kind(first, body), text.ToString(), first.Bold, first.Italic));
                text.Clear();
                first = null;
            }
            first ??= line;
            if (text.Length > 0)
            {
                // Rejoin words hyphenated across a line break.
                if (text[^1] == '-' && line.Text.Length > 0 && char.IsLower(line.Text[0])) text.Length--;
                else text.Append(' ');
            }
            text.Append(line.Text);
            previous = line;
        }
        if (first is not null) yield return (first.Top, ParagraphXml(Kind(first, body), text.ToString(), first.Bold, first.Italic));
    }

    private static Block Kind(Pdfium.TextLine line, double body) =>
        line.FontSize >= body * 1.6 ? Block.Heading1 : line.FontSize >= body * 1.2 ? Block.Heading2 : Block.Body;

    /// <summary>True when <paramref name="next"/> carries on the paragraph that <paramref name="previous"/> is in.</summary>
    private static bool Continues(Pdfium.TextLine previous, Pdfium.TextLine next, double body, double typicalWidth)
    {
        if (Kind(previous, body) != Kind(next, body) || Kind(next, body) != Block.Body) return false;
        if (previous.Bold != next.Bold) return false;
        double gap = next.Top - previous.Bottom;
        if (gap < -2 || gap > previous.Height * 0.9) return false;          // jumped up (new column) or a blank line
        if (Math.Abs(next.Left - previous.Left) > body * 2.5) return false;  // indented: a new paragraph or list item
        bool hyphenated = previous.Text.EndsWith('-') && next.Text.Length > 0 && char.IsLower(next.Text[0]);
        if (!hyphenated && previous.Width < typicalWidth * 0.75) return false; // a short line ends its paragraph
        if (next.Text.Length > 0 && "•▪◦‣-–*".Contains(next.Text[0])) return false;
        return true;
    }

    private static string ParagraphXml(Block kind, string text, bool bold, bool italic)
    {
        string style = kind switch { Block.Heading1 => "<w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr>", Block.Heading2 => "<w:pPr><w:pStyle w:val=\"Heading2\"/></w:pPr>", _ => "" };
        string runProps = kind == Block.Body && (bold || italic) ? $"<w:rPr>{(bold ? "<w:b/>" : "")}{(italic ? "<w:i/>" : "")}</w:rPr>" : "";
        return $"<w:p>{style}<w:r>{runProps}<w:t xml:space=\"preserve\">{Xml(text)}</w:t></w:r></w:p>";
    }

    private static string InlinePicture(int id, string relationship, long cx, long cy) => $"""
        <w:p><w:r><w:drawing><wp:inline distT="0" distB="0" distL="0" distR="0"><wp:extent cx="{cx}" cy="{cy}"/><wp:docPr id="{id}" name="Picture {id}"/>
        <wp:cNvGraphicFramePr><a:graphicFrameLocks noChangeAspect="1"/></wp:cNvGraphicFramePr>
        <a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:pic>
        <pic:nvPicPr><pic:cNvPr id="{id}" name="Picture {id}"/><pic:cNvPicPr/></pic:nvPicPr>
        <pic:blipFill><a:blip r:embed="{relationship}"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>
        <pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="{cx}" cy="{cy}"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr>
        </pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>
        """;

    private static string Styles(string font, double body)
    {
        int size = (int)Math.Clamp(Math.Round(body * 2), 16, 32); // half-points
        string f = Xml(font);
        return $"""
            <w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
            <w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii="{f}" w:hAnsi="{f}" w:cs="{f}"/><w:sz w:val="{size}"/><w:szCs w:val="{size}"/></w:rPr></w:rPrDefault>
            <w:pPrDefault><w:pPr><w:spacing w:after="160" w:line="259" w:lineRule="auto"/></w:pPr></w:pPrDefault></w:docDefaults>
            <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/><w:qFormat/></w:style>
            <w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/>
            <w:pPr><w:keepNext/><w:spacing w:before="360" w:after="120"/><w:outlineLvl w:val="0"/></w:pPr><w:rPr><w:b/><w:sz w:val="{size * 2}"/><w:szCs w:val="{size * 2}"/></w:rPr></w:style>
            <w:style w:type="paragraph" w:styleId="Heading2"><w:name w:val="heading 2"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/>
            <w:pPr><w:keepNext/><w:spacing w:before="240" w:after="80"/><w:outlineLvl w:val="1"/></w:pPr><w:rPr><w:b/><w:sz w:val="{(int)(size * 1.4)}"/><w:szCs w:val="{(int)(size * 1.4)}"/></w:rPr></w:style>
            </w:styles>
            """;
    }

    // ---- Shared bits -------------------------------------------------------------------------------

    private static string Typeface(string font, string prefix) =>
        font.Length == 0 ? "" : $"<{prefix}:latin typeface=\"{Xml(font)}\"/>";

    /// <summary>Escapes text for XML and drops control characters XML can't hold.</summary>
    private static string Xml(string text)
    {
        var clean = new StringBuilder(text.Length);
        foreach (char c in text)
            if (c is '\t' or '\n' or '\r' || (c >= 0x20 && c is not '￾' and not '￿')) clean.Append(c);
        return SecurityElement.Escape(clean.ToString()) ?? "";
    }

    private static void Feed(OpenXmlPart part, string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        part.FeedData(stream);
    }

    private static VipsImage Rgb(byte[] bgra, int width, int height, out VipsImage[] owned)
    {
        var image = VipsImage.NewFromMemory(bgra, width, height, 4, Enums.BandFormat.Uchar);
        var b = image[0]; var g = image[1]; var r = image[2]; var a = image[3];
        var joined = r.Bandjoin(g, b, a);
        owned = [image, b, g, r, a, joined];
        return joined;
    }

    private static byte[] Jpeg(byte[] bgra, int width, int height)
    {
        using var rgba = Rgb(bgra, width, height, out var owned).Copy(interpretation: Enums.Interpretation.Srgb);
        using var flat = rgba.Flatten(background: [255, 255, 255]);
        try { return flat.JpegsaveBuffer(q: 88, optimizeCoding: true); }
        finally { foreach (var image in owned) image.Dispose(); }
    }

    private static byte[] Png(byte[] bgra, int width, int height)
    {
        using var rgba = Rgb(bgra, width, height, out var owned).Copy(interpretation: Enums.Interpretation.Srgb);
        try { return rgba.PngsaveBuffer(compression: 6); }
        finally { foreach (var image in owned) image.Dispose(); }
    }
}
