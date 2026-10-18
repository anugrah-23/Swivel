using System.Runtime.InteropServices;
using System.Text;

namespace Swivel.Native;

/// <summary>
/// The files selected in the foreground Explorer window (the active tab on Windows 11) or on the
/// desktop, read through the Shell's own automation objects.
/// </summary>
public static partial class ExplorerSelection
{
    private const int SWC_DESKTOP = 8, SWFO_NEEDDISPATCH = 1;
    private static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IID_IShellBrowser = new("000214E2-0000-0000-C000-000000000046");

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid service, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    }

    /// <summary>IShellBrowser; only its first method, IOleWindow::GetWindow, is needed.</summary>
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        [PreserveSig]
        int GetWindow(out nint hwnd);
    }

    /// <summary>Call from an STA thread. Returns nothing when the foreground isn't Explorer or the desktop.</summary>
    public static IReadOnlyList<string> ForForeground()
    {
        nint foreground = Win32.GetForegroundWindow();
        string cls = ClassName(foreground);
        if (cls is not ("CabinetWClass" or "ExploreWClass" or "Progman" or "WorkerW")) return [];

        if (Type.GetTypeFromProgID("Shell.Application") is not { } type) return [];
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic windows = shell.Windows();
            if (cls is "Progman" or "WorkerW")
            {
                object location = 0, root = Type.Missing;
                dynamic desktop = windows.FindWindowSW(ref location, ref root, SWC_DESKTOP, out int _, SWFO_NEEDDISPATCH);
                return Selected(desktop.Document);
            }

            // Windows 11 tabs share one top-level window; Explorer keeps the active tab's view first.
            nint activeTab = FindWindowExW(foreground, 0, "ShellTabWindowClass", null);
            foreach (dynamic window in windows)
            {
                try
                {
                    if ((nint)(long)window.HWND != foreground) continue;
                    if (activeTab != 0 && TabWindow(window) is nint tab && tab != activeTab) continue;
                    return Selected(window.Document);
                }
                catch (COMException) { }
                finally { Marshal.ReleaseComObject(window); }
            }
            return [];
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException)
        {
            return [];
        }
        finally
        {
            Marshal.ReleaseComObject(shell);
        }
    }

    private static nint? TabWindow(object browser)
    {
        if (browser is not IServiceProvider provider) return null;
        Guid service = SID_STopLevelBrowser, iid = IID_IShellBrowser;
        if (provider.QueryService(ref service, ref iid, out object result) != 0) return null;
        try { return ((IShellBrowser)result).GetWindow(out nint hwnd) == 0 ? hwnd : null; }
        finally { Marshal.ReleaseComObject(result); }
    }

    private static List<string> Selected(dynamic document)
    {
        var paths = new List<string>();
        foreach (dynamic item in document.SelectedItems())
        {
            string? path = item.Path;
            if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path))) paths.Add(path);
        }
        return paths;
    }

    private static unsafe string ClassName(nint hwnd)
    {
        char* buffer = stackalloc char[256];
        int length = GetClassNameW(hwnd, buffer, 256);
        return new string(buffer, 0, Math.Max(0, length));
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowExW(nint parent, nint after, string? className, string? title);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    private static unsafe partial int GetClassNameW(nint hwnd, char* buffer, int max);
}
