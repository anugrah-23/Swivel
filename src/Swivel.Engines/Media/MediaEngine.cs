using System.Globalization;
using Swivel.Core.Catalog;
using Swivel.Core.Files;
using Swivel.Core.Jobs;
using Swivel.Engines.Infrastructure;

namespace Swivel.Engines.Media;

/// <summary>Audio and video through the LGPL FFmpeg build, run as a child process.</summary>
public sealed class MediaEngine : IEngine
{
    private const int MaxFrames = 600;

    private static readonly Lazy<Task<HashSet<string>>> Encoders = new(LoadEncodersAsync);

    public string Name => Operation.Engines.Media;

    public static string Tool(string exe) =>
        EngineLocator.Find(Path.Combine("ffmpeg", exe))
        ?? throw new JobException("FFmpeg is missing from this install. Reinstall Swivel to get it back.");

    public async Task RunAsync(Job job, IProgress<double> progress, CancellationToken ct)
    {
        string input = job.Inputs[0];
        var info = await MediaInfo.ProbeAsync(input, ct);
        var op = job.Operation;
        var target = op.OutputKind ?? info.Kind;
        bool wantsVideo = FileKinds.Family(target) == FileFamily.Video || op.Id is "video.gif" or "video.frames";

        if (wantsVideo && !info.HasVideo) throw new JobException($"{Path.GetFileName(input)} has no video in it.");
        if (!wantsVideo && !info.HasAudio && op.Id != "video.mute") throw new JobException($"{Path.GetFileName(input)} has no sound in it.");

        var args = new List<string> { "-hide_banner", "-nostdin", "-y" };
        double duration = info.Duration;
        if (op.Id == "media.trim")
        {
            // Seeking before -i is fast; with stream copy the cut snaps to the nearest keyframe.
            double start = Seconds(op.Parameter("start", "0")), end = Seconds(op.Parameter("end", "0"));
            if (end <= 0 || end > info.Duration) end = info.Duration;
            if (end - start < 0.1) throw new JobException("That trim leaves nothing behind.");
            args.AddRange(["-ss", Fmt(start), "-to", Fmt(end)]);
            duration = end - start;
        }
        args.AddRange(["-i", input]);
        args.AddRange(await ArgumentsFor(op, target, info));
        info = info with { Duration = duration };
        args.AddRange(["-progress", "pipe:1", "-nostats"]);

        if (op.OutputIsFolder)
        {
            Directory.CreateDirectory(job.TempPath!);
            args.Add(Path.Combine(job.TempPath!, "frame %04d.png"));
        }
        else
        {
            args.Add(job.TempPath!);
        }

        var result = await ProcessRunner.RunAsync(Tool("ffmpeg.exe"), args, ct, line =>
        {
            if (info.Duration > 0 && line.StartsWith("out_time_us=", StringComparison.Ordinal)
                && long.TryParse(line.AsSpan(12), out long us) && us > 0)
                progress.Report(Math.Min(0.99, us / 1e6 / info.Duration));
        });

        if (result.ExitCode != 0)
            throw new JobException($"Couldn't crack {Path.GetFileName(input)}.", new InvalidOperationException(result.StandardError));
        progress.Report(1);
    }

    private static async Task<IEnumerable<string>> ArgumentsFor(Operation op, FileKind target, MediaInfo info)
    {
        switch (op.Id)
        {
            case "video.gif":
                return ["-an", "-vf", "fps=12,scale='min(480,iw)':-2:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer",
                    "-loop", "0", "-f", "gif"];
            case "video.frames":
                return ["-an", "-vf", "fps=1", "-frames:v", MaxFrames.ToString(CultureInfo.InvariantCulture), "-f", "image2"];
            case "video.mute":
                return ["-map", "0", "-map", "-0:a", "-c", "copy", "-f", Muxer(target)];
            case "video.mkv":
                return ["-map", "0:v?", "-map", "0:a?", "-c", "copy", "-f", "matroska"];
            case "media.compress":
                return info.HasVideo ? await VideoArgs(target, info, compress: true) : AudioArgs(target, compress: true);
            case "media.trim" when op.Parameter("exact", "false") == "true":
                return info.HasVideo ? await VideoArgs(target, info, compress: false) : AudioArgs(target, compress: false);
            case "media.trim":
                return ["-map", "0", "-c", "copy", "-avoid_negative_ts", "make_zero", "-f", Muxer(target)];
        }
        return FileKinds.Family(target) == FileFamily.Video ? await VideoArgs(target, info, compress: false) : AudioArgs(target, compress: false);
    }

