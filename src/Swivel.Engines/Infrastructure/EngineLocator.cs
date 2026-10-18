namespace Swivel.Engines.Infrastructure;

/// <summary>
/// Finds engine binaries: shipped ones under <c>engines\</c> beside Swivel.exe, optional packs under
/// %LocalAppData%\Swivel\packs, and in a dev checkout the repo's <c>engines\</c> folder.
/// </summary>
public static class EngineLocator
{
    public static string PacksRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Swivel", "packs");

    private static readonly Lazy<string?> RepoEngines = new(() =>
    {
        // SWIVEL_NO_REPO_ENGINES=1 makes a dev build behave like an install (to try pack downloads).
        if (Environment.GetEnvironmentVariable("SWIVEL_NO_REPO_ENGINES") == "1") return null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Swivel.sln")))
                return Path.Combine(dir.FullName, "engines");
        return null;
    });

    /// <summary>The full path of an engine file such as <c>ffmpeg\ffmpeg.exe</c>, or null when it isn't installed.</summary>
    public static string? Find(string relativePath)
    {
        foreach (string? root in new[] { Path.Combine(AppContext.BaseDirectory, "engines"), PacksRoot, RepoEngines.Value })
        {
            if (root is null) continue;
            string candidate = Path.Combine(root, relativePath);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
