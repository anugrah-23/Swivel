using System.Windows.Threading;
using Serilog;
using Swivel.App.Actions;
using Swivel.Core.Catalog;
using Swivel.Core.History;
using Swivel.Core.Jobs;
using Swivel.Core.Settings;
using Swivel.Engines.Archives;
using Swivel.Engines.Documents;
using Swivel.Engines.Files;
using Swivel.Engines.Images;
using Swivel.Engines.Media;
using Swivel.Engines.Pdf;
using Swivel.Native;

namespace Swivel.App.Services;

/// <summary>Where Swivel keeps things on disk.</summary>
public static class AppPaths
{
    private static readonly string? Override = Environment.GetEnvironmentVariable("SWIVEL_DATA_DIR") is { Length: > 0 } dir ? Path.GetFullPath(dir) : null;

    /// <summary>
    /// True when SWIVEL_DATA_DIR points Swivel at a private folder (UI tests, side-by-side builds).
    /// Such an instance keeps its own mutex and pipe and leaves the Explorer menu and hotkeys alone.
    /// </summary>
    public static bool Isolated => Override != null;

    public static string Data { get; } = Override ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Swivel");
    public static string Settings { get; } = Path.Combine(Data, "settings.json");
    public static string History { get; } = Path.Combine(Data, "history.db");
    public static string Logs { get; } = Override != null ? Path.Combine(Override, "logs")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Swivel", "logs");

    /// <summary>Names the single-instance mutex and the hand-off pipe.</summary>
    public static string InstanceName { get; } = Override is null ? $"Swivel-{Environment.UserName}"
        : $"Swivel-{Environment.UserName}-{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Override.ToLowerInvariant())))[..12]}";

    /// <summary>Virtual files dragged from browsers and Outlook are written here before conversion.</summary>
    public static string Drops { get; } = Path.Combine(Path.GetTempPath(), "Swivel", "drops");

    public static string Downloads { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
}

/// <summary>The long-lived parts of the shell: settings, catalog, history and the job queue.</summary>
public sealed class SwivelServices : IAsyncDisposable
{
    private sealed class ShellRecycler : IRecycler
    {
        public void Recycle(IReadOnlyList<string> paths) => ShellFiles.Recycle(paths);
    }

    private readonly Dispatcher _ui;
    private readonly JobQueue _queue;
    private readonly IRecycler _recycler = new ShellRecycler();

    public SwivelServices(Dispatcher ui)
    {
        _ui = ui;
        Settings = new SettingsStore(AppPaths.Settings);
        Settings.LoadFailed += ex => Log.Warning(ex, "settings.json couldn't be read; keeping the previous settings");
        Settings.StartWatching();
        Catalog = OperationCatalog.FromSettings(Settings.Current);
        Settings.Changed += settings => Catalog = OperationCatalog.FromSettings(settings);
        History = new HistoryStore(AppPaths.History);
        _queue = new JobQueue(
            [
                new ImageEngine(), new ActionEngine(ui), new MediaEngine(), new DocumentEngine(),
                new OfficeEngine(), new PdfEngine(), new ArchiveEngine(), new TransferEngine(),
            ],
            OutputDirectoryFor);
        _queue.GroupCompleted += OnGroupCompleted;
        CleanOldDrops();
        _ = Usage();
    }

    public SettingsStore Settings { get; }
    /// <summary>Rebuilt when settings change (presets, pinned folders).</summary>
    public OperationCatalog Catalog { get; private set; }
    public HistoryStore History { get; }

    /// <summary>Raised on the UI thread once a group has been recorded in history.</summary>
    public event Action<JobGroupResult, HistoryEntry?>? GroupFinished;

    /// <summary>Raised on the UI thread after a successful undo, so open menus can refresh.</summary>
    public event Action? HistoryChanged;

    /// <summary>Usage ranks, cached in memory so opening the wheel never waits on the database.</summary>
    public IReadOnlyDictionary<string, int> Usage()
    {
        if (Settings.Current.Ordering != WedgeOrdering.Usage) return new Dictionary<string, int>();
        lock (_usageLock) return new Dictionary<string, int>(_usage ??= new Dictionary<string, int>(History.UsageCounts()));
    }

    private readonly Lock _usageLock = new();
    private Dictionary<string, int>? _usage;

    public JobGroup Submit(Operation operation, IReadOnlyList<string> inputs)
    {
        Log.Information("Job {Operation} on {Count} file(s)", operation.Id, inputs.Count);
        return _queue.Submit(operation, inputs);
    }

    public UndoResult Undo(Guid groupId)
    {
        try
        {
            var result = History.Undo(groupId, _recycler);
            Log.Information("Undo {Group}: {Result}", groupId, result);
            if (result == UndoResult.Done) HistoryChanged?.Invoke();
            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Undo failed for {Group}", groupId);
            return UndoResult.OutputsChanged;
        }
    }

    private string OutputDirectoryFor(string input) =>
        OutputLocator.DirectoryFor(input, Settings.Current.Output, AppPaths.Drops, AppPaths.Downloads);

    private void OnGroupCompleted(JobGroupResult result)
    {
        HistoryEntry? entry = null;
        try
        {
            if (!result.Cancelled || result.Outputs.Count > 0) entry = History.Record(result);
            lock (_usageLock) _usage = null; // re-read on the next wheel open
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Couldn't record job {Group} in history", result.GroupId);
        }
        foreach (var failure in result.Failures)
            Log.Warning("Job {Operation} failed on {Input}: {Message}\n{Detail}", result.Operation.Id, failure.Input, failure.Message, failure.Detail);
        Log.Information("Job {Operation} finished: {Outputs} output(s), {Failures} failure(s), cancelled {Cancelled}",
            result.Operation.Id, result.Outputs.Count, result.Failures.Count, result.Cancelled);
        _ui.InvokeAsync(() => GroupFinished?.Invoke(result, entry));
    }

    /// <summary>Virtual-file drops older than a day are scratch; their outputs live elsewhere.</summary>
    private static void CleanOldDrops()
    {
        try
        {
            if (!Directory.Exists(AppPaths.Drops)) return;
            foreach (var dir in new DirectoryInfo(AppPaths.Drops).EnumerateDirectories())
                if (dir.CreationTimeUtc < DateTime.UtcNow.AddDays(-1)) dir.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug(ex, "Couldn't tidy old drops");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _queue.DisposeAsync();
        Settings.Dispose();
    }
}
