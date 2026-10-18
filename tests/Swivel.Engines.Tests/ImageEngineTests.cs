using System.Diagnostics;
using Swivel.Core.Catalog;
using Swivel.Core.Files;
using Swivel.Core.Jobs;
using Swivel.Engines.Images;
using Xunit.Abstractions;
using VipsImage = NetVips.Image;

namespace Swivel.Engines.Tests;

public class ImageEngineTests(ITestOutputHelper output) : IDisposable
{
    private readonly Fixtures _fixtures = new();
    private readonly OperationCatalog _catalog = new();

    public static TheoryData<string> Sources => new() { "photo.jpg", "photo.png", "photo.webp", "photo.gif", "photo.tiff", "photo.avif" };

    [Theory]
    [MemberData(nameof(Sources))]
    public async Task Every_convert_wedge_produces_its_format_and_keeps_the_original(string source)
    {
        string input = _fixtures.Write(source);
        string before = Fixtures.Hash(input);
        var operations = _catalog.For([FileSniffer.Detect(input)], WheelMode.Convert);
        Assert.NotEmpty(operations);

        foreach (var operation in operations)
        {
            var result = await Fixtures.Run(operation, input);

            Assert.True(result.Succeeded, $"{operation.Id} failed: {string.Join("; ", result.Failures.Select(f => f.Message))}");
            string produced = Assert.Single(result.Outputs);
            Assert.Equal(operation.OutputKind, FileSniffer.Detect(produced));
            Assert.True(new FileInfo(produced).Length > 0);
            if (operation.OutputKind is FileKind.Png or FileKind.Jpeg or FileKind.WebP or FileKind.Avif or FileKind.Tiff
                && !operation.Parameters.ContainsKey("maxSize"))
            {
                using var image = VipsImage.NewFromFile(produced);
                Assert.Equal((640, 480), (image.Width, image.Height));
            }
        }

        Assert.Equal(before, Fixtures.Hash(input));
        Assert.Empty(Directory.GetFiles(_fixtures.Dir, "*" + OutputNamer.TempSuffix));
    }

    [Fact]
    public async Task Rotate_turns_the_image_and_writes_a_new_file()
    {
        string input = _fixtures.Write("wide.jpg", 400, 200);
        var result = await Fixtures.Run(_catalog.Find("tool.rotate-right")!, input);

        string produced = Assert.Single(result.Outputs);
        Assert.Equal(Path.Combine(_fixtures.Dir, "wide (1).jpg"), produced);
        using var image = VipsImage.NewFromFile(produced);
        Assert.Equal((200, 400), (image.Width, image.Height));
    }

    [Fact]
    public async Task Web_preset_fits_1600_px_and_strips_exif()
    {
        string input = Path.Combine(_fixtures.Dir, "big.jpg");
        using (var photo = Fixtures.Photo(3200, 1800))
        using (var tagged = photo.Mutate(m => m.Set(NetVips.GValue.GStrType, "exif-ifd0-Artist", "Swivel test")))
            tagged.Jpegsave(input);
        using (var original = VipsImage.NewFromFile(input))
            Assert.Contains("exif-data", original.GetFields());

        var result = await Fixtures.Run(_catalog.Find("preset.web-image")!, input);

        using var image = VipsImage.NewFromFile(Assert.Single(result.Outputs));
        Assert.Equal((1600, 900), (image.Width, image.Height));
        Assert.DoesNotContain("exif-data", image.GetFields());
    }

    [Fact]
    public async Task Compress_makes_a_lossy_file_smaller()
    {
        string input = _fixtures.Write("heavy.jpg", 1200, 900);
        var result = await Fixtures.Run(_catalog.Find("tool.compress")!, input);
        Assert.True(new FileInfo(Assert.Single(result.Outputs)).Length < new FileInfo(input).Length);
    }

    [Fact]
    public async Task A_broken_file_fails_cleanly()
    {
        string input = Path.Combine(_fixtures.Dir, "broken.png");
        File.WriteAllBytes(input, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3]);

        var result = await Fixtures.Run(_catalog.Find("image.webp")!, input);

