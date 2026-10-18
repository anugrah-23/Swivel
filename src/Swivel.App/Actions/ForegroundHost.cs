using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Swivel.Native;

namespace Swivel.App.Actions;

/// <summary>
/// An invisible, focusable window that owns system UI Swivel opens (the folder picker, the share
/// sheet). Swivel's own surfaces never take focus, so these need a proper owner in the foreground.
/// </summary>
internal sealed class ForegroundHost : Window
{
    private ForegroundHost()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        Width = Height = 1;
        var area = SystemParameters.WorkArea;
        Left = area.Left + area.Width / 2;
        Top = area.Top + area.Height / 3;
        WindowStartupLocation = WindowStartupLocation.Manual;
    }

    public nint Handle => new WindowInteropHelper(this).Handle;

    public static ForegroundHost Open()
    {
        var host = new ForegroundHost();
        host.Show();
        Win32.ForceForeground(host.Handle);
        host.Activate();
        return host;
    }
}
