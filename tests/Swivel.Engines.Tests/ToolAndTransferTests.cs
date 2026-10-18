using Swivel.Core.Catalog;
using Swivel.Core.History;
using Swivel.Core.Jobs;
using Swivel.Engines.Files;
using Swivel.Engines.Media;
using VipsImage = NetVips.Image;

namespace Swivel.Engines.Tests;

public class ToolAndTransferTests : IDisposable
{
    private readonly Fixtures _fixtures = new();

    private static Operation With(string id, params (string Key, string Value)[] parameters) =>
        Fixtures.Op(id).WithParameters(parameters.ToDictionary(p => p.Key, p => p.Value));

    [Fact]
    public async Task Resize_follows_the_panel()
    {
        string input = _fixtures.Write("big.png", 1600, 1200);

        var widthOnly = await Fixtures.Run(With("tool.resize", ("width", "800")), input);
        using (var a = VipsImage.NewFromFile(widthOnly.Outputs[0])) Assert.Equal((800, 600), (a.Width, a.Height));

        var both = await Fixtures.Run(With("tool.resize", ("width", "300"), ("height", "300")), input);
        using var b = VipsImage.NewFromFile(both.Outputs[0]);
        Assert.Equal((300, 300), (b.Width, b.Height));
    }

    [Theory]
    [InlineData("1:1", 900, 900)]
    [InlineData("16:9", 1200, 675)]
    public async Task Smart_crop_cuts_to_the_aspect(string aspect, int width, int height)
    {
        string input = _fixtures.Write("photo.jpg", 1200, 900);
        var result = await Fixtures.Run(With("tool.crop", ("aspect", aspect)), input);
        using var image = VipsImage.NewFromFile(Assert.Single(result.Outputs));
        Assert.Equal((width, height), (image.Width, image.Height));
    }

    [Fact]
    public async Task Trim_keeps_the_chosen_span()
    {
        string input = Path.Combine(_fixtures.Dir, "talk.wav");
        await Fixtures.Ffmpeg("-f", "lavfi", "-i", "sine=duration=10", input);

        var result = await Fixtures.Run(With("media.trim", ("start", "2"), ("end", "5.5")), input);

        var info = await MediaInfo.ProbeAsync(Assert.Single(result.Outputs), CancellationToken.None);
        Assert.InRange(info.Duration, 3.3, 3.7);
    }

    [Fact]
    public async Task Copy_to_leaves_the_original_and_names_clashes()
    {
        string input = _fixtures.Write("a.png");
        string target = Directory.CreateDirectory(Path.Combine(_fixtures.Dir, "Pinned")).FullName;
        File.WriteAllText(Path.Combine(target, "a.png"), "already here");

        var result = await Fixtures.Run(With("action.copy-to", ("target", target)), input);

        Assert.Equal(Path.Combine(target, "a (1).png"), Assert.Single(result.Outputs));
        Assert.True(File.Exists(input));
        Assert.Equal(Fixtures.Hash(input), Fixtures.Hash(result.Outputs[0]));
    }

    [Fact]
    public async Task Move_to_moves_folders_and_undo_puts_them_back()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_fixtures.Dir, "Album")).FullName;
        File.WriteAllText(Path.Combine(folder, "song.txt"), "la");
        string target = Directory.CreateDirectory(Path.Combine(_fixtures.Dir, "Archive")).FullName;
        var history = new HistoryStore(Path.Combine(_fixtures.Dir, "h.db"));

        var result = await Fixtures.Run(With("action.move-to", ("target", target)), folder);
        var entry = history.Record(result);

        string moved = Assert.Single(result.Outputs);
        Assert.Equal(Path.Combine(target, "Album"), moved);
        Assert.False(Directory.Exists(folder));
        Assert.Equal(UndoResult.Done, history.Undo(entry.GroupId, new ThrowingRecycler()));
        Assert.True(File.Exists(Path.Combine(folder, "song.txt")));
        Assert.False(Directory.Exists(moved));
    }

    [Fact]
    public async Task Failed_move_puts_the_original_back()
    {
        string input = _fixtures.Write("keep.png");
        string before = Fixtures.Hash(input);
        string target = Directory.CreateDirectory(Path.Combine(_fixtures.Dir, "Out")).FullName;
        var op = With("action.move-to", ("target", target));

        // An engine that moves the input to the temp path and then fails, as a crash mid-job would.
        var failing = new FailingMove();
        await using var queue = new JobQueue([failing], Path.GetDirectoryName!);
        var result = await queue.Submit(op with { Engine = failing.Name }, [input]).Completion;

        Assert.Single(result.Failures);
        Assert.True(File.Exists(input));
        Assert.Equal(before, Fixtures.Hash(input));
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [Fact]
    public async Task A_folder_cannot_move_into_itself()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_fixtures.Dir, "Self")).FullName;
        string inner = Directory.CreateDirectory(Path.Combine(folder, "Inner")).FullName;
        var result = await Fixtures.Run(With("action.move-to", ("target", inner)), folder);
        Assert.Equal("A folder can't go inside itself.", Assert.Single(result.Failures).Message);
        Assert.True(Directory.Exists(inner));
    }

    [Fact]
    public void Pinned_folders_and_presets_become_wedges()
    {
        var settings = new Core.Settings.AppSettings
        {
            PinnedFolders = [@"D:\Photos"],
            Presets = [new("blog", "Blog", "1200 px", "image.webp", new Dictionary<string, string> { ["maxSize"] = "1200" })],
        };
        var catalog = OperationCatalog.FromSettings(settings);

        var preset = catalog.Find("preset.user.blog")!;
        Assert.Equal(("BLOG", "1200", ".webp"), (preset.Label, preset.Parameter("maxSize", ""), preset.OutputExtension));
        Assert.Equal(@"D:\Photos", catalog.Find("action.move-to:0")!.Parameter("target", ""));
        Assert.Equal("Photos", catalog.Find("action.copy-to:0")!.Hint);
    }

    private sealed class ThrowingRecycler : IRecycler
    {
        public void Recycle(IReadOnlyList<string> paths) => throw new InvalidOperationException("Move undo must not recycle.");
    }

    private sealed class FailingMove : IEngine
    {
        public string Name => "failing-move";

        public Task RunAsync(Job job, IProgress<double> progress, CancellationToken ct)
        {
            File.Move(job.Inputs[0], job.TempPath!);
            throw new JobException("Disk vanished.");
        }
    }

    public void Dispose() => _fixtures.Dispose();
}
