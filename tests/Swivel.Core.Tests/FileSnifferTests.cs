using System.Text;
using Swivel.Core.Files;

namespace Swivel.Core.Tests;

public class FileSnifferTests
{
    private static byte[] Ftyp(string major, params string[] compatible)
    {
        var body = new List<byte>();
        body.AddRange(Encoding.ASCII.GetBytes("ftyp" + major));
        body.AddRange(new byte[4]);
        foreach (var b in compatible) body.AddRange(Encoding.ASCII.GetBytes(b));
        int size = body.Count + 4;
        return [(byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size, .. body];
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, FileKind.Jpeg)]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, FileKind.Png)]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, FileKind.Gif)]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }, FileKind.Pdf)]
    [InlineData(new byte[] { 0x49, 0x49, 0x2A, 0x00 }, FileKind.Tiff)]
    [InlineData(new byte[] { 0x66, 0x4C, 0x61, 0x43 }, FileKind.Flac)]
    [InlineData(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }, FileKind.SevenZip)]
    public void Detects_magic_bytes(byte[] header, FileKind expected) =>
        Assert.Equal(expected, FileSniffer.Detect(header, FileKind.Unknown));

    [Fact]
    public void Magic_bytes_beat_a_wrong_extension()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        Assert.Equal(FileKind.Png, FileSniffer.Detect(png, FileKind.Jpeg));
    }

    [Fact]
    public void Riff_forms_are_told_apart()
    {
        Assert.Equal(FileKind.WebP, FileSniffer.Detect("RIFF\0\0\0\0WEBPVP8 "u8, FileKind.Unknown));
        Assert.Equal(FileKind.Wav, FileSniffer.Detect("RIFF\0\0\0\0WAVEfmt "u8, FileKind.Unknown));
    }

    [Fact]
    public void Heif_brands_are_told_apart()
    {
        Assert.Equal(FileKind.Heic, FileSniffer.Detect(Ftyp("heic", "mif1", "heic"), FileKind.Unknown));
        Assert.Equal(FileKind.Avif, FileSniffer.Detect(Ftyp("avif", "mif1", "avif"), FileKind.Unknown));
        Assert.Equal(FileKind.Avif, FileSniffer.Detect(Ftyp("mif1", "avif"), FileKind.Unknown));
        Assert.Equal(FileKind.Mp4, FileSniffer.Detect(Ftyp("isom", "mp41"), FileKind.Unknown));
        Assert.Equal(FileKind.Mov, FileSniffer.Detect(Ftyp("qt  "), FileKind.Unknown));
    }

    [Fact]
    public void Zip_containers_trust_the_office_extension()
    {
        var zip = "PK\x03\x04"u8;
        Assert.Equal(FileKind.Docx, FileSniffer.Detect(zip, FileKind.Docx));
        Assert.Equal(FileKind.Zip, FileSniffer.Detect(zip, FileKind.Unknown));
    }

    [Fact]
    public void Svg_is_found_in_text()
    {
        var svg = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?>\n<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        Assert.Equal(FileKind.Svg, FileSniffer.Detect(svg, FileKind.Unknown));
    }

    [Fact]
    public void Falls_back_to_extension_and_handles_folders()
    {
        using var dir = new TempDir();
        Assert.Equal(FileKind.Markdown, FileSniffer.Detect(dir.File("notes.md", "# hi"u8.ToArray())));
        Assert.Equal(FileKind.Folder, FileSniffer.Detect(dir.Path));
    }
}
