namespace Swivel.Core.Jobs;

/// <summary>
/// Picks <c>name.ext</c>, then <c>name (1).ext</c> and so on, and reserves the pick in memory so
/// parallel jobs in one batch never race for the same name. Never returns an existing path, which
/// is what guarantees an original can't be overwritten.
/// </summary>
public sealed class OutputNamer
{
    public const string TempSuffix = ".swivel-tmp";

    private readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public string Reserve(string directory, string stem, string extension)
    {
        lock (_lock)
        {
            for (int n = 0; ; n++)
            {
                string name = n == 0 ? stem + extension : $"{stem} ({n}){extension}";
                string path = Path.GetFullPath(Path.Combine(directory, name));
                if (_reserved.Contains(path) || File.Exists(path) || Directory.Exists(path) || File.Exists(path + TempSuffix))
                    continue;
                _reserved.Add(path);
                return path;
            }
        }
    }

    public void Release(string path)
    {
        lock (_lock) _reserved.Remove(Path.GetFullPath(path));
    }

    /// <summary>
    /// The base name for an output: the input's name (a folder keeps its whole name; "a.tar.gz" becomes
    /// "a"), or for a combined job over several inputs, the shared parent folder's name.
    /// </summary>
    public static string StemFor(IReadOnlyList<string> inputs, bool combined)
    {
        if (combined && inputs.Count > 1)
        {
            var parents = inputs.Select(i => Path.GetDirectoryName(Path.GetFullPath(i).TrimEnd(Path.DirectorySeparatorChar))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string? parentName = parents.Length == 1 && parents[0] is { } p ? Path.GetFileName(p) : null;
            if (!string.IsNullOrEmpty(parentName)) return parentName;
            return Stem(inputs[0]);
        }
        return Stem(inputs[0]);
    }

    private static string Stem(string path)
    {
        string trimmed = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (Directory.Exists(trimmed)) return Path.GetFileName(trimmed);
        string stem = Path.GetFileNameWithoutExtension(trimmed);
        return stem.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) ? stem[..^4] : stem;
    }
}
