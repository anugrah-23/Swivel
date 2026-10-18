using H.NotifyIcon;

namespace Swivel.App.Shell;

/// <summary>The tray icon. Left or right click opens the flyout.</summary>
internal static class Tray
{
    public static TaskbarIcon Create(FlyoutWindow flyout)
    {
        var icon = new TaskbarIcon
        {
            ToolTipText = "Swivel",
            Icon = Logo.CreateIcon(32),
            NoLeftClickDelay = true,
        };
        icon.TrayLeftMouseUp += (_, _) => flyout.Toggle();
        icon.TrayRightMouseUp += (_, _) => flyout.Toggle();
        icon.ForceCreate(false);
        return icon;
    }
}
