using System.Diagnostics;
using System.Windows.Controls;
using H.NotifyIcon;
using Serilog;
using Swivel.App.Services;
using Swivel.Core.History;

namespace Swivel.App.Shell;

/// <summary>M1 tray icon: status, recent jobs with Undo, settings file, logs and Quit. The full flyout lands in M4.</summary>
internal static class Tray
{
    private const int RecentCount = 5;

    public static TaskbarIcon Create(SwivelServices services, Action quit)
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) => Build(menu, services, quit);
        Build(menu, services, quit);

        var icon = new TaskbarIcon
        {
            ToolTipText = "Swivel",
            Icon = Logo.CreateIcon(32),
            ContextMenu = menu,
        };
        icon.ForceCreate(false);
        return icon;
    }

    private static void Build(ContextMenu menu, SwivelServices services, Action quit)
    {
        menu.Items.Clear();
        string trigger = TriggerKeys.Label(services.Settings.Current.Trigger);
        menu.Items.Add(new MenuItem { Header = $"Swivel is on · drag a file, tap {trigger}", IsEnabled = false });
        menu.Items.Add(new Separator());

        var recent = new MenuItem { Header = "Recent jobs" };
        IReadOnlyList<HistoryEntry> entries = [];
        try { entries = services.History.Recent(RecentCount); }
        catch (Exception ex) { Log.Warning(ex, "Couldn't read history"); }
        foreach (var entry in entries)
        {
            string what = entry.Inputs.Count == 1 ? Path.GetFileName(entry.Inputs[0]) : $"{entry.Inputs.Count} files";
            bool canUndo = !entry.Undone && services.History.CanUndo(entry.GroupId);
            var item = new MenuItem
            {
                Header = $"{entry.Label} · {what}{(entry.Undone ? " (undone)" : "")}",
                InputGestureText = canUndo ? "Undo" : "",
                IsEnabled = canUndo,
            };
            item.Click += (_, _) => services.Undo(entry.GroupId);
            recent.Items.Add(item);
        }
        if (recent.Items.Count == 0) recent.Items.Add(new MenuItem { Header = "Nothing yet", IsEnabled = false });
        menu.Items.Add(recent);

        var settings = new MenuItem { Header = "Edit settings file" };
        settings.Click += (_, _) => OpenSettingsFile(services);
        menu.Items.Add(settings);

        var logs = new MenuItem { Header = "Open logs folder" };
        logs.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Logs}\"") { UseShellExecute = true });
        menu.Items.Add(logs);

        menu.Items.Add(new Separator());
        var exit = new MenuItem { Header = "Quit Swivel" };
        exit.Click += (_, _) => quit();
        menu.Items.Add(exit);
    }

    private static void OpenSettingsFile(SwivelServices services)
    {
        if (!File.Exists(AppPaths.Settings)) services.Settings.Save(services.Settings.Current);
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{AppPaths.Settings}\"") { UseShellExecute = true });
    }
}
