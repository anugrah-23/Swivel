namespace Swivel.Core.Settings;

public enum OutputLocation { NextToOriginal, Folder }

public enum WedgeOrdering { Usage, Fixed }

public sealed record OutputRule
{
    public OutputLocation Location { get; init; } = OutputLocation.NextToOriginal;

    /// <summary>The fixed folder used when <see cref="Location"/> is <see cref="OutputLocation.Folder"/>.</summary>
    public string? Folder { get; init; }
}

/// <summary>Everything in %AppData%\Swivel\settings.json. Unknown or missing fields fall back to these defaults.</summary>
public sealed record AppSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    /// <summary>The trigger key: "Alt", "CapsLock", "Ctrl", "Shift" or a virtual-key number.</summary>
    public string Trigger { get; init; } = "Alt";

    public string SelectionHotkey { get; init; } = "Ctrl+Alt+Space";

    public string ClipboardHotkey { get; init; } = "Ctrl+Alt+V";

    public OutputRule Output { get; init; } = new();

    public WedgeOrdering Ordering { get; init; } = WedgeOrdering.Usage;

    /// <summary>Operation ids hidden from the wheel.</summary>
    public IReadOnlyList<string> HiddenOperations { get; init; } = [];

    public IReadOnlyList<string> PinnedFolders { get; init; } = [];

    /// <summary>Plays wheel motion when Windows animations are also on.</summary>
    public bool Animations { get; init; } = true;

    public bool LaunchAtSignIn { get; init; }
}
