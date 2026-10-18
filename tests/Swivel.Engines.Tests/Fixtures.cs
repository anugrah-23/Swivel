using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetVips;
using Operation = Swivel.Core.Catalog.Operation;
using Swivel.Core.Catalog;
using Swivel.Core.Files;
using Swivel.Core.Jobs;
using Swivel.Engines.Images;
using VipsImage = NetVips.Image;

namespace Swivel.Engines.Tests;

/// <summary>Builds photo-like fixtures on the fly so the repo carries no binary test files.</summary>
public sealed class Fixtures : IDisposable
{
    private static readonly Guid HeifContainer = new("e1e62521-6787-405b-a339-500715b5763f");

    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "swivel-engine-tests", Guid.NewGuid().ToString("N"));

    public Fixtures() => Directory.CreateDirectory(Dir);

    /// <summary>A noisy gradient, which compresses like a real photo rather than a flat fill.</summary>
    public static VipsImage Photo(int width, int height)
    {
        using var noise = VipsImage.Gaussnoise(width, height, sigma: 28, mean: 128);
        using var xyz = VipsImage.Xyz(width, height);
        using var x = xyz[0];
        using var y = xyz[1];
        using var g = x * (255.0 / width);
        using var b = y * (255.0 / height);
        using var joined = noise.Bandjoin(g, b);
        using var cast = joined.Cast(Enums.BandFormat.Uchar);
        return cast.Copy(interpretation: Enums.Interpretation.Srgb);
    }

    public string Write(string name, int width = 640, int height = 480, FileKind? as_ = null)
    {
        string path = Path.Combine(Dir, name);
        var kind = as_ ?? FileKinds.FromExtension(name);
        using var image = Photo(width, height);
        switch (kind)
        {
            case FileKind.Jpeg: image.Jpegsave(path, q: 90); break;
            case FileKind.Png: image.Pngsave(path); break;
            case FileKind.WebP: image.Webpsave(path); break;
            case FileKind.Gif: image.Gifsave(path); break;
            case FileKind.Tiff: image.Tiffsave(path); break;
            case FileKind.Avif: image.Heifsave(path, compression: Enums.ForeignHeifCompression.Av1); break;
            default: throw new ArgumentException($"No fixture writer for {kind}");
        }
        return path;
    }

    /// <summary>Writes an HEIC through WIC, or returns null when this machine can't encode HEIF.</summary>
    public string? WriteHeic(string name, int width, int height)
    {
        string path = Path.Combine(Dir, name);
        using var image = Photo(width, height);
        using var withAlpha = image.Bandjoin(255);
        byte[] rgba = withAlpha.WriteToMemory<byte>();
        for (int i = 0; i < rgba.Length; i += 4) (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);

        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, rgba, width * 4);
                var encoder = BitmapEncoder.Create(HeifContainer);
                encoder.Frames.Add(BitmapFrame.Create(source));
                using var file = File.Create(path);
                encoder.Save(file);
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error == null) return path;
        if (File.Exists(path)) File.Delete(path);
        return null;
    }

    /// <summary>
    /// A real phone HEIC for machines without a HEIF encoder: SWIVEL_HEIC_SAMPLE, or
    /// tests/fixtures/sample.heic (git-ignored; drop any iPhone photo there).
    /// </summary>
    public static string? HeicSample()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("SWIVEL_HEIC_SAMPLE");
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv)) return fromEnv;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "Swivel.sln"))) continue;
            string sample = Path.Combine(dir.FullName, "tests", "fixtures", "sample.heic");
            return File.Exists(sample) ? sample : null;
        }
        return null;
    }

    /// <summary>Copies a fixture into this test's folder so outputs land beside it, not in the repo.</summary>
    public string Import(string path)
    {
        string copy = Path.Combine(Dir, Path.GetFileName(path));
        File.Copy(path, copy);
        return copy;
    }

    public static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    public static IEngine[] AllEngines() =>
    [
        new ImageEngine(), new Media.MediaEngine(), new Documents.DocumentEngine(), new Documents.OfficeEngine(),
        new Pdf.PdfEngine(), new Archives.ArchiveEngine(), new Files.TransferEngine(),
    ];

    public static async Task<JobGroupResult> Run(Operation operation, params string[] inputs)
    {
        await using var queue = new JobQueue(AllEngines(), Path.GetDirectoryName!);
        return await queue.Submit(operation, inputs).Completion;
    }

    public static OperationCatalog Catalog { get; } = new();

    public static Operation Op(string id) => Catalog.Find(id) ?? throw new ArgumentException(id);

    /// <summary>Runs FFmpeg directly to make media fixtures from its built-in test sources.</summary>
    public static async Task Ffmpeg(params string[] args)
    {
        var result = await Infrastructure.ProcessRunner.RunAsync(Media.MediaEngine.Tool("ffmpeg.exe"),
            ["-hide_banner", "-y", .. args], CancellationToken.None);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.StandardError);
    }

    public static void AssertNoTemp(string dir) =>
        Assert.Empty(Directory.GetFileSystemEntries(dir, "*" + OutputNamer.TempSuffix, SearchOption.AllDirectories));

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Test classes that start FFmpeg run one after another, so a test that counts FFmpeg processes
/// (cancel must leave none behind) never sees another class's encode.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FfmpegCollection
{
    public const string Name = "FFmpeg";
}
