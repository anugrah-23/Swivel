using System.Text.Json;
using System.Text.Json.Serialization;

namespace Swivel.Core.Settings;

/// <summary>Loads, saves and watches the settings file; edits made by hand apply live.</summary>
public sealed class SettingsStore : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _path;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;

    public SettingsStore(string path)
    {
        _path = path;
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    /// <summary>Raised on a thread-pool thread after the file changes on disk.</summary>
    public event Action<AppSettings>? Changed;

    /// <summary>Raised when the file can't be parsed; the previous settings stay in force.</summary>
    public event Action<Exception>? LoadFailed;

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string json = JsonSerializer.Serialize(settings, Json);
        string temp = _path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, _path, overwrite: true);
        _lastJson = json;
        Current = settings;
        Changed?.Invoke(settings);
    }

    /// <summary>The text Swivel itself last wrote, so the watcher ignores its own saves.</summary>
    private string? _lastJson;

    public void StartWatching()
    {
        string directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        _debounce = new Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(directory, Path.GetFileName(_path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += (_, _) => _debounce.Change(200, Timeout.Infinite);
        _watcher.Created += (_, _) => _debounce.Change(200, Timeout.Infinite);
        _watcher.Renamed += (_, _) => _debounce.Change(200, Timeout.Infinite);
    }

    private AppSettings Load()
    {
        if (!File.Exists(_path)) return new AppSettings();
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Json) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            LoadFailed?.Invoke(ex);
            return new AppSettings();
        }
    }

    private void Reload()
    {
        try
        {
            string json = File.ReadAllText(_path);
            if (json == _lastJson) return;
            _lastJson = json;
            var next = JsonSerializer.Deserialize<AppSettings>(json, Json);
            if (next is null) return;
            Current = next;
            Changed?.Invoke(next);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            LoadFailed?.Invoke(ex);
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}
