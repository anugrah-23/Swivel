using SharpCompress.Common;
using SharpCompress.Writers;
using Swivel.Core.Catalog;
using Swivel.Core.Files;

namespace Swivel.Engines.Tests;

public class ArchiveEngineTests : IDisposable
{
    private readonly Fixtures _fixtures = new();

    /// <summary>A folder "Holiday" with two files and a subfolder, plus a loose file beside it.</summary>
    private (string Folder, string Loose) Tree()
    {
        string folder = Path.Combine(_fixtures.Dir, "Holiday");
        Directory.CreateDirectory(Path.Combine(folder, "day 2"));
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "beach");
        File.WriteAllBytes(Path.Combine(folder, "day 2", "photo.bin"), Enumerable.Range(0, 50_000).Select(i => (byte)(i % 251)).ToArray());
        string loose = Path.Combine(_fixtures.Dir, "readme.md");
        File.WriteAllText(loose, "# hi");
        return (folder, loose);
    }

    private static Dictionary<string, string> Snapshot(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(root, p).Replace('\\', '/'), Fixtures.Hash);

    [Theory]
    [InlineData("archive.zip", FileKind.Zip)]
    [InlineData("archive.7z", FileKind.SevenZip)]
    [InlineData("archive.tar", FileKind.Tar)]
    public async Task Pack_then_unpack_round_trips(string op, FileKind kind)
    {
        var (folder, loose) = Tree();
        var expected = Snapshot(_fixtures.Dir);

        var packed = await Fixtures.Run(Fixtures.Op(op), folder, loose);
        Assert.True(packed.Succeeded, string.Join("; ", packed.Failures.Select(f => f.Detail)));
        string archive = Assert.Single(packed.Outputs);
        Assert.Equal(kind, FileSniffer.Detect(archive));
        Assert.Equal(Path.GetFileName(_fixtures.Dir), Path.GetFileNameWithoutExtension(archive));

        var unpacked = await Fixtures.Run(Fixtures.Op("archive.extract"), archive);
        Assert.True(unpacked.Succeeded, string.Join("; ", unpacked.Failures.Select(f => f.Detail)));
        var actual = Snapshot(Assert.Single(unpacked.Outputs));
        Assert.Equal(expected.OrderBy(k => k.Key), actual.OrderBy(k => k.Key));
    }

    [Fact]
    public async Task Gzip_wraps_one_file()
    {
        var (_, loose) = Tree();
        var result = await Fixtures.Run(Fixtures.Op("archive.gz"), loose);
        string gz = Assert.Single(result.Outputs);
        Assert.Equal("readme.gz", Path.GetFileName(gz));
        Assert.Equal(FileKind.Gzip, FileSniffer.Detect(gz));
        Assert.DoesNotContain(Fixtures.Catalog.For([FileKind.Folder], WheelMode.Tools), o => o.Id == "archive.gz");
    }

    [Fact]
    public async Task Single_top_folder_is_not_nested_twice()
    {
        var (folder, _) = Tree();
        var zip = await Fixtures.Run(Fixtures.Op("archive.zip"), folder);
        Directory.Delete(folder, recursive: true);

        var result = await Fixtures.Run(Fixtures.Op("archive.extract"), zip.Outputs[0]);

        string output = Assert.Single(result.Outputs);
        Assert.Equal(Path.Combine(_fixtures.Dir, "Holiday"), output);
        Assert.True(File.Exists(Path.Combine(output, "notes.txt")));
    }

    [Fact]
    public async Task Zip_slip_entries_are_refused()
    {
        string evil = Path.Combine(_fixtures.Dir, "evil.zip");
        using (var stream = File.Create(evil))
        using (var writer = WriterFactory.OpenWriter(stream, ArchiveType.Zip, WriterOptions.ForZip(CompressionType.Deflate)))
        {
            writer.Write("ok.txt", new MemoryStream("fine"u8.ToArray()), DateTime.Now);
            writer.Write("../../escaped.txt", new MemoryStream("bad"u8.ToArray()), DateTime.Now);
        }

        var result = await Fixtures.Run(Fixtures.Op("archive.extract"), evil);

        Assert.Contains("outside its folder", Assert.Single(result.Failures).Message);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_fixtures.Dir)!, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(_fixtures.Dir, "..", "..", "escaped.txt")));
        Assert.Equal([evil], Directory.GetFileSystemEntries(_fixtures.Dir));
    }

    [Fact]
    public void Folders_open_on_tools_with_archive_wedges()
    {
        Assert.Equal(WheelMode.Tools, Fixtures.Catalog.OpeningMode([FileKind.Folder]));
        var tools = Fixtures.Catalog.For([FileKind.Folder], WheelMode.Tools).Select(o => o.Id).ToArray();
        Assert.Contains("archive.zip", tools);
        Assert.Contains("archive.7z", tools);
    }

    public void Dispose() => _fixtures.Dispose();
}
