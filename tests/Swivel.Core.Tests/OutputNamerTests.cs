using Swivel.Core.Jobs;

namespace Swivel.Core.Tests;

public class OutputNamerTests
{
    [Fact]
    public void Stems_follow_the_input()
    {
        using var dir = new TempDir();
        string folder = Path.Combine(dir.Path, "My.Photos");
        Directory.CreateDirectory(folder);

        Assert.Equal("report", OutputNamer.StemFor([dir.File("report.pdf")], combined: false));
        Assert.Equal("backup", OutputNamer.StemFor([dir.File("backup.tar.gz")], combined: false));
        Assert.Equal("My.Photos", OutputNamer.StemFor([folder], combined: true));
    }

    [Fact]
    public void Combined_outputs_take_the_shared_folder_name()
    {
        using var dir = new TempDir();
        string a = dir.File("a.pdf"), b = dir.File("b.pdf");
        Assert.Equal(Path.GetFileName(dir.Path), OutputNamer.StemFor([a, b], combined: true));
    }

    [Fact]
    public async Task Folder_outputs_rename_into_place_and_undo_as_one()
    {
        using var dir = new TempDir();
        string input = dir.File("pack.txt");
        var op = new Catalog.Operation("test.folder", Catalog.WheelMode.Tools, "Split", "Pages",
            new HashSet<Files.FileKind> { Files.FileKind.Txt }, null, "fake") { OutputIsFolder = true, OutputSuffix = " parts" };
        var engine = new FolderEngine();
        await using var queue = new JobQueue([engine], Path.GetDirectoryName!);

        var result = await queue.Submit(op, [input]).Completion;

        string folder = Assert.Single(result.Outputs);
        Assert.Equal(Path.Combine(dir.Path, "pack parts"), folder);
        Assert.Equal(2, Directory.GetFiles(folder).Length);

        var history = new History.HistoryStore(Path.Combine(dir.Path, "h.db"));
        var entry = history.Record(result);
        Assert.True(history.CanUndo(entry.GroupId));
        File.WriteAllText(Path.Combine(folder, "extra.txt"), "edited");
        Assert.False(history.CanUndo(entry.GroupId));
    }

    private sealed class FolderEngine : IEngine
    {
        public string Name => "fake";

        public Task RunAsync(Job job, IProgress<double> progress, CancellationToken ct)
        {
            Directory.CreateDirectory(job.TempPath!);
            File.WriteAllText(Path.Combine(job.TempPath!, "1.txt"), "one");
            File.WriteAllText(Path.Combine(job.TempPath!, "2.txt"), "two");
            return Task.CompletedTask;
        }
    }
}
