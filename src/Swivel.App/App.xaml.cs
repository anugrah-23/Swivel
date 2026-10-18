using System.Windows;
using System.Windows.Threading;
using H.NotifyIcon;
using Serilog;
using Swivel.App.Feedback;
using Swivel.App.Overlay;
using Swivel.App.Services;
using Swivel.App.Shell;
using Swivel.App.Theme;
using Swivel.Core.Settings;
using Swivel.Native;

namespace Swivel.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private SwivelServices? _services;
    private OverlayWindow? _overlay;
    private TaskbarIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, @"Local\Swivel.SingleInstance", out bool created);
        if (!created)
        {
            Shutdown();
            return;
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(Path.Combine(AppPaths.Logs, "swivel-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .CreateLogger();
        DispatcherUnhandledException += OnUnhandledException;

        // Swivel's surfaces are small and short-lived; software rendering keeps the GPU driver's
        // ~50 MB out of an always-running tray process. SWIVEL_GPU_RENDER=1 restores hardware rendering.
        if (Environment.GetEnvironmentVariable("SWIVEL_GPU_RENDER") != "1")
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        base.OnStartup(e);

        _services = new SwivelServices(Dispatcher);
        _services.Settings.Changed += settings => Dispatcher.InvokeAsync(() => Apply(settings));
        Apply(_services.Settings.Current);

        if (e.Args is ["--snapshot", var outDir, .. var files] && files.Length > 0)
        {
            Snapshot(outDir, files);
            return;
        }

        var feedback = new JobFeedback(_services);
        _overlay = new OverlayWindow(_services);
        _overlay.Dropped += feedback.OnDrop;
        _overlay.Prewarm();

        var overlay = _overlay;
        TriggerHook.Signal += (signal, timestamp) =>
            overlay.Dispatcher.InvokeAsync(() => overlay.OnSignal(signal, timestamp), DispatcherPriority.Send);
        try
        {
            TriggerHook.Start();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Keyboard hook failed to install");
            MessageBox.Show("Swivel couldn't watch for the trigger key, so it has to close.\n\n" + ex.Message, "Swivel");
            Quit();
            return;
        }

        _tray = Tray.Create(_services, Quit);
        _services.GroupFinished += (_, _) => ScheduleIdleTrim();
        ScheduleIdleTrim();
        Log.Information("Swivel started (elevated: {Elevated})", Environment.IsPrivilegedProcess);
    }

    private DispatcherTimer? _idleTrim;

    /// <summary>
    /// Swivel spends nearly all its life idle in the tray, so once startup or a job has settled it
    /// hands back the garbage collector's slack rather than holding it until the next allocation.
    /// </summary>
    private void ScheduleIdleTrim()
    {
        _idleTrim ??= new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.ApplicationIdle, (_, _) =>
        {
            _idleTrim!.Stop();
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }, Dispatcher);
        _idleTrim.Stop();
        _idleTrim.Start();
    }

    /// <summary>Renders each mode of the wheel for the given files to PNGs, then exits.</summary>
    private void Snapshot(string outDir, string[] files)
    {
        Directory.CreateDirectory(outDir);
        var overlay = new OverlayWindow(_services!);
        foreach (var mode in Enum.GetValues<Core.Catalog.WheelMode>())
            overlay.RenderSnapshot(files, mode, active: 0, Path.Combine(outDir, $"wheel-{mode.ToString().ToLowerInvariant()}.png"));
        overlay.RenderSnapshot(files, Core.Catalog.WheelMode.Convert, active: -1, Path.Combine(outDir, "wheel-idle.png"));
        overlay.Close();
        Quit();
    }

    private static void Apply(AppSettings settings)
    {
        TriggerHook.SetTrigger(TriggerKeys.Parse(settings.Trigger));
        Palette.UserAnimations = settings.Animations;
        Log.Information("Settings applied: trigger {Trigger}, output {Output}", settings.Trigger, settings.Output.Location);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI exception");
        e.Handled = true;
    }

    private async void Quit()
    {
        TriggerHook.Stop();
        _tray?.Dispose();
        if (_services != null) await _services.DisposeAsync();
        Log.Information("Swivel quit");
        await Log.CloseAndFlushAsync();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
