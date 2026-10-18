using Serilog;
using Swivel.App.Overlay;
using Swivel.App.Services;
using Swivel.App.Theme;
using Swivel.Core.Catalog;
using Swivel.Core.History;
using Swivel.Core.Jobs;
using Swivel.Engines.Infrastructure;
using Swivel.Native;

namespace Swivel.App.Feedback;

/// <summary>Turns drops into jobs with a progress pill, and finished jobs into toasts.</summary>
public sealed class JobFeedback
{
    private static readonly TimeSpan DoneLife = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan FailedLife = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan BlipLife = TimeSpan.FromSeconds(2.5);

    private readonly SwivelServices _services;
    private readonly ToastHost _toasts = new();
    private int _running;

    public JobFeedback(SwivelServices services)
    {
        _services = services;
        services.GroupFinished += OnFinished;
    }

    public void OnDrop(WheelDrop drop)
    {
        var group = _services.Submit(drop.Operation, drop.Files);
        if (drop.Operation.ProducesNoOutput) return;
        _ = new ProgressPill(group, drop.ScreenCentre, drop.Scale, _running++);
        group.Completion.ContinueWith(_ => Interlocked.Decrement(ref _running), TaskScheduler.Default);
    }

    private void OnFinished(JobGroupResult result, HistoryEntry? entry)
    {
        var op = result.Operation;

        if (op.ProducesNoOutput)
        {
            if (result.Failures.Count > 0) ShowFailure(result);
            else if (op.Id == "action.copy-path")
                _toasts.Show(new Toast(ToastKind.Info, "Copied.", Voice.Action(op, result.Inputs.Count), null, [], BlipLife));
            return;
        }

        if (result.Cancelled)
        {
            string body = result.Outputs.Count == 0
                ? "Nothing was written. " + Voice.Safe(result.Inputs.Count)
                : $"{result.Outputs.Count} finished before you stopped it.";
            _toasts.Show(new Toast(ToastKind.Info, "Stopped.", body, null, UndoOnly(entry), DoneLife));
            return;
        }

        if (result.Outputs.Count == 0)
        {
            ShowFailure(result);
            return;
        }

        string sizes = entry is { BytesBefore: > 0 } e ? $" · {Format.Size(e.BytesBefore)} → {Format.Size(e.BytesAfter)}" : "";
        if (result.Failures.Count == 0)
        {
            string what = result.Outputs.Count == 1 ? Path.GetFileName(result.Outputs[0]) : $"{result.Outputs.Count} files";
            var actions = new List<ToastAction>();
            if (result.Outputs.Count == 1) actions.Add(new ToastAction("Open", _ => { Toast.Launch(result.Outputs[0]); return true; }));
            actions.Add(new ToastAction("Show in folder", _ => { ShellFiles.Reveal(result.Outputs); return true; }));
            actions.AddRange(UndoOnly(entry));
            _toasts.Show(new Toast(ToastKind.Done, Voice.Success(result), what + sizes, null, actions, DoneLife));
        }
        else
        {
            var actions = new List<ToastAction>
            {
                new("Show in folder", _ => { ShellFiles.Reveal(result.Outputs); return true; }),
                Details(result),
            };
            actions.AddRange(UndoOnly(entry));
            _toasts.Show(new Toast(ToastKind.Done, "Mostly boom.", Voice.Partial(result) + sizes, null, actions, FailedLife));
        }
    }

    private void ShowFailure(JobGroupResult result)
    {
        var first = result.Failures[0];
        var actions = new List<ToastAction>();
        if (first.HelpUrl is { } url && url.StartsWith(PackMissingException.Scheme, StringComparison.Ordinal)
            && EnginePacks.Find(url[PackMissingException.Scheme.Length..]) is { } pack)
        {
            // A missing optional engine isn't really a failure: offer to fetch it, then retry.
            var retry = result.Failures.Select(f => f.Input).Distinct().ToArray();
            actions.Add(new ToastAction($"Get {pack.Name} ({Format.Size(pack.DownloadBytes)})", toast =>
            {
                _ = InstallPackAsync(pack, toast, result.Operation, retry);
                return false;
            }));
            actions.Add(new ToastAction("Not now", _ => true));
            _toasts.Show(new Toast(ToastKind.Info, $"One-time download: {pack.Name}", first.Message, null, actions, FailedLife));
            return;
        }

        if (first.HelpUrl is { } help) actions.Add(new ToastAction("Get the extension", _ => { Toast.Launch(help); return true; }));
        actions.Add(Details(result));
        actions.Add(new ToastAction("Dismiss", _ => true));
        string title = Voice.Failure(result);
        // Don't repeat the title when the reason is just the generic "Couldn't crack x."
        string body = first.Message == title ? Voice.Safe(result.Inputs.Count) : $"{first.Message} {Voice.Safe(result.Inputs.Count)}";
        _toasts.Show(new Toast(ToastKind.Failed, title, body, null, actions, FailedLife));
    }

    private async Task InstallPackAsync(EnginePack pack, Toast toast, Operation operation, IReadOnlyList<string> inputs)
    {
        using var cancel = new CancellationTokenSource();
        var stop = new ToastAction("Cancel", _ => { cancel.Cancel(); return false; });
        toast.Update(ToastKind.Info, $"Fetching {pack.Name}…", "0%", [stop], life: TimeSpan.FromHours(1));
        var progress = new Progress<double>(p => toast.Update(ToastKind.Info, $"Fetching {pack.Name}…",
            p < 0.9 ? $"{p / 0.9:P0} of {Format.Size(pack.DownloadBytes)}" : "Unpacking…", [stop], life: TimeSpan.FromHours(1)));
        try
        {
            await EnginePacks.InstallAsync(pack, progress, cancel.Token);
            Log.Information("Installed engine pack {Pack}", pack.Id);
            toast.Update(ToastKind.Done, $"Got {pack.Name}.", "Running your job again now.", life: BlipLife);
            _services.Submit(operation, inputs);
        }
        catch (OperationCanceledException)
        {
            toast.Update(ToastKind.Info, "Download cancelled.", "Nothing was installed.", life: BlipLife);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Installing pack {Pack} failed", pack.Id);
            toast.Update(ToastKind.Failed, $"Couldn't get {pack.Name}.", ex is JobException ? ex.Message : "Check your connection and try again.", life: DoneLife);
        }
    }

    private static ToastAction Details(JobGroupResult result) => new("Details", toast =>
    {
        var lines = result.Failures.Take(6).Select(f => $"{Path.GetFileName(f.Input)}: {f.Message}").ToList();
        if (result.Failures.Count > 6) lines.Add($"…and {result.Failures.Count - 6} more. The log has the rest.");
        toast.ShowDetail(string.Join(Environment.NewLine, lines));
        return false;
    });

    private IEnumerable<ToastAction> UndoOnly(HistoryEntry? entry)
    {
        if (entry is null || entry.Outputs.Count == 0) yield break;
        yield return new ToastAction("Undo", toast =>
        {
            var outcome = _services.Undo(entry.GroupId);
            switch (outcome)
            {
                case UndoResult.Done:
                    toast.Update(ToastKind.Info, "Undone.", "Sent to the Recycle Bin. Originals untouched.", life: BlipLife);
                    break;
                case UndoResult.OutputsChanged:
                    toast.Update(ToastKind.Failed, "Can't undo that.", "The output changed since Swivel made it, so it stays put.", life: DoneLife);
                    break;
                default:
                    toast.Update(ToastKind.Info, "Nothing to undo.", "Those files are already gone.", life: BlipLife);
                    break;
            }
            return false;
        });
    }
}
