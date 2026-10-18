using Swivel.Core.Catalog;

namespace Swivel.Core.Jobs;

/// <summary>A batch: one pill, one toast, one undo, however many jobs it holds.</summary>
public sealed class JobGroup
{
    private readonly Lock _lock = new();
    private readonly double[] _progress;
    private readonly List<string> _outputs = [];
    private readonly List<JobFailure> _failures = [];
    private readonly Dictionary<string, string> _sources = [];
    private readonly TaskCompletionSource<JobGroupResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cancel = new();
    private int _remaining;
    private bool _anyCancelled;

    internal JobGroup(Operation operation, IReadOnlyList<string> inputs, int jobCount)
    {
        Operation = operation;
        Inputs = inputs;
        JobCount = jobCount;
        _remaining = jobCount;
        _progress = new double[jobCount];
    }

    public Guid Id { get; } = Guid.NewGuid();
    public Operation Operation { get; }
    public IReadOnlyList<string> Inputs { get; }
    public int JobCount { get; }
    public DateTimeOffset Started { get; } = DateTimeOffset.Now;

    public int Completed { get { lock (_lock) return JobCount - _remaining; } }

    public double Progress { get { lock (_lock) return _progress.Average(); } }

    /// <summary>Raised from worker threads with the group's overall fraction, 0 to 1.</summary>
    public event Action<JobGroup>? ProgressChanged;

    public Task<JobGroupResult> Completion => _completion.Task;

    public CancellationToken CancellationToken => _cancel.Token;

    public void Cancel() => _cancel.Cancel();

    internal void Report(int index, double fraction)
    {
        lock (_lock) _progress[index] = Math.Clamp(fraction, 0, 1);
        ProgressChanged?.Invoke(this);
    }

    internal JobGroupResult? Finish(int index, string? output, string source, JobFailure? failure, bool cancelled)
    {
        JobGroupResult? result = null;
        lock (_lock)
        {
            _progress[index] = 1;
            if (output != null)
            {
                _outputs.Add(output);
                _sources[output] = source;
            }
            if (failure != null) _failures.Add(failure);
            _anyCancelled |= cancelled;
            if (--_remaining == 0)
                result = new JobGroupResult(Id, Operation, Inputs, [.. _outputs], [.. _failures], _anyCancelled, Started, DateTimeOffset.Now)
                {
                    Sources = new Dictionary<string, string>(_sources),
                };
        }
        ProgressChanged?.Invoke(this);
        if (result != null) _completion.TrySetResult(result);
        return result;
    }
}
