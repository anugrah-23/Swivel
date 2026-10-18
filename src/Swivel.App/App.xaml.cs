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
    private SingleInstance? _instance;
    private SwivelServices? _services;
    private OverlayWindow? _overlay;
    private JobFeedback? _feedback;
    private HotkeyHost? _hotkeys;
    private TaskbarIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Tools that render and exit don't take part in single-instance.
        bool tool = e.Args is ["--snapshot", ..] or ["--export-assets", ..];
        string[] wheelPaths = e.Args is ["--wheel", .. var rest] ? rest : [];
        if (!tool)
        {
            _instance = SingleInstance.Claim(wheelPaths, background: e.Args.Contains("--background"));
            if (_instance is null)
            {
                Shutdown();
                return;
            }
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
        Palette.InstallBrushes(Resources, SystemParameters.HighContrast || Environment.GetEnvironmentVariable("SWIVEL_HIGH_CONTRAST") == "1");

        if (e.Args is ["--snapshot", var outDir, .. var files] && files.Length > 0)
        {
            Snapshot(outDir, files);
            return;
        }
        if (e.Args is ["--export-assets", var assetDir])
        {
            Logo.ExportAssets(assetDir);
            Quit();
            return;
        }

        _state = new ShellState(_services, Dispatcher);
        _feedback = new JobFeedback(_services);
        _overlay = new OverlayWindow(_services);
        _overlay.Dropped += drop =>
        {
            // While onboarding's practice step is up, drops are caught there and nothing runs.
            if (_onboarding?.Practising == true) _onboarding.Caught(drop.Operation);
            else _feedback.OnDrop(drop);
        };
        _overlay.Opened += () => _onboarding?.WheelOpened();
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

        _hotkeys = new HotkeyHost();
        _services.Settings.Changed += settings => Dispatcher.InvokeAsync(() => Apply(settings));
        Apply(_services.Settings.Current);

        _instance!.WheelRequested += paths => Dispatcher.InvokeAsync(() => OpenWheel(paths, "Explorer menu"));
        _instance.ShowRequested += () => Dispatcher.InvokeAsync(OpenSettings);
        _instance.Listen();
        if (wheelPaths.Length > 0) _instance.Gather(wheelPaths);

        _flyout = new FlyoutWindow(_services, _state, OpenSettings, Quit);
        _tray = Tray.Create(_flyout);
        _services.GroupFinished += (_, _) => ScheduleIdleTrim();
        ScheduleIdleTrim();
        Log.Information("Swivel started (elevated: {Elevated}, isolated: {Isolated})", Environment.IsPrivilegedProcess, AppPaths.Isolated);

        if (Updates.Enabled && !AppPaths.Isolated)
        {
            _updateTimer = new DispatcherTimer(TimeSpan.FromHours(24), DispatcherPriority.Background, async (_, _) => await CheckForUpdatesAsync(), Dispatcher);
            _ = Dispatcher.InvokeAsync(async () => { await Task.Delay(TimeSpan.FromMinutes(1)); await CheckForUpdatesAsync(); });
        }

        if (!_state.Settings.OnboardingDone && !AppPaths.Isolated)
        {
            _onboarding = new OnboardingWindow(_state);
            _onboarding.Closed += (_, _) => _onboarding = null;
            _onboarding.Show();
        }
        else if (!e.Args.Contains("--background") && wheelPaths.Length == 0 && !AppPaths.Isolated)
            _feedback.Info("Swivel is on.", $"It lives in your tray. Drag a file and tap {TriggerKeys.Label(_state.Settings.Trigger)}.");
    }

    private DispatcherTimer? _updateTimer;

    private async Task CheckForUpdatesAsync()
    {
        if (await Updates.CheckAndDownloadAsync() is { } version)
            _feedback?.Offer($"Swivel {version} is ready.", "Restart Swivel to switch over. Jobs in progress finish first.", "Restart now", Updates.RestartIntoUpdate);
    }

    private ShellState? _state;
    private FlyoutWindow? _flyout;
    private SettingsWindow? _settingsWindow;
    private OnboardingWindow? _onboarding;
    private (string Selection, string Clipboard)? _registeredHotkeys;

    private void OpenSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_state!, _hotkeys);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        Win32.ForceForeground(new System.Windows.Interop.WindowInteropHelper(_settingsWindow).Handle);
    }

    private void OpenWheel(IReadOnlyList<string> paths, string route)
    {
        Log.Information("Wheel via {Route} for {Count} item(s)", route, paths.Count);
        _overlay?.OpenFor(paths);
    }

    private void Apply(AppSettings settings)
    {
        TriggerHook.SetTrigger(TriggerKeys.Parse(settings.Trigger));
        Palette.UserAnimations = settings.Animations;

        // An isolated instance (tests, side-by-side builds) must not take over the real menu, hotkeys or Run key.
        if (!AppPaths.Isolated)
        {
            ExplorerMenu.Apply(settings.ExplorerMenu, SignIn.LauncherPath);
            SignIn.Apply(settings.LaunchAtSignIn);
            if (_hotkeys != null && _registeredHotkeys != (settings.SelectionHotkey, settings.ClipboardHotkey))
            {
                _registeredHotkeys = (settings.SelectionHotkey, settings.ClipboardHotkey);
                _hotkeys.Clear();
                RegisterSelectionHotkey(settings.SelectionHotkey);
                if (!_hotkeys.Register(settings.ClipboardHotkey, OnClipboardHotkey))
                    _feedback?.Info($"{settings.ClipboardHotkey} is taken.", "Another app owns that shortcut. Pick a new one in Settings.");
            }
        }
        Log.Information("Settings applied: trigger {Trigger}, output {Output}", settings.Trigger, settings.Output.Location);
    }

    /// <summary>
    /// Registers the selection shortcut. If the default is owned by another app, the next free
    /// fallback is used and the user is told; a shortcut the user chose is never swapped silently.
    /// </summary>
    private void RegisterSelectionHotkey(string wanted)
    {
        if (_hotkeys is null || _hotkeys.Register(wanted, OnSelectionHotkey)) return;
        if (wanted == AppSettings.DefaultSelectionHotkey)
        {
            foreach (string fallback in AppSettings.SelectionHotkeyFallbacks)
            {
                if (!_hotkeys.Register(fallback, OnSelectionHotkey)) continue;
                Log.Information("Selection shortcut {Wanted} was taken; using {Fallback}", wanted, fallback);
                _feedback?.Info($"Selection wheel: {fallback}", $"Another app already uses {wanted}, so Swivel picked {fallback}.");
                return;
            }
        }
        _feedback?.Info($"{wanted} is taken.", "Another app owns that shortcut. Pick a new one in Settings.");
    }

    private void OnSelectionHotkey()
    {
        if (_state?.IsListening == false) return;
        SelectionHotkey();
    }

    private void SelectionHotkey()
    {
        var paths = ExplorerSelection.ForForeground();
        if (paths.Count == 0) _feedback?.Info("Nothing selected.", "Select files in Explorer or on the desktop, then press the shortcut.");
        else OpenWheel(paths, "selection hotkey");
    }

    private void OnClipboardHotkey()
    {
        if (_state?.IsListening == false) return;
        ClipboardHotkey();
    }

    private void ClipboardHotkey()
    {
        var paths = ClipboardRoute.Materialize();
        if (paths.Count == 0) _feedback?.Info("Clipboard's empty.", "Copy some files, a picture or text first.");
        else OpenWheel(paths, "clipboard hotkey");
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
        foreach (string panel in new[] { "resize", "crop", "trim" })
            PanelWindow.Snapshot(panel, files[0], Path.Combine(outDir, $"panel-{panel}.png"));
        Shell.Snapshots.Windows(outDir, _services!, new ShellState(_services!, Dispatcher));
        Quit();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI exception");
        e.Handled = true;
    }

    private async void Quit()
    {
        TriggerHook.Stop();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        if (_services != null) await _services.DisposeAsync();
        Log.Information("Swivel quit");
        await Log.CloseAndFlushAsync();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
