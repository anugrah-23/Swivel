using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Swivel.Core.Jobs;

namespace Swivel.Engines.Infrastructure;

public sealed record ProcessResult(int ExitCode, string StandardError);

/// <summary>
/// Runs an engine executable at below-normal priority with no window, streams its output, and
/// kills the whole process tree on cancel.
/// </summary>
public static class ProcessRunner
{
    private const int StderrTail = 8 * 1024;

    public static async Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        Action<string>? onOutputLine = null,
        string? workingDirectory = null)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(executable)!,
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = info };
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) onOutputLine?.Invoke(e.Data); };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (stderr)
            {
                stderr.AppendLine(e.Data);
                if (stderr.Length > StderrTail * 2) stderr.Remove(0, stderr.Length - StderrTail);
            }
        };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new JobException($"Couldn't start {Path.GetFileName(executable)}.", ex);
        }
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (InvalidOperationException) { }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using (cancellationToken.Register(() => Kill(process)))
            await process.WaitForExitAsync(CancellationToken.None);
        process.WaitForExit(); // flush the async readers
        cancellationToken.ThrowIfCancellationRequested();

        string tail;
        lock (stderr) tail = stderr.Length > StderrTail ? stderr.ToString(stderr.Length - StderrTail, StderrTail) : stderr.ToString();
        return new ProcessResult(process.ExitCode, tail);
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }
}
