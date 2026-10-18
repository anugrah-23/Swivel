using System.Security.Cryptography;
using Swivel.Core.Catalog;
using Swivel.Core.Files;
using Swivel.Core.Jobs;

namespace Swivel.Core.Tests;

public class JobQueueTests
{
    private static readonly Operation Upper = new("test.upper", WheelMode.Convert, "UP", "Loud",
        new HashSet<FileKind> { FileKind.Txt }, ".up", "fake");

    private sealed class FakeEngine(Func<Job, CancellationToken, Task>? body = null) : IEngine
    {
        public string Name => "fake";

        public async Task RunAsync(Job job, IProgress<double> progress, CancellationToken ct)
        {
            if (body != null) { await body(job, ct); return; }
            progress.Report(0.5);
            await File.WriteAllTextAsync(job.TempPath!, (await File.ReadAllTextAsync(job.Inputs[0], ct)).ToUpperInvariant(), ct);
        }
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public async Task Writes_output_next_to_the_original_and_never_touches_it()
    {
        using var dir = new TempDir();
        string input = dir.File("a.txt", "hello"u8.ToArray());
        string before = Hash(input);
        await using var queue = new JobQueue([new FakeEngine()], Path.GetDirectoryName!);

        var result = await queue.Submit(Upper, [input]).Completion;

        Assert.True(result.Succeeded);
        Assert.Equal(Path.Combine(dir.Path, "a.up"), Assert.Single(result.Outputs));
        Assert.Equal("HELLO", File.ReadAllText(result.Outputs[0]));
        Assert.Equal(before, Hash(input));
        Assert.Empty(Directory.GetFiles(dir.Path, "*" + OutputNamer.TempSuffix));
    }

    [Fact]
    public async Task Name_clashes_become_numbered()
    {
        using var dir = new TempDir();
        string input = dir.File("a.txt", "x"u8.ToArray());
        dir.File("a.up");
        await using var queue = new JobQueue([new FakeEngine()], Path.GetDirectoryName!);

        var result = await queue.Submit(Upper, [input]).Completion;

        Assert.Equal(Path.Combine(dir.Path, "a (1).up"), Assert.Single(result.Outputs));
    }

    [Fact]
    public async Task Same_format_output_never_overwrites_the_input()
    {
        using var dir = new TempDir();
        string input = dir.File("a.txt", "keep me"u8.ToArray());
        var sameFormat = Upper with { OutputExtension = null };
        await using var queue = new JobQueue([new FakeEngine()], Path.GetDirectoryName!);

        var result = await queue.Submit(sameFormat, [input]).Completion;

        Assert.Equal(Path.Combine(dir.Path, "a (1).txt"), Assert.Single(result.Outputs));
        Assert.Equal("keep me", File.ReadAllText(input));
    }

    [Fact]
    public async Task A_batch_is_one_group_with_unique_names()
    {
        using var dir = new TempDir();
        var inputs = Enumerable.Range(0, 20).Select(i => dir.File($"f{i}.txt", [(byte)'a'])).ToArray();
        await using var queue = new JobQueue([new FakeEngine()], _ => dir.Path, lightWorkers: 4);

        var group = queue.Submit(Upper, inputs);
        var result = await group.Completion;

        Assert.Equal(20, group.JobCount);
        Assert.Equal(20, result.Outputs.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(1, group.Progress);
    }

    [Fact]
    public async Task Failure_leaves_nothing_behind()
    {
        using var dir = new TempDir();
        string input = dir.File("a.txt");
        var engine = new FakeEngine(async (job, ct) =>
        {
            await File.WriteAllTextAsync(job.TempPath!, "partial", ct);
            throw new JobException("Couldn't crack a.txt.");
        });
        await using var queue = new JobQueue([engine], Path.GetDirectoryName!);

        var result = await queue.Submit(Upper, [input]).Completion;

        Assert.Empty(result.Outputs);
        Assert.Equal("Couldn't crack a.txt.", Assert.Single(result.Failures).Message);
        Assert.Equal([input], Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task Cancel_leaves_nothing_behind()
    {
        using var dir = new TempDir();
        string input = dir.File("a.txt");
        var started = new TaskCompletionSource();
        var engine = new FakeEngine(async (job, ct) =>
        {
            await File.WriteAllTextAsync(job.TempPath!, "partial", CancellationToken.None);
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        });
        await using var queue = new JobQueue([engine], Path.GetDirectoryName!);

        var group = queue.Submit(Upper, [input]);
        await started.Task;
        group.Cancel();
        var result = await group.Completion;

        Assert.True(result.Cancelled);
        Assert.Equal([input], Directory.GetFiles(dir.Path));
    }
}
