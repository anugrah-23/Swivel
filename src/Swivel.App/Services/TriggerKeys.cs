namespace Swivel.App.Services;

/// <summary>Maps the settings' trigger name to the virtual keys the hook watches.</summary>
public static class TriggerKeys
{
    public static uint[] Parse(string trigger) => trigger.Trim().ToLowerInvariant() switch
    {
        "alt" => [0xA4, 0xA5],
        "ctrl" or "control" => [0xA2, 0xA3],
        "shift" => [0xA0, 0xA1],
        "capslock" or "caps lock" => [0x14],
        "win" or "windows" => [0x5B, 0x5C],
        var other when uint.TryParse(other, out uint vk) && vk is > 0 and < 256 => [vk],
        _ => [0xA4, 0xA5],
    };

    public static string Label(string trigger) => trigger.Trim().ToLowerInvariant() switch
    {
        "ctrl" or "control" => "Ctrl",
        "shift" => "Shift",
        "capslock" or "caps lock" => "Caps",
        "win" or "windows" => "Win",
        _ => "Alt",
    };
}
