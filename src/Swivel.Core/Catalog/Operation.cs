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

    /// <summary>
    /// A small panel opens after the drop to ask for details: "resize", "crop", "trim", or "folder"
    /// for Copy to / Move to without a pinned folder. Its answers are merged into <see cref="Parameters"/>.
    /// </summary>
    public string? Panel { get; init; }

    public bool NeedsPanel => Panel != null;

    /// <summary>Move to: the input itself is relocated, so a failed job must put it back, never delete it.</summary>
    public bool MovesInput { get; init; }

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

    /// <summary>A copy with extra parameters merged over the existing ones (panel answers, presets).</summary>
    public Operation WithParameters(IReadOnlyDictionary<string, string> extra)
    {
        var merged = new Dictionary<string, string>(Parameters);
        foreach (var (key, value) in extra) merged[key] = value;
        return this with { Parameters = merged };
    }

    public static class Engines
    {
        public const string Image = "image";
        public const string Action = "action";
        public const string Media = "media";
        public const string Document = "document";
        public const string Office = "office";
        public const string Pdf = "pdf";
        public const string Archive = "archive";
        public const string Transfer = "transfer";
    }
}
