using System.Windows.Threading;
using Swivel.Core.Settings;
using Swivel.Native;

namespace Swivel.App.Services;

/// <summary>
/// Whether Swivel is listening (the flyout's switch and Pause), and the one place the windows change
/// settings from. Saving applies immediately; hand edits to settings.json still apply live too.
/// </summary>
public sealed class ShellState
{
    private readonly SwivelServices _services;
    private readonly DispatcherTimer _resume;

    public ShellState(SwivelServices services, Dispatcher dispatcher)
    {
        _services = services;
        _resume = new DispatcherTimer(DispatcherPriority.Normal, dispatcher);
        _resume.Tick += (_, _) => Resume();
        services.Settings.Changed += _ => dispatcher.InvokeAsync(Refresh);
        Refresh();
    }

    public AppSettings Settings => _services.Settings.Current;

    public DateTime? PausedUntil { get; private set; }

    /// <summary>On and not paused: the trigger and shortcuts work.</summary>
    public bool IsListening => Settings.Enabled && PausedUntil is null;

    /// <summary>Raised on the UI thread whenever the switch, pause or settings change.</summary>
    public event Action? Changed;

    public void Update(Func<AppSettings, AppSettings> change) => _services.Settings.Save(change(Settings));

    public void SetEnabled(bool enabled)
    {
        PausedUntil = null;
        _resume.Stop();
        Update(s => s with { Enabled = enabled });
    }

    public void Pause(TimeSpan duration)
    {
        PausedUntil = DateTime.Now + duration;
        _resume.Interval = duration;
        _resume.Start();
        Refresh();
    }

    public void Resume()
    {
        _resume.Stop();
        PausedUntil = null;
        Refresh();
    }

    public string Status => !Settings.Enabled ? "Off. The trigger is left alone."
        : PausedUntil is { } until ? $"Paused until {until:t}."
        : $"On. Drag a file, tap {TriggerKeys.Label(Settings.Trigger)}.";

    private void Refresh()
    {
        TriggerHook.Enabled = IsListening;
        Changed?.Invoke();
    }
}
