using Swivel.Core.Catalog;

namespace Swivel.Core.Jobs;

/// <summary>
/// One unit of engine work. Engines write to <see cref="TempPath"/>; the queue renames it to
/// <see cref="OutputPath"/> only after the engine succeeds, so a failed job leaves nothing behind.
/// </summary>
public sealed record Job(Guid Id, Guid GroupId, Operation Operation, IReadOnlyList<string> Inputs, string? OutputPath)
{
    public string? TempPath => OutputPath is null ? null : OutputPath + OutputNamer.TempSuffix;
}

public interface IEngine
{
    string Name { get; }

    /// <summary>Runs the job, writing to <see cref="Job.TempPath"/>. Throws <see cref="JobException"/> with a user-facing reason.</summary>
    Task RunAsync(Job job, IProgress<double> progress, CancellationToken cancellationToken);
}

/// <summary>A failure whose message is safe to show the user ("Couldn't crack tickets.pdf").</summary>
public class JobException(string userMessage, Exception? inner = null) : Exception(userMessage, inner)
{
    /// <summary>Where the user can fix it, such as a Store page for a missing codec.</summary>
    public virtual string? HelpUrl => null;
}

public sealed record JobFailure(string Input, string Message, string? HelpUrl = null, string? Detail = null);

public sealed record JobGroupResult(
    Guid GroupId,
    Operation Operation,
    IReadOnlyList<string> Inputs,
    IReadOnlyList<string> Outputs,
    IReadOnlyList<JobFailure> Failures,
    bool Cancelled,
    DateTimeOffset Started,
    DateTimeOffset Finished)
{
    public bool Succeeded => !Cancelled && Failures.Count == 0;

    /// <summary>Which input each output came from (for a combined job, the first input).</summary>
    public IReadOnlyDictionary<string, string> Sources { get; init; } = new Dictionary<string, string>();
}
