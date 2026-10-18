using System.Threading.Channels;
using Swivel.Core.Catalog;
using Swivel.Core.Files;

namespace Swivel.Core.Jobs;

/// <summary>
/// Background job runner: <c>ProcessorCount - 1</c> light workers and one or two heavy slots.
/// Jobs survive the wheel closing. Each output is written to a temp name and renamed into place
/// only on success, and the namer never hands out an existing path, so originals are never touched.
/// </summary>
public sealed class JobQueue : IAsyncDisposable
{
    private readonly record struct WorkItem(JobGroup Group, int Index, IReadOnlyList<string> Inputs);

    private readonly Channel<WorkItem> _light = Channel.CreateUnbounded<WorkItem>();
    private readonly Channel<WorkItem> _heavy = Channel.CreateUnbounded<WorkItem>();
    private readonly Dictionary<string, IEngine> _engines;
    private readonly Func<string, string> _outputDirectory;
    private readonly OutputNamer _namer = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task[] _workers;

    /// <param name="engines">Engines keyed by <see cref="Operation.Engine"/>.</param>
    /// <param name="outputDirectory">Where the output for a given input goes.</param>
    public JobQueue(IEnumerable<IEngine> engines, Func<string, string> outputDirectory, int? lightWorkers = null, int? heavyWorkers = null)
    {
        _engines = engines.ToDictionary(e => e.Name);
        _outputDirectory = outputDirectory;
        int light = lightWorkers ?? Math.Max(1, Environment.ProcessorCount - 1);
        int heavy = heavyWorkers ?? (Environment.ProcessorCount >= 8 ? 2 : 1);
        _workers =
        [
            .. Enumerable.Range(0, light).Select(_ => Task.Run(() => WorkAsync(_light.Reader))),
            .. Enumerable.Range(0, heavy).Select(_ => Task.Run(() => WorkAsync(_heavy.Reader))),
        ];
    }

    /// <summary>Raised on a worker thread when every job in a group has finished.</summary>
    public event Action<JobGroupResult>? GroupCompleted;

    public JobGroup Submit(Operation operation, IReadOnlyList<string> inputs)
    {
        if (inputs.Count == 0) throw new ArgumentException("A job needs at least one input.", nameof(inputs));
        if (!_engines.ContainsKey(operation.Engine)) throw new InvalidOperationException($"No engine named '{operation.Engine}'.");

        if (!operation.Accepts(inputs.Count))
            throw new ArgumentException($"{operation.Label} takes {operation.MinInputs} to {operation.MaxInputs} files.", nameof(inputs));

        // Actions and combining jobs (merge, archive) act on the whole selection; conversions run per file.
        IReadOnlyList<IReadOnlyList<string>> batches = operation.ProducesNoOutput || operation.CombinesInputs
            ? [inputs]
            : inputs.Select(i => (IReadOnlyList<string>)[i]).ToArray();

        var group = new JobGroup(operation, inputs, batches.Count);
        var channel = operation.Heavy ? _heavy : _light;
        for (int i = 0; i < batches.Count; i++)
            channel.Writer.TryWrite(new WorkItem(group, i, batches[i]));
        return group;
    }

    private async Task WorkAsync(ChannelReader<WorkItem> reader)
    {
        try
        {
            await foreach (var item in reader.ReadAllAsync(_shutdown.Token))
                await RunAsync(item);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task RunAsync(WorkItem item)
    {
        var (group, index, inputs) = item;
        var operation = group.Operation;
        string? output = null;
        string? temp = null;
        JobFailure? failure = null;
        bool cancelled = false;

        try
        {
            group.CancellationToken.ThrowIfCancellationRequested();

            if (!operation.ProducesNoOutput)
            {
                string input = inputs[0];
                // Copy to / Move to name their own folder; everything else follows the output rule.
                string directory = operation.Parameter("target", "") is { Length: > 0 } target ? target : _outputDirectory(input);
                Directory.CreateDirectory(directory);
                bool folderInput = Directory.Exists(input) && !operation.CombinesInputs;
                string extension = operation.OutputIsFolder ? ""
                    : operation.OutputExtension ?? (folderInput ? "" : Path.GetExtension(input));
                string stem = OutputNamer.StemFor(inputs, operation.CombinesInputs) + operation.OutputSuffix;
                output = _namer.Reserve(directory, stem, extension);
            }

            var job = new Job(Guid.NewGuid(), group.Id, operation, inputs, output);
            temp = job.TempPath;
            var progress = new Progress(group, index);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(group.CancellationToken, _shutdown.Token);
            await _engines[operation.Engine].RunAsync(job, progress, linked.Token);

            if (output != null && temp != null)
            {
                // Folders and files are both renamed into place in one step.
                if (Directory.Exists(temp)) Directory.Move(temp, output);
                else if (File.Exists(temp)) File.Move(temp, output, overwrite: false);
                else throw new JobException("The engine finished without writing anything.");
                temp = null;
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            // Engines explain known failures; anything unexpected reads the same way, with the detail logged.
            string reason = ex is JobException ? ex.Message : $"Couldn't crack {Path.GetFileName(inputs[0].TrimEnd(Path.DirectorySeparatorChar))}.";
            string detail = (ex is JobException { InnerException: { } inner } ? inner : ex).ToString();
            failure = new JobFailure(inputs[0], reason, (ex as JobException)?.HelpUrl, detail);
        }
        finally
        {
            if (temp != null)
            {
                // A move that didn't finish puts the original back where it was; anything else is scratch.
                if (operation.MovesInput && FileOps.Exists(temp) && !FileOps.Exists(inputs[0])) TryRestore(temp, inputs[0]);
                else TryDelete(temp);
            }
            if (output != null) _namer.Release(output);
        }

        bool produced = output != null && failure == null && !cancelled;
        var result = group.Finish(index, produced ? output : null, inputs[0], failure, cancelled);
        if (result != null) GroupCompleted?.Invoke(result);
    }

    private static void TryRestore(string temp, string original)
    {
        try { FileOps.Move(temp, original); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leave it under its temp name rather than delete the only copy.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _light.Writer.TryComplete();
        _heavy.Writer.TryComplete();
        _shutdown.Cancel();
        try { await Task.WhenAll(_workers); } catch (OperationCanceledException) { }
        _shutdown.Dispose();
    }

    private sealed class Progress(JobGroup group, int index) : IProgress<double>
    {
        public void Report(double value) => group.Report(index, value);
    }
}
