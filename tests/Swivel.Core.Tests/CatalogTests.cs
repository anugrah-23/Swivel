using Swivel.Core.Catalog;
using Swivel.Core.Files;

namespace Swivel.Core.Tests;

public class CatalogTests
{
    private readonly OperationCatalog _catalog = new();

    [Fact]
    public void Never_offers_converting_to_the_same_format()
    {
        var ops = _catalog.For([FileKind.Png], WheelMode.Convert);
        Assert.DoesNotContain(ops, o => o.Id == "image.png");
        Assert.Contains(ops, o => o.Id == "image.webp");
    }

    [Fact]
    public void Mixed_selection_only_shows_options_valid_for_every_item()
    {
        var ops = _catalog.For([FileKind.Jpeg, FileKind.Png], WheelMode.Convert);
        Assert.DoesNotContain(ops, o => o.Id is "image.png" or "image.jpg");
        Assert.Contains(ops, o => o.Id == "image.webp");
    }

    [Fact]
    public void Opens_on_the_first_mode_with_something_valid()
    {
        // Unknown files can't convert, but anything can be archived.
        Assert.Equal(WheelMode.Tools, _catalog.OpeningMode([FileKind.Unknown]));
        Assert.Equal(WheelMode.Tools, _catalog.OpeningMode([FileKind.Png, FileKind.Mp3]));
        Assert.Equal(WheelMode.Convert, _catalog.OpeningMode([FileKind.Heic]));
        Assert.Equal(WheelMode.Convert, _catalog.OpeningMode([FileKind.Mp4, FileKind.Mp3]));
    }

    [Fact]
    public void Video_and_audio_mixed_only_offer_audio_targets()
    {
        var ops = _catalog.For([FileKind.Mp4, FileKind.Wav], WheelMode.Convert).Select(o => o.Id).ToArray();
        Assert.DoesNotContain("video.mp4", ops);
        Assert.Contains("audio.mp3", ops);
        Assert.DoesNotContain("audio.wav", ops);
    }

    [Fact]
    public void Combining_wedges_respect_input_counts()
    {
        Assert.DoesNotContain(_catalog.For([FileKind.Pdf], WheelMode.Tools), o => o.Id == "pdf.merge");
        Assert.Contains(_catalog.For([FileKind.Pdf, FileKind.Pdf], WheelMode.Tools), o => o.Id == "pdf.merge");
        Assert.DoesNotContain(_catalog.For([FileKind.Txt, FileKind.Txt], WheelMode.Tools), o => o.Id == "archive.gz");
    }

    [Fact]
    public void Most_used_operations_come_first()
    {
        var usage = new Dictionary<string, int> { ["image.avif"] = 5, ["image.pdf"] = 2 };
        var ops = _catalog.For([FileKind.Jpeg], WheelMode.Convert, usage);
        Assert.Equal(["image.avif", "image.pdf", "image.webp"], ops.Take(3).Select(o => o.Id));
    }

    [Fact]
    public void Overflow_turns_the_eighth_wedge_into_more()
    {
        var ops = _catalog.For([FileKind.Jpeg], WheelMode.Convert);
        Assert.True(ops.Count > 8);
        var rings = OperationCatalog.Rings(ops);
        Assert.Equal(2, rings.Count);
        Assert.All(rings, r => Assert.Null(r[^1]));
        Assert.Equal(ops.Count, rings.Sum(r => r.Count(o => o != null)));
    }

    [Fact]
    public void Small_lists_fit_one_ring_without_more()
    {
        var rings = OperationCatalog.Rings(_catalog.For([FileKind.Jpeg], WheelMode.Tools));
        Assert.Single(rings);
        Assert.DoesNotContain(null, rings[0]);
    }

    [Fact]
    public void Automation_names_read_naturally() =>
        Assert.Equal("Convert to WEBP, smaller", _catalog.Find("image.webp")!.AutomationName);
}
