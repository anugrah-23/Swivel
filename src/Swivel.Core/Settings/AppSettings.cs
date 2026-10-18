namespace Swivel.Core.Settings;

public enum OutputLocation { NextToOriginal, Folder }

public enum WedgeOrdering { Usage, Fixed }

/// <summary>
/// A user-made wedge: a built-in operation (<see cref="Base"/>, such as "image.webp") with its own
/// label and parameters, for example { "maxSize": "1200", "quality": "70", "strip": "true" }.
/// </summary>
public sealed record PresetSetting(string Id, string Label, string Hint, string Base, IReadOnlyDictionary<string, string> Parameters);

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

    public const string DefaultSelectionHotkey = "Ctrl+Alt+W";

    /// <summary>Tried in order when the default selection shortcut is taken by another app.</summary>
    public static IReadOnlyList<string> SelectionHotkeyFallbacks { get; } = ["Ctrl+Alt+X", "Ctrl+Alt+Q", "Ctrl+Shift+Space"];

    public string SelectionHotkey { get; init; } = DefaultSelectionHotkey;

    public string ClipboardHotkey { get; init; } = "Ctrl+Alt+V";

    public OutputRule Output { get; init; } = new();

    public WedgeOrdering Ordering { get; init; } = WedgeOrdering.Usage;

    /// <summary>Operation ids hidden from the wheel.</summary>
    public IReadOnlyList<string> HiddenOperations { get; init; } = [];

    public IReadOnlyList<string> PinnedFolders { get; init; } = [];

    public IReadOnlyList<PresetSetting> Presets { get; init; } = [];

    /// <summary>Adds "Spin with Swivel" to the Explorer right-click menu.</summary>
    public bool ExplorerMenu { get; init; } = true;

    /// <summary>Plays wheel motion when Windows animations are also on.</summary>
    public bool Animations { get; init; } = true;

    /// <summary>Starts Swivel in the tray when the user signs in to Windows.</summary>
    public bool LaunchAtSignIn { get; init; } = true;

    /// <summary>The flyout's on/off switch. Off, the trigger and shortcuts do nothing; jobs still finish.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>True once the first-run steps have been seen (or skipped).</summary>
    public bool OnboardingDone { get; init; }
}
