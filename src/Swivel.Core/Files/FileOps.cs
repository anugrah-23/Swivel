namespace Swivel.Core.Files;

/// <summary>Copy and move for files or whole folders, across volumes too.</summary>
public static class FileOps
{
    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public static long Size(string path) =>
        File.Exists(path) ? new FileInfo(path).Length
        : Directory.Exists(path) ? new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
        : 0;

    /// <summary>Copies a file or folder to <paramref name="destination"/>, which must not exist yet.</summary>
    public static void Copy(string source, string destination, CancellationToken ct = default, Action<long>? copied = null)
    {
        if (File.Exists(source))
        {
            CopyFile(source, destination, ct, copied);
            return;
        }
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            CopyFile(file, Path.Combine(destination, Path.GetRelativePath(source, file)), ct, copied);
    }

    /// <summary>
    /// Moves a file or folder. Same-volume moves are a rename; across volumes the copy finishes
    /// before the source is deleted, so an interruption never loses data.
    /// </summary>
    public static void Move(string source, string destination, CancellationToken ct = default, Action<long>? copied = null)
    {
        if (File.Exists(source))
        {
            File.Move(source, destination, overwrite: false);
            return;
        }
        if (SameVolume(source, destination))
        {
            Directory.Move(source, destination);
            return;
        }
        Copy(source, destination, ct, copied);
        Directory.Delete(source, recursive: true);
    }

    private static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    private static void CopyFile(string source, string destination, CancellationToken ct, Action<long>? copied)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
        {
            var buffer = new byte[1 << 20];
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
                copied?.Invoke(read);
            }
        }
        File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
    }
}
