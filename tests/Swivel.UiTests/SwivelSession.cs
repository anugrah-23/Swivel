using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace Swivel.UiTests;

/// <summary>
/// Runs the real Swivel.exe in isolated mode (its own data folder, mutex and pipe; no Explorer menu
/// or hotkeys), so UI tests never touch the user's own Swivel.
/// </summary>
public sealed class SwivelSession : IDisposable
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(12);

    private readonly List<Process> _processes = [];

    public SwivelSession()
    {
        Directory.CreateDirectory(Work);
        Directory.CreateDirectory(DataDir);
        // Usage ranks start empty, so wedges keep their table order.
        File.WriteAllText(Path.Combine(DataDir, "settings.json"), """{ "explorerMenu": false, "animations": false }""");
    }

    public string Work { get; } = Path.Combine(Path.GetTempPath(), "swivel-ui-tests", Guid.NewGuid().ToString("N"));

    public string DataDir => Path.Combine(Work, "data");

    public UIA3Automation Automation { get; } = new();

    public static string Exe
    {
        get
        {
            string config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                string exe = Path.Combine(dir.FullName, "src", "Swivel.App", "bin", config, "net10.0-windows", "Swivel.exe");
                if (File.Exists(exe)) return exe;
            }
            throw new FileNotFoundException("Build Swivel.App first.");
        }
    }

    public Process Launch(params string[] args)
    {
        var start = new ProcessStartInfo(Exe) { UseShellExecute = false };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        start.Environment["SWIVEL_DATA_DIR"] = DataDir;
        var process = Process.Start(start)!;
        _processes.Add(process);
        return process;
    }

    /// <summary>A real 400 × 300 PNG.</summary>
    public string Png(string name)
    {
        string path = Path.Combine(Work, name);
        var pixels = new byte[400 * 300 * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = (byte)(i % 251); pixels[i + 1] = 120; pixels[i + 2] = 200; pixels[i + 3] = 255; }
        var bitmap = BitmapSource.Create(400, 300, 96, 96, PixelFormats.Bgra32, null, pixels, 400 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
        return path;
    }

    /// <summary>Finds an on-screen element by its UI Automation name in any of Swivel's windows.</summary>
    public AutomationElement? Find(string name, TimeSpan? wait = null)
    {
        var until = DateTime.UtcNow + (wait ?? Patience);
        do
        {
            foreach (var process in _processes.Where(p => !p.HasExited))
            {
                var app = FlaUI.Core.Application.Attach(process);
                foreach (var window in app.GetAllTopLevelWindows(Automation))
                {
                    var match = window.FindFirstDescendant(cf => cf.ByName(name));
                    if (match != null && !match.Properties.IsOffscreen.ValueOrDefault) return match;
                }
            }
            Thread.Sleep(150);
        } while (DateTime.UtcNow < until);
        return null;
    }

    public static bool WaitFor(Func<bool> condition, TimeSpan? wait = null)
    {
        var until = DateTime.UtcNow + (wait ?? Patience);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            Thread.Sleep(100);
        }
        return condition();
    }

    public void Dispose()
    {
        foreach (var process in _processes)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process.Dispose();
        }
        Automation.Dispose();
        try { Directory.Delete(Work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
