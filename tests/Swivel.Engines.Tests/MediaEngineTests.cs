using System.Diagnostics;
using Swivel.Core.Catalog;
using Swivel.Core.Files;
using Swivel.Engines.Media;
using Xunit.Abstractions;

namespace Swivel.Engines.Tests;

public class MediaEngineTests(ITestOutputHelper output) : IDisposable
{
    private readonly Fixtures _fixtures = new();

    private async Task<string> Video(string name = "clip.mp4", int seconds = 3)
    {
        string path = Path.Combine(_fixtures.Dir, name);
        await Fixtures.Ffmpeg("-f", "lavfi", "-i", $"testsrc2=size=320x240:rate=25:duration={seconds}",
            "-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}",
            "-c:v", "mpeg4", "-q:v", "5", "-c:a", "aac", "-shortest", path);
        return path;
    }

    private async Task<string> Audio(string name = "tone.wav")
    {
        string path = Path.Combine(_fixtures.Dir, name);
        await Fixtures.Ffmpeg("-f", "lavfi", "-i", "sine=frequency=330:duration=3", "-c:a", "pcm_s16le", path);
        return path;
    }

    [Fact]
    public async Task Every_video_convert_wedge_works_and_keeps_the_original()
    {
        string input = await Video();
        string before = Fixtures.Hash(input);

        foreach (var op in Fixtures.Catalog.For([FileKind.Mp4], WheelMode.Convert))
        {
            var clock = Stopwatch.StartNew();
            var result = await Fixtures.Run(op, input);
            output.WriteLine($"{op.Id}: {clock.ElapsedMilliseconds} ms {string.Join("; ", result.Failures.Select(f => f.Message + " " + f.Detail?[..Math.Min(400, f.Detail.Length)]))}");

            Assert.True(result.Succeeded, op.Id);
            string produced = Assert.Single(result.Outputs);
            Assert.Equal(op.OutputKind, FileSniffer.Detect(produced));
            var info = await MediaInfo.ProbeAsync(produced, CancellationToken.None);
            Assert.InRange(info.Duration, 2.0, 3.6);
            Assert.Equal(op.Id.StartsWith("video."), info.HasVideo);
        }
        Assert.Equal(before, Fixtures.Hash(input));
        Fixtures.AssertNoTemp(_fixtures.Dir);
    }

    [Fact]
    public async Task Every_audio_convert_wedge_works()
    {
        string input = await Audio();
        foreach (var op in Fixtures.Catalog.For([FileKind.Wav], WheelMode.Convert))
        {
            var result = await Fixtures.Run(op, input);
            Assert.True(result.Succeeded, $"{op.Id}: {string.Join("; ", result.Failures.Select(f => f.Detail))}");
            var info = await MediaInfo.ProbeAsync(result.Outputs[0], CancellationToken.None);
            Assert.True(info.HasAudio && !info.HasVideo, op.Id);
            Assert.InRange(info.Duration, 2.8, 3.2);
        }
    }

    [Fact]
    public async Task Mute_drops_the_sound_and_compress_shrinks()
    {
        string input = await Video("big.mp4", seconds: 4);

        var muted = await Fixtures.Run(Fixtures.Op("video.mute"), input);
        var info = await MediaInfo.ProbeAsync(Assert.Single(muted.Outputs), CancellationToken.None);
        Assert.True(info.HasVideo);
        Assert.False(info.HasAudio);
        Assert.EndsWith("big (1).mp4", muted.Outputs[0]);

        var compressed = await Fixtures.Run(Fixtures.Op("media.compress"), input);
        Assert.True(compressed.Succeeded, string.Join("; ", compressed.Failures.Select(f => f.Detail)));
    }

    [Fact]
    public async Task Frames_land_in_a_folder()
    {
        string input = await Video();
        var result = await Fixtures.Run(Fixtures.Op("video.frames"), input);

        string folder = Assert.Single(result.Outputs);
        Assert.Equal(Path.Combine(_fixtures.Dir, "clip frames"), folder);
        Assert.InRange(Directory.GetFiles(folder, "*.png").Length, 3, 4);
    }

    [Fact]
    public async Task Audio_only_file_has_no_video_wedges()
    {
        Assert.DoesNotContain(Fixtures.Catalog.For([FileKind.Wav], WheelMode.Convert), o => o.Id.StartsWith("video."));
        string input = await Audio();
        var result = await Fixtures.Run(Fixtures.Op("video.mp4") with { InputKinds = new HashSet<FileKind> { FileKind.Wav } }, input);
        Assert.Equal("tone.wav has no video in it.", Assert.Single(result.Failures).Message);
    }

    [Fact]
    public async Task Cancel_kills_ffmpeg_and_leaves_nothing()
    {
        string input = Path.Combine(_fixtures.Dir, "long.mp4");
        await Fixtures.Ffmpeg("-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30:duration=60", "-c:v", "mpeg4", "-q:v", "10", input);
        int ffmpegBefore = Process.GetProcessesByName("ffmpeg").Length;

        await using var queue = new Core.Jobs.JobQueue(Fixtures.AllEngines(), Path.GetDirectoryName!);
        var group = queue.Submit(Fixtures.Op("video.webm"), [input]);
        await Task.Delay(1500);
        group.Cancel();
        var result = await group.Completion;
        await Task.Delay(300);

        Assert.True(result.Cancelled);
        Assert.Equal([input], Directory.GetFileSystemEntries(_fixtures.Dir));
        Assert.Equal(ffmpegBefore, Process.GetProcessesByName("ffmpeg").Length);
    }

    public void Dispose() => _fixtures.Dispose();
}
