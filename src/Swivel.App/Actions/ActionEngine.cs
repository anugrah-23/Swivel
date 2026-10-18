using System.Collections.Specialized;
using System.Diagnostics;
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
            var paths = job.Inputs;
            switch (job.Operation.Id)
            {
                case "action.copy-path":
                    Clipboard.SetText(string.Join(Environment.NewLine, paths));
                    break;
                case "action.copy-file":
                    var list = new StringCollection();
                    list.AddRange([.. paths]);
                    Clipboard.SetFileDropList(list);
                    break;
                case "action.reveal":
                    ShellFiles.Reveal(paths);
                    break;
                case "action.share":
                    Share(paths);
                    break;
                case "action.float":
                    new FloatWindow(paths).Show();
                    break;
                case "action.terminal":
                    OpenTerminal(paths[0]);
                    break;
                default:
                    throw new JobException($"{job.Operation.Label} isn't built yet.");
            }
            progress.Report(1);
        }, DispatcherPriority.Normal, cancellationToken).Task;

    private static void Share(IReadOnlyList<string> paths)
    {
        // The share sheet needs a foreground owner; the host closes once focus comes back to it.
        var host = ForegroundHost.Open();
        bool deactivated = false;
        host.Deactivated += (_, _) => deactivated = true;
        host.Activated += (_, _) => { if (deactivated) host.Close(); };
        var expire = new DispatcherTimer { Interval = TimeSpan.FromMinutes(3) };
        expire.Tick += (_, _) => { expire.Stop(); if (host.IsLoaded) host.Close(); };
        expire.Start();

        string folder = Path.GetDirectoryName(Path.GetFullPath(paths[0]))!;
        var sameFolder = paths.Where(p => string.Equals(Path.GetDirectoryName(Path.GetFullPath(p)), folder, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (!ShellShare.Share(sameFolder, host.Handle))
        {
            host.Close();
            throw new JobException("Windows didn't offer a share sheet for that.");
        }
    }

    private static void OpenTerminal(string path)
    {
        string folder = Directory.Exists(path) ? path : Path.GetDirectoryName(Path.GetFullPath(path))!;
        try
        {
            var wt = new ProcessStartInfo("wt.exe") { UseShellExecute = true };
            wt.ArgumentList.Add("-d");
            wt.ArgumentList.Add(folder);
            Process.Start(wt);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No Windows Terminal: PowerShell opens in the folder instead.
            Process.Start(new ProcessStartInfo("powershell.exe", "-NoExit") { UseShellExecute = true, WorkingDirectory = folder });
        }
    }
}