    private static IEnumerable<string> AudioArgs(FileKind target, bool compress) => target switch
    {
        FileKind.Mp3 => ["-vn", "-c:a", "libmp3lame", "-q:a", compress ? "6" : "2", "-f", "mp3"],
        FileKind.M4a => ["-vn", "-c:a", "aac", "-b:a", compress ? "96k" : "192k", "-f", "ipod"],
        FileKind.Wav => ["-vn", "-c:a", "pcm_s16le", "-f", "wav"],
        FileKind.Flac => ["-vn", "-c:a", "flac", "-compression_level", compress ? "12" : "5", "-f", "flac"],
        FileKind.Ogg => ["-vn", "-c:a", "libvorbis", "-q:a", compress ? "2" : "5", "-f", "ogg"],
        FileKind.Opus => ["-vn", "-c:a", "libopus", "-b:a", compress ? "64k" : "128k", "-f", "opus"],
        _ => throw new JobException($"Swivel can't write {target} audio yet."),
    };

    private static async Task<IEnumerable<string>> VideoArgs(FileKind target, MediaInfo info, bool compress)
    {
        var args = new List<string>();
        int width = info.Width, height = info.Height;
        if (compress && width > 1280)
        {
            args.AddRange(["-vf", "scale=1280:-2"]);
            height = (int)(height * 1280.0 / width);
            width = 1280;
        }
        string bitrate = VideoBitrate(width, height, info.FrameRate, compress ? 0.05 : 0.1).ToString(CultureInfo.InvariantCulture);

        switch (target)
        {
            case FileKind.WebM:
                args.AddRange(["-c:v", "libvpx-vp9", "-b:v", "0", "-crf", compress ? "40" : "33", "-row-mt", "1",
                    "-deadline", "good", "-cpu-used", "5", "-c:a", "libopus", "-b:a", compress ? "64k" : "128k", "-f", "webm"]);
                break;
            case FileKind.Avi:
                args.AddRange(["-c:v", "mpeg4", "-q:v", compress ? "8" : "4", "-c:a", "libmp3lame", "-q:a", "4", "-f", "avi"]);
                break;
            case FileKind.Mkv:
                args.AddRange([.. await H264(bitrate), "-c:a", "aac", "-b:a", compress ? "96k" : "160k", "-f", "matroska"]);
                break;
            case FileKind.Mp4 or FileKind.Mov:
                args.AddRange([.. await H264(bitrate), "-c:a", "aac", "-b:a", compress ? "96k" : "160k",
                    "-movflags", "+faststart", "-f", target == FileKind.Mov ? "mov" : "mp4"]);
                break;
            default:
                throw new JobException($"Swivel can't write {target} video yet.");
        }
        return args;
    }

    /// <summary>
    /// H.264 through Windows' own licensed Media Foundation encoder where present; OpenH264 or
    /// MPEG-4 Part 2 otherwise. No GPL encoder is ever used.
    /// </summary>
    private static async Task<IEnumerable<string>> H264(string bitrate)
    {
        var encoders = await Encoders.Value;
        if (encoders.Contains("h264_mf")) return ["-c:v", "h264_mf", "-b:v", bitrate, "-pix_fmt", "nv12"];
        if (encoders.Contains("libopenh264")) return ["-c:v", "libopenh264", "-b:v", bitrate, "-pix_fmt", "yuv420p"];
        return ["-c:v", "mpeg4", "-q:v", "4"];
    }

    /// <summary>Bits per second for a given picture size and frame rate at a bits-per-pixel density.</summary>
    public static long VideoBitrate(int width, int height, double fps, double bitsPerPixel)
    {
        double rate = fps is > 0 and < 240 ? fps : 30;
        long bps = (long)(Math.Max(width, 16) * Math.Max(height, 16) * rate * bitsPerPixel);
        return Math.Clamp(bps, 400_000, 12_000_000);
    }

    private static double Seconds(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double s) ? Math.Max(0, s) : 0;

    private static string Fmt(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Muxer(FileKind kind) => kind switch
    {
        FileKind.Mp4 => "mp4", FileKind.Mov => "mov", FileKind.Mkv => "matroska", FileKind.WebM => "webm", FileKind.Avi => "avi",
        FileKind.Mp3 => "mp3", FileKind.M4a => "ipod", FileKind.Wav => "wav", FileKind.Flac => "flac", FileKind.Ogg => "ogg", FileKind.Opus => "opus",
        _ => throw new JobException($"Swivel can't write {kind} yet."),
    };

    private static async Task<HashSet<string>> LoadEncodersAsync()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        await ProcessRunner.RunAsync(Tool("ffmpeg.exe"), ["-hide_banner", "-encoders"], CancellationToken.None, line =>
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Length == 6) lock (names) names.Add(parts[1]);
        });
        return names;
    }
}
