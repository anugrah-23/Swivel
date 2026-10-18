using System.Globalization;
using System.Text.Json;
using Swivel.Core.Catalog;
using Swivel.Core.Files;
using Swivel.Core.Jobs;
using Swivel.Engines.Infrastructure;

namespace Swivel.Engines.Media;

/// <summary>What ffprobe says about a media file.</summary>
public sealed record MediaInfo(FileKind Kind, double Duration, bool HasVideo, bool HasAudio, int Width, int Height, double FrameRate, long Bytes)
{
    public static async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct)
    {
        string ffprobe = MediaEngine.Tool("ffprobe.exe");
        var lines = new List<string>();
        var result = await ProcessRunner.RunAsync(ffprobe,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path], ct,
            onOutputLine: line => { lock (lines) lines.Add(line); });
        if (result.ExitCode != 0) throw new JobException($"Couldn't read {Path.GetFileName(path)} as audio or video.");

        using var json = JsonDocument.Parse(string.Join('\n', lines));
        var root = json.RootElement;
        double duration = root.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var d)
            ? double.Parse(d.GetString() ?? "0", CultureInfo.InvariantCulture) : 0;

        bool hasVideo = false, hasAudio = false;
        int width = 0, height = 0;
        double fps = 0;
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                string type = stream.GetProperty("codec_type").GetString() ?? "";
                bool attachedPicture = stream.TryGetProperty("disposition", out var disp)
                    && disp.TryGetProperty("attached_pic", out var pic) && pic.GetInt32() == 1;
                if (type == "audio") hasAudio = true;
                if (type == "video" && !attachedPicture && !hasVideo)
                {
                    hasVideo = true;
                    width = stream.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                    height = stream.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                    fps = stream.TryGetProperty("avg_frame_rate", out var r) ? ParseRate(r.GetString()) : 0;
                }
            }
        }
        return new MediaInfo(FileSniffer.Detect(path), duration, hasVideo, hasAudio, width, height, fps, new FileInfo(path).Length);
    }

    private static double ParseRate(string? rate)
    {
        if (rate is null) return 0;
        var parts = rate.Split('/');
        return parts.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out double n)
            && double.TryParse(parts[1], CultureInfo.InvariantCulture, out double m) && m > 0 ? n / m : 0;
    }

    /// <summary>Rough output size from bitrate × duration; always shown as approximate.</summary>
    public long? Estimate(Operation op)
    {
        if (op.Engine != Operation.Engines.Media || Duration <= 0) return null;
        if (op.OutputIsFolder) return null;
        var target = op.OutputKind ?? Kind;
        double audioKbps = target switch
        {
            FileKind.Mp3 => 190, FileKind.M4a => 192, FileKind.Opus => 128, FileKind.Ogg => 160,
            FileKind.Wav => 1411, FileKind.Flac => 800, _ => 160,
        };
        bool video = HasVideo && FileKinds.Family(target) == FileFamily.Video;
        if (op.Id == "media.compress") return (long)(Bytes * (HasVideo ? 0.45 : 0.6));
        if (op.Id == "video.mute") return (long)(Bytes * 0.92);
        if (op.Id == "video.mkv") return Bytes;
        if (target == FileKind.Gif && HasVideo)
        {
            double scale = Width > 480 ? 480.0 / Width : 1;
            return (long)(Width * scale * Height * scale * 12 * Duration * 0.12);
        }
        double videoKbps = video ? MediaEngine.VideoBitrate(Width, Height, FrameRate, target == FileKind.WebM ? 0.06 : 0.1) / 1000.0 : 0;
        return (long)((audioKbps * (HasAudio ? 1 : 0) + videoKbps) * 1000 / 8 * Duration);
    }
}
