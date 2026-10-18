using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace Swivel.UiTests;

/// <summary>
/// End to end through the real app: the Explorer-menu route (<c>--wheel</c>), wedges invoked by UI
/// Automation and by keyboard, the toast's Undo, Esc, and the single-instance hand-off.
/// These drive the real desktop, so they run one at a time.
/// </summary>
[Collection("Desktop")]
public class WheelFlowTests
{
    [Fact]
    public void Invoking_a_wedge_converts_and_undo_recycles_the_output()
    {
        using var session = new SwivelSession();
        string photo = session.Png("photo.png");
        string webp = Path.Combine(session.Work, "photo.webp");

        session.Launch("--wheel", photo);
        var wedge = session.Find("Convert to WEBP, smaller");
        Assert.NotNull(wedge);
        wedge.Patterns.Invoke.Pattern.Invoke();

        Assert.True(SwivelSession.WaitFor(() => File.Exists(webp)), "photo.webp never appeared");
        Assert.True(File.Exists(photo));

        var undo = session.Find("Undo");
        Assert.NotNull(undo);
        undo.Patterns.Invoke.Pattern.Invoke();
        Assert.True(SwivelSession.WaitFor(() => !File.Exists(webp)), "Undo didn't remove photo.webp");
        Assert.True(File.Exists(photo));
    }

    [Fact]
    public void Arrow_keys_and_enter_pick_a_wedge()
    {
        using var session = new SwivelSession();
        string photo = session.Png("pick.png");

        session.Launch("--wheel", photo);
        Assert.NotNull(session.Find("Convert to WEBP, smaller"));

        // The wheel opens with the first wedge (WEBP) selected; one step right is JPG.
        Keyboard.Type(VirtualKeyShort.RIGHT);
        Thread.Sleep(150);
        Keyboard.Type(VirtualKeyShort.ENTER);

        Assert.True(SwivelSession.WaitFor(() => File.Exists(Path.Combine(session.Work, "pick.jpg"))), "pick.jpg never appeared");
    }

    [Fact]
    public void Escape_closes_the_wheel_and_does_nothing()
    {
        using var session = new SwivelSession();
        string photo = session.Png("quiet.png");

        session.Launch("--wheel", photo);
        Assert.NotNull(session.Find("Convert to WEBP, smaller"));
        Keyboard.Type(VirtualKeyShort.ESCAPE);

        Assert.True(SwivelSession.WaitFor(() => session.Find("Convert to WEBP, smaller", TimeSpan.Zero) is null), "wheel stayed open");
        Thread.Sleep(500);
        Assert.Equal([photo], Directory.GetFiles(session.Work));
    }

    [Fact]
    public void A_second_launch_hands_its_files_to_the_running_swivel()
    {
        using var session = new SwivelSession();
        string photo = session.Png("handoff.png");

        var first = session.Launch();
        Assert.True(SwivelSession.WaitFor(() => File.Exists(Path.Combine(session.DataDir, "history.db"))), "first instance didn't start");

        var second = session.Launch("--wheel", photo);
        Assert.True(second.WaitForExit(5000), "second instance should exit after handing off");
        Assert.False(first.HasExited);
        Assert.NotNull(session.Find("Convert to WEBP, smaller"));
    }

    [Fact]
    public void Tools_mode_shows_the_panel_wedges_for_images()
    {
        using var session = new SwivelSession();
        string photo = session.Png("tools.png");

        session.Launch("--wheel", photo);
        Assert.NotNull(session.Find("Convert to WEBP, smaller"));
        Keyboard.Type(VirtualKeyShort.ALT); // the trigger cycles to Tools while the wheel is open

        Assert.NotNull(session.Find("Resize, fit"));
        Assert.NotNull(session.Find("Crop, smart"));
        Keyboard.Type(VirtualKeyShort.ESCAPE);
    }
}

[CollectionDefinition("Desktop", DisableParallelization = true)]
public class DesktopCollection;
