using System.Text.Json;
using Swivel.Engines.Infrastructure;

namespace Swivel.Engines.Tests;

public class EnginePackTests
{
    [Fact]
    public void Pack_hashes_match_the_build_lock_file()
    {
        string? lockFile = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "build", "engines.lock.json");
            if (File.Exists(candidate)) { lockFile = candidate; break; }
        }
        Assert.NotNull(lockFile);
        var pins = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(lockFile))!;

        foreach (var pack in EnginePacks.All.Where(p => pins.ContainsKey(p.Url)))
            Assert.Equal(pins[pack.Url], pack.Sha256);
        Assert.True(pins.ContainsKey(EnginePacks.Pandoc.Url), "Pandoc must be pinned in build/engines.lock.json");
    }

    [Fact]
    public void Core_engines_are_present_in_a_dev_checkout()
    {
        Assert.NotNull(EngineLocator.Find(Path.Combine("ffmpeg", "ffmpeg.exe")));
        Assert.NotNull(EngineLocator.Find(Path.Combine("ffmpeg", "ffprobe.exe")));
        Assert.NotNull(EngineLocator.Find(Path.Combine("7zip", "7zr.exe")));
        Assert.NotNull(EnginePacks.Locate(EnginePacks.Pandoc));
    }
}
