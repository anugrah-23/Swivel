using System.Windows;
using System.Windows.Threading;
using Swivel.Core.Catalog;
using Swivel.Core.Jobs;
using Swivel.Native;

namespace Swivel.App.Actions;

/// <summary>Wedges that act on files rather than produce new ones. They run on the UI (STA) thread.</summary>
public sealed class ActionEngine(Dispatcher ui) : IEngine
{
    public string Name => Operation.Engines.Action;

    public Task RunAsync(Job job, IProgress<double> progress, CancellationToken cancellationToken) =>
        ui.InvokeAsync(() =>
        {
            switch (job.Operation.Id)
            {
                case "action.copy-path":
                    Clipboard.SetText(string.Join(Environment.NewLine, job.Inputs));
                    break;
                case "action.reveal":
                    ShellFiles.Reveal(job.Inputs);
                    break;
                default:
                    throw new JobException($"{job.Operation.Label} isn't built yet.");
            }
            progress.Report(1);
        }, DispatcherPriority.Normal, cancellationToken).Task;
}
