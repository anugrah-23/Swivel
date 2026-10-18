namespace Swivel.Core.Settings;

/// <summary>Decides which folder an output lands in.</summary>
public static class OutputLocator
{
    /// <param name="input">The original file.</param>
    /// <param name="rule">The user's output rule.</param>
    /// <param name="scratchRoot">Swivel's temp folder for virtual-file drops; inputs under it have no real home.</param>
    /// <param name="fallback">Where outputs for scratch inputs go (the Downloads folder).</param>
    public static string DirectoryFor(string input, OutputRule rule, string scratchRoot, string fallback)
    {
        if (rule.Location == OutputLocation.Folder && !string.IsNullOrWhiteSpace(rule.Folder))
            return rule.Folder;

        string directory = Path.GetDirectoryName(Path.GetFullPath(input)) ?? fallback;
        string scratch = Path.GetFullPath(scratchRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return (directory + Path.DirectorySeparatorChar).StartsWith(scratch, StringComparison.OrdinalIgnoreCase)
            ? fallback
            : directory;
    }
}
