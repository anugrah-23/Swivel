using Swivel.Core.Files;

namespace Swivel.Core.Catalog;

public enum WheelMode { Convert, Tools, Actions }

/// <summary>One wedge on the wheel: what it accepts, what it produces and which engine runs it.</summary>
public sealed record Operation(
    string Id,
    WheelMode Mode,
    string Label,
    string Hint,
    IReadOnlySet<FileKind> InputKinds,
    string? OutputExtension,
    string Engine)
{
    /// <summary>The format the output takes, when it is fixed (null keeps the input's format).</summary>
    public FileKind? OutputKind => OutputExtension is null ? null : FileKinds.FromExtension(OutputExtension);

    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();

    /// <summary>Trim, Crop and Resize open a small panel after the drop.</summary>
    public bool NeedsPanel { get; init; }

    /// <summary>Video and Office jobs run in the 1–2 heavy slots.</summary>
    public bool Heavy { get; init; }

    /// <summary>All inputs make one output (merge, archive), rather than one output each.</summary>
    public bool CombinesInputs { get; init; }

    public int MinInputs { get; init; } = 1;

    public int MaxInputs { get; init; } = int.MaxValue;

    /// <summary>The output is a folder of files (split pages, extracted archive, frames).</summary>
    public bool OutputIsFolder { get; init; }

    /// <summary>Appended to the output's name, such as " pages" or " frames".</summary>
    public string OutputSuffix { get; init; } = "";

    /// <summary>True when the job produces no file (copy path, reveal).</summary>
    public bool ProducesNoOutput => Engine == Engines.Action;

    /// <summary>The UI Automation name, for example "Convert to WebP, smaller".</summary>
    public string AutomationName => Mode == WheelMode.Convert && !OutputIsFolder
        ? $"Convert to {Label}, {Hint.ToLowerInvariant()}"
        : $"{Label}, {Hint.ToLowerInvariant()}";

    public string Parameter(string key, string fallback) => Parameters.TryGetValue(key, out var v) ? v : fallback;

    public bool Accepts(int inputCount) => inputCount >= MinInputs && inputCount <= MaxInputs;

    public static class Engines
    {
        public const string Image = "image";
        public const string Action = "action";
        public const string Media = "media";
        public const string Document = "document";
        public const string Office = "office";
        public const string Pdf = "pdf";
        public const string Archive = "archive";
    }
}