        Assert.Empty(result.Outputs);
        Assert.StartsWith("Couldn't crack broken.png", Assert.Single(result.Failures).Message);
        Assert.Equal([input], Directory.GetFiles(_fixtures.Dir));
    }

    [Fact]
    public async Task Heic_converts_through_windows_codecs()
    {
        string? input = HeicInput("phone.heic", 800, 600);
        if (input is null) return;
        Assert.Equal(FileKind.Heic, FileSniffer.Detect(input));
        string before = Fixtures.Hash(input);
        var probe = ImageEstimator.Probe(input)!;

        var result = await Fixtures.Run(_catalog.Find("image.webp")!, input);

        Assert.True(result.Succeeded, string.Join("; ", result.Failures.Select(f => f.Message)));
        using var image = VipsImage.NewFromFile(Assert.Single(result.Outputs));
        Assert.Equal((probe.Width, probe.Height), (image.Width, image.Height));
        Assert.Equal(before, Fixtures.Hash(input));
    }

    /// <summary>
    /// An HEIC built on this machine, else the real sample, else a loud skip. Also skips loudly, after
    /// checking Swivel says so in plain words, when Windows has no HEVC decoder.
    /// </summary>
    private string? HeicInput(string name, int width, int height)
    {
        string? input = _fixtures.WriteHeic(name, width, height);
        if (input is null && Fixtures.HeicSample() is string sample) input = _fixtures.Import(sample);
        if (input is null)
        {
            output.WriteLine("SKIPPED: no HEIF encoder here and no tests/fixtures/sample.heic. HEIC is NOT verified.");
            return null;
        }
        try
        {
            using var _ = WicDecoder.Decode(input);
        }
        catch (CodecMissingException ex)
        {
            output.WriteLine($"SKIPPED: Windows has no working HEVC decoder ({ex.Message}). HEIC is NOT verified.");
            return null;
        }
        return input;
    }

    [Fact]
    public async Task Missing_hevc_codec_gives_a_plain_reason()
    {
        if (Fixtures.HeicSample() is not string sample) return;
        string input = _fixtures.Import(sample);
        var result = await Fixtures.Run(_catalog.Find("image.webp")!, input);
        if (result.Succeeded) return; // codec present: nothing to check
        Assert.Equal("Windows needs the HEIF and HEVC extensions to open HEIC photos.", Assert.Single(result.Failures).Message);
    }

    [Fact]
    public void Estimates_are_in_a_sane_range()
    {
        string input = _fixtures.Write("estimate.jpg", 1600, 1200);
        var probe = ImageEstimator.Probe(input)!;
        Assert.Equal((1600, 1200), (probe.Width, probe.Height));

        long? webp = ImageEstimator.Estimate(probe, _catalog.Find("image.webp")!);
        long? web = ImageEstimator.Estimate(probe, _catalog.Find("preset.web-image")!);
        Assert.InRange(webp!.Value, 100_000, 1_000_000);
        Assert.True(web <= webp);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public async Task Perf_heic_to_webp_under_a_second()
    {
        // About 5 MB as HEIC from a modern phone: 4032 × 3024.
        string? input = HeicInput("perf.heic", 4032, 3024);
        if (input is null) return;
        output.WriteLine($"Fixture: {new FileInfo(input).Length / 1e6:F1} MB");
        await Fixtures.Run(_catalog.Find("image.webp")!, _fixtures.Write("warm.png", 64, 64)); // load libvips

        var clock = Stopwatch.StartNew();
        var result = await Fixtures.Run(_catalog.Find("image.webp")!, input);
        clock.Stop();

        output.WriteLine($"HEIC → WebP: {clock.ElapsedMilliseconds} ms");
        Assert.True(result.Succeeded);
        Assert.True(clock.ElapsedMilliseconds < 1000, $"took {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    [Trait("Category", "Perf")]
    public async Task Perf_fifty_photos_under_thirty_seconds()
    {
        string first = _fixtures.Write("batch-0.jpg", 4032, 3024);
        var inputs = new List<string> { first };
        for (int i = 1; i < 50; i++)
        {
            string copy = Path.Combine(_fixtures.Dir, $"batch-{i}.jpg");
            File.Copy(first, copy);
            inputs.Add(copy);
        }

        var clock = Stopwatch.StartNew();
        var result = await Fixtures.Run(_catalog.Find("image.webp")!, [.. inputs]);
        clock.Stop();

        output.WriteLine($"50 × 12 MP JPEG → WebP: {clock.Elapsed.TotalSeconds:F1} s");
        Assert.Equal(50, result.Outputs.Count);
        Assert.True(clock.Elapsed.TotalSeconds < 30, $"took {clock.Elapsed.TotalSeconds:F1} s");
    }

    public void Dispose() => _fixtures.Dispose();
}
