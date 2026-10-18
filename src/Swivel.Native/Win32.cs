using System.Runtime.InteropServices;

namespace Swivel.Native;

/// <summary>Raw Win32 declarations shared by the native helpers and the app.</summary>
public static partial class Win32
{
    public const int WH_KEYBOARD_LL = 13;
    public const uint WM_QUIT = 0x0012;

    public const uint VK_LBUTTON = 0x01, VK_RBUTTON = 0x02, VK_RETURN = 0x0D, VK_ESCAPE = 0x1B,
        VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28,
        VK_LMENU = 0xA4, VK_RMENU = 0xA5;

    public const int GWL_EXSTYLE = -20;
    public const nint WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
    public const uint SWP_NOACTIVATE = 0x0010;
    public static readonly nint HWND_TOPMOST = -1;
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const int MDT_EFFECTIVE_DPI = 0;
    public const int SM_SWAPBUTTON = 23;

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public nuint dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public nint hwnd; public uint message; public nuint wParam; public nint lParam; public uint time; public POINT pt; }

    [LibraryImport("user32.dll", SetLastError = true)]
    public static unsafe partial nint SetWindowsHookExW(int idHook, delegate* unmanaged<int, nint, nint, nint> lpfn, nint hmod, uint threadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWindowsHookEx(nint hhk);

    [LibraryImport("user32.dll")]
    public static partial nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial int GetMessageW(out MSG msg, nint hwnd, uint min, uint max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostThreadMessageW(uint threadId, uint msg, nuint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandleW(string? name);

    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int vk);

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT p);

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromPoint(POINT pt, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfoW(nint hMonitor, ref MONITORINFO mi);

    [LibraryImport("shcore.dll")]
    public static partial int GetDpiForMonitor(nint hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    /// <summary>True while the primary mouse button is held, honouring swapped buttons.</summary>
    public static bool PrimaryButtonDown()
    {
        uint vk = GetSystemMetrics(SM_SWAPBUTTON) != 0 ? VK_RBUTTON : VK_LBUTTON;
        return (GetAsyncKeyState((int)vk) & 0x8000) != 0;
    }

    /// <summary>The work area and effective DPI scale of the monitor under a point.</summary>
    public static (RECT Work, double Scale) MonitorAt(POINT pt)
    {
        nint monitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfoW(monitor, ref info);
        double scale = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        return (info.rcWork, scale);
    }
}
