namespace Swivel.Core.Tests;

/// <summary>A throwaway folder per test.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "swivel-tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name, byte[]? content = null)
    {
        string path = System.IO.Path.Combine(Path, name);
        System.IO.File.WriteAllBytes(path, content ?? [1, 2, 3]);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}
