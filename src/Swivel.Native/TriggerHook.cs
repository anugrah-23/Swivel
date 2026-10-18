using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static Swivel.Native.Win32;

namespace Swivel.Native;

public enum HookSignal { TriggerTap, Left, Right, Up, Down, Enter, Escape }

/// <summary>
/// Low-level keyboard hook on its own thread. It reacts only to the trigger key while the primary
/// button is held (or while the overlay is open), swallowing that tap so drag sources and menu bars
/// never see it. No other keys are inspected unless the overlay is open.
/// </summary>
public static class TriggerHook
{
    private const uint LLKHF_UP = 0x80;

    private static Thread? _thread;
    private static uint _threadId;
    private static nint _hook;
    private static volatile bool _swallowingTrigger;
    private static volatile bool _overlayOpen;
    private static volatile uint[] _triggerKeys = [VK_LMENU, VK_RMENU];

    /// <summary>Raised on the hook thread with a Stopwatch timestamp. Handlers must only post work.</summary>
    public static event Action<HookSignal, long>? Signal;

    /// <summary>While true the trigger always cycles, and arrows, Enter and Esc are forwarded.</summary>
    public static bool OverlayOpen { get => _overlayOpen; set => _overlayOpen = value; }

    public static void SetTrigger(params uint[] virtualKeys) => _triggerKeys = virtualKeys;

    public static void Start()
    {
        if (_thread != null) return;
        using var ready = new ManualResetEventSlim();
        int error = 0;
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            unsafe { _hook = SetWindowsHookExW(WH_KEYBOARD_LL, &HookProc, GetModuleHandleW(null), 0); }
            error = Marshal.GetLastPInvokeError();
            ready.Set();
            if (_hook == 0) return;
            while (GetMessageW(out _, 0, 0, 0) > 0) { }
            UnhookWindowsHookEx(_hook);
        })
        { IsBackground = true, Name = "Swivel.KeyboardHook", Priority = ThreadPriority.Highest };
        _thread.Start();
        ready.Wait();
        if (_hook == 0)
        {
            _thread = null;
            throw new Win32Exception(error, "Could not install the keyboard hook.");
        }
    }

    public static void Stop()
    {
        if (_thread == null) return;
        PostThreadMessageW(_threadId, WM_QUIT, 0, 0);
        _thread.Join(1000);
        _thread = null;
    }

    [UnmanagedCallersOnly]
    private static unsafe nint HookProc(int code, nint wParam, nint lParam)
    {
        try
        {
            if (code >= 0 && Handle(*(KBDLLHOOKSTRUCT*)lParam)) return 1;
        }
        catch
        {
            // Never let an exception unwind into user32.
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static bool Handle(in KBDLLHOOKSTRUCT key)
    {
        bool down = (key.flags & LLKHF_UP) == 0;
        uint vk = key.vkCode;

        if (Array.IndexOf(_triggerKeys, vk) >= 0)
        {
            if (!down)
            {
                if (!_swallowingTrigger) return false;
                _swallowingTrigger = false;
                return true;
            }
            if (_swallowingTrigger) return true; // auto-repeat of a tap already taken
            if (!_overlayOpen && !PrimaryButtonDown()) return false;
            _swallowingTrigger = true;
            Raise(HookSignal.TriggerTap);
            return true;
        }

        if (!_overlayOpen) return false;

        HookSignal? nav = vk switch
        {
            VK_LEFT => HookSignal.Left,
            VK_RIGHT => HookSignal.Right,
            VK_UP => HookSignal.Up,
            VK_DOWN => HookSignal.Down,
            VK_RETURN => HookSignal.Enter,
            VK_ESCAPE => HookSignal.Escape,
            _ => null,
        };
        if (nav is null) return false;
        if (down) Raise(nav.Value);
        // Esc passes through so an OLE drag source also cancels its drag.
        return nav != HookSignal.Escape;
    }

    private static void Raise(HookSignal signal) => Signal?.Invoke(signal, Stopwatch.GetTimestamp());
}
