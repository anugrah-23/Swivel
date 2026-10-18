using System.Reflection;
using Serilog;
using Velopack;
using Velopack.Sources;

namespace Swivel.App.Services;

/// <summary>
/// Delta updates from GitHub Releases through Velopack. Swivel checks once a day, downloads in the
/// background and asks before restarting. Off until a repository URL is built in (SwivelUpdateRepo).
/// </summary>
public static class Updates
{
    public static string? Repo { get; } = typeof(Updates).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "UpdateRepo")?.Value is { Length: > 0 } repo ? repo : null;

    private static UpdateManager? _manager;
    private static UpdateInfo? _ready;

    /// <summary>True for an installed build that knows where its updates live.</summary>
    public static bool Enabled => Repo != null && (Manager()?.IsInstalled ?? false);

    private static UpdateManager? Manager()
    {
        if (Repo is null) return null;
        try { return _manager ??= new UpdateManager(new GithubSource(Repo, null, false)); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Update manager unavailable");
            return null;
        }
    }

    /// <summary>Checks and downloads. Returns the new version when one is ready to install, else null.</summary>
    public static async Task<string?> CheckAndDownloadAsync()
    {
        if (!Enabled || Manager() is not { } manager) return null;
        try
        {
            var info = await manager.CheckForUpdatesAsync();
            if (info is null) return null;
            await manager.DownloadUpdatesAsync(info);
            _ready = info;
            Log.Information("Update {Version} downloaded", info.TargetFullRelease.Version);
            return info.TargetFullRelease.Version.ToString();
        }
        catch (Exception ex)
        {
            Log.Information(ex, "Update check failed (offline is fine)");
            return null;
        }
    }

    public static void RestartIntoUpdate()
    {
        if (_ready != null) Manager()?.ApplyUpdatesAndRestart(_ready);
    }
}
