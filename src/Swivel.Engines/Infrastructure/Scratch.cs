namespace Swivel.Engines.Infrastructure;

/// <summary>
/// A private temp folder for one job, for engines that insist on real file extensions (Office,
/// Pandoc, 7-Zip). Results are moved onto the job's temp path; the folder is always deleted.
/// </summary>
public sealed class Scratch : IDisposable
{
    public Scratch()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Swivel", "scratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
