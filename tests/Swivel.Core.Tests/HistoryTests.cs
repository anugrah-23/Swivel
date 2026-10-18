using Swivel.Core.Catalog;
using Swivel.Core.History;
using Swivel.Core.Jobs;
using Swivel.Core.Settings;

namespace Swivel.Core.Tests;

public class HistoryTests
{
    private sealed class DeletingRecycler : IRecycler
    {
        public List<string> Recycled { get; } = [];

        public void Recycle(IReadOnlyList<string> paths)
        {
            Recycled.AddRange(paths);
            foreach (var p in paths) File.Delete(p);
        }
    }

    private static JobGroupResult Result(string input, string output) => new(
        Guid.NewGuid(), new OperationCatalog().Find("image.webp")!, [input], [output], [], false,
        DateTimeOffset.Now, DateTimeOffset.Now);

    [Fact]
    public void Undo_recycles_outputs_and_keeps_the_original()
    {
        using var dir = new TempDir();
        var store = new HistoryStore(Path.Combine(dir.Path, "history.db"));
        string input = dir.File("a.png", new byte[100]), output = dir.File("a.webp", new byte[40]);
        var entry = store.Record(Result(input, output));
        var recycler = new DeletingRecycler();

        Assert.Equal(100, entry.BytesBefore);
        Assert.Equal(40, entry.BytesAfter);
        Assert.True(store.CanUndo(entry.GroupId));
        Assert.Equal(UndoResult.Done, store.Undo(entry.GroupId, recycler));
        Assert.Equal([Path.GetFullPath(output)], recycler.Recycled);
        Assert.True(File.Exists(input));
        Assert.Equal(UndoResult.AlreadyUndone, store.Undo(entry.GroupId, recycler));
    }

    [Fact]
    public void Undo_refuses_once_an_output_was_edited()
    {
        using var dir = new TempDir();
        var store = new HistoryStore(Path.Combine(dir.Path, "history.db"));
        string input = dir.File("a.png"), output = dir.File("a.webp", new byte[40]);
        var entry = store.Record(Result(input, output));

        File.WriteAllBytes(output, new byte[41]);

        Assert.False(store.CanUndo(entry.GroupId));
        Assert.Equal(UndoResult.OutputsChanged, store.Undo(entry.GroupId, new DeletingRecycler()));
        Assert.True(File.Exists(output));
    }

    [Fact]
    public void Usage_counts_feed_the_ranking()
    {
        using var dir = new TempDir();
        var store = new HistoryStore(Path.Combine(dir.Path, "history.db"));
        store.Record(Result(dir.File("a.png"), dir.File("a.webp")));
        store.Record(Result(dir.File("b.png"), dir.File("b.webp")));

        Assert.Equal(2, store.UsageCounts()["image.webp"]);
        Assert.Equal(2, store.Recent(10).Count);
    }

    [Fact]
    public void Scratch_drops_go_to_the_fallback_folder()
    {
        string scratch = Path.Combine(Path.GetTempPath(), "Swivel", "drops");
        var rule = new OutputRule();
        Assert.Equal(@"C:\Users\x\Downloads",
            OutputLocator.DirectoryFor(Path.Combine(scratch, "abc", "photo.jpg"), rule, scratch, @"C:\Users\x\Downloads"));
        Assert.Equal(@"D:\Photos", OutputLocator.DirectoryFor(@"D:\Photos\a.jpg", rule, scratch, "fallback"));
        Assert.Equal(@"E:\Out", OutputLocator.DirectoryFor(@"D:\Photos\a.jpg",
            rule with { Location = OutputLocation.Folder, Folder = @"E:\Out" }, scratch, "fallback"));
    }
}
