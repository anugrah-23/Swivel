using System.Windows;
using System.Windows.Media.Imaging;
using Swivel.Core.Files;
using Swivel.Engines.Images;
using Swivel.Engines.Media;
using Swivel.Native;
using IDataObject = System.Windows.IDataObject;

namespace Swivel.App.Overlay;

/// <summary>What is being dragged, worked out once when the drag reaches the wheel.</summary>
public sealed class DragSession
{
    private const int SniffLimit = 100;

    private DragSession(IReadOnlyList<string> names, IReadOnlyList<string>? paths, IReadOnlyList<FileKind> kinds, long? totalBytes)
    {
        Names = names;
        Paths = paths;
        Kinds = kinds;
        TotalBytes = totalBytes;
    }

    public IReadOnlyList<string> Names { get; }

    /// <summary>Real paths, or null for virtual files that only exist once dropped.</summary>
    public IReadOnlyList<string>? Paths { get; }

    /// <summary>One kind per input, in order.</summary>
    public IReadOnlyList<FileKind> Kinds { get; }

    public long? TotalBytes { get; }

    public int Count => Names.Count;

    /// <summary>Header of the first image, filled in the background for size estimates.</summary>
    public ImageProbe? Probe { get; private set; }

    /// <summary>ffprobe facts about the first audio or video file, for size estimates.</summary>
    public MediaInfo? Media { get; private set; }

    public BitmapSource? Thumbnail { get; private set; }

    public static DragSession? From(IDataObject data)
    {
        if (data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
        {
            var kinds = paths.Select((p, i) => i < SniffLimit ? FileSniffer.Detect(p) : FileKinds.FromExtension(p)).ToArray();
            long total = 0;
            foreach (var p in paths)
                if (File.Exists(p)) total += new FileInfo(p).Length;
            var names = paths.Select(p => Path.GetFileName(p.TrimEnd('\\')) is { Length: > 0 } n ? n : p).ToArray();
            return new DragSession(names, paths, kinds, total);
        }

        if (data.GetData(VirtualFiles.DescriptorFormat) is MemoryStream descriptor)
        {
            var entries = VirtualFiles.ReadDescriptor(descriptor)
                .Where(e => !e.Name.Contains('\\') && !e.Name.Contains('/'))
                .ToArray();
            if (entries.Length == 0) return null;
            var kinds = entries.Select(e => e.IsDirectory ? FileKind.Folder : FileKinds.FromExtension(e.Name)).ToArray();
            long? total = entries.All(e => e.Size.HasValue) ? entries.Sum(e => e.Size!.Value) : null;
            return new DragSession(entries.Select(e => e.Name).ToArray(), null, kinds, total);
        }

        return null;
    }

    /// <summary>True once the background probe has finished, whether or not it learned anything.</summary>
    public bool DetailsLoaded { get; private set; }

    /// <summary>Reads the first file's header (and a thumbnail for images) off the UI thread.</summary>
    public Task LoadDetailsAsync() => LoadAsync().ContinueWith(_ => DetailsLoaded = true, TaskScheduler.Default);

    private Task LoadAsync()
    {
        if (Paths is not { Count: > 0 } paths) return Task.CompletedTask;
        return FileKinds.Family(Kinds[0]) switch
        {
            FileFamily.Image => Task.Run(() =>
            {
                Probe = ImageEstimator.Probe(paths[0]);
                Thumbnail = LoadThumbnail(paths[0]);
            }),
            FileFamily.Audio or FileFamily.Video => Task.Run(async () =>
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { Media = await MediaInfo.ProbeAsync(paths[0], timeout.Token); }
                catch (Exception) { Media = null; } // no estimate is fine
            }),
            _ => Task.CompletedTask,
        };
    }

    private static BitmapSource? LoadThumbnail(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.DecodePixelWidth = 96;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null; // formats WPF can't decode (or a missing codec) just show the type label
        }
    }
}
