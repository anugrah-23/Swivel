using Swivel.Core.Catalog;
using Swivel.Core.Files;
using Swivel.Core.Jobs;

namespace Swivel.Engines.Files;

/// <summary>
/// Copy to and Move to. The item lands on the job's temp path in the target folder and the queue
/// renames it into place; if that fails, the queue puts a moved original back where it was.
/// </summary>
public sealed class TransferEngine : IEngine
{
    public string Name => Operation.Engines.Transfer;

    public Task RunAsync(Job job, IProgress<double> progress, CancellationToken ct) => Task.Run(() =>
    {
        string input = Path.GetFullPath(job.Inputs[0]).TrimEnd(Path.DirectorySeparatorChar);
        if (!FileOps.Exists(input)) throw new JobException($"{Path.GetFileName(input)} isn't there any more.");
        string target = Path.GetDirectoryName(job.OutputPath!)!;
        if (Directory.Exists(input) && (target + Path.DirectorySeparatorChar).StartsWith(input + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new JobException("A folder can't go inside itself.");

        long total = Math.Max(1, FileOps.Size(input)), done = 0;
        void Copied(long bytes) => progress.Report(Math.Min(0.99, (done += bytes) / (double)total));

        if (job.Operation.MovesInput) FileOps.Move(input, job.TempPath!, ct, Copied);
        else FileOps.Copy(input, job.TempPath!, ct, Copied);
        progress.Report(1);
    }, ct);
}
