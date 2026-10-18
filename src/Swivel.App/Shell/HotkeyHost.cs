using System.Windows.Interop;
using System.Windows.Input;
using Serilog;
using Swivel.Native;

namespace Swivel.App.Shell;

/// <summary>Global hotkeys (Ctrl+Alt+Space, Ctrl+Alt+V) on a message-only window.</summary>
public sealed class HotkeyHost : IDisposable
{
    private readonly HwndSource _window;
    private readonly Dictionary<int, Action> _handlers = [];
    private int _nextId = 1;

    public HotkeyHost()
    {
        var parameters = new HwndSourceParameters("Swivel hotkeys") { ParentWindow = new nint(-3), WindowStyle = 0 }; // HWND_MESSAGE
        _window = new HwndSource(parameters);
        _window.AddHook(WndProc);
    }

    /// <summary>Registers a gesture such as "Ctrl+Alt+Space". Returns false if another app owns it.</summary>
    public bool Register(string gesture, Action handler)
    {
        if (!TryParse(gesture, out uint modifiers, out uint vk))
        {
            Log.Warning("Couldn't read hotkey {Gesture}", gesture);
            return false;
        }
        int id = _nextId++;
        if (!Win32.RegisterHotKey(_window.Handle, id, modifiers | Win32.MOD_NOREPEAT, vk))
        {
            Log.Warning("Hotkey {Gesture} is already taken by another app", gesture);
            return false;
        }
        _handlers[id] = handler;
        return true;
    }

    public void Clear()
    {
        foreach (int id in _handlers.Keys) Win32.UnregisterHotKey(_window.Handle, id);
        _handlers.Clear();
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY && _handlers.TryGetValue((int)wParam, out var handler))
        {
            handled = true;
            handler();
        }
        return 0;
    }

    public static bool TryParse(string gesture, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        foreach (string raw in gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= Win32.MOD_CONTROL; break;
                case "alt": modifiers |= Win32.MOD_ALT; break;
                case "shift": modifiers |= Win32.MOD_SHIFT; break;
                case "win": modifiers |= Win32.MOD_WIN; break;
                default:
                    if (!Enum.TryParse(raw, ignoreCase: true, out Key key) && !(raw.Length == 1 && Enum.TryParse(raw.ToUpperInvariant(), out key)))
                        return false;
                    vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }
        return vk != 0 && modifiers != 0;
    }

    public void Dispose()
    {
        Clear();
        _window.Dispose();
    }
}
