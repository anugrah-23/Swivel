using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Swivel.Native;

namespace Swivel.App.Feedback;

/// <summary>A transparent, topmost window that takes clicks but never steals focus from the user's app.</summary>
public class FloatingWindow : Window
{
    public FloatingWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SourceInitialized += (_, _) =>
        {
            nint hwnd = new WindowInteropHelper(this).Handle;
            nint style = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE);
            Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, style | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW);
        };
    }

    /// <summary>Moves the window so its centre sits on a physical screen point.</summary>
    protected void CentreOn(Point physical, double logicalWidth, double logicalHeight, double scale)
    {
        nint hwnd = new WindowInteropHelper(this).EnsureHandle();
        int width = (int)Math.Round(logicalWidth * scale), height = (int)Math.Round(logicalHeight * scale);
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, (int)(physical.X - width / 2.0), (int)(physical.Y - height / 2.0),
            width, height, Win32.SWP_NOACTIVATE);
    }
}
