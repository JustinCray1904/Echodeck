#if GLOBAL_KEY_HOOK
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace EchoDeck;

/// <summary>
/// OPTIONAL low-level keyboard hook, only compiled with -p:EnableGlobalKeyHook=true.
/// Use it only if Ctrl+passcode is ignored after clicking on the video (focus inside LibVLC's native
/// window). Expect antivirus tools to flag a binary that imports SetWindowsHookEx unless it's signed.
///
/// Windows silently removes a low-level hook whose callback is slow (LowLevelHooksTimeout), so the callback does
/// the bare minimum: read the key code, queue ONE tiny work item to the UI dispatcher, and return. Everything
/// else (foreground-window checks, buffer matching, passcode hashing) happens later on the dispatcher.
/// Keys are always passed on to the system unchanged.
/// </summary>
internal sealed class GlobalKeyboardHook : IKeyInputSource
{
    private readonly LowLevelKeyboardProc _proc;   // keep a reference so the GC doesn't collect it
    private readonly Dispatcher _dispatcher;
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private IntPtr _hookId = IntPtr.Zero;

    public event Action<int, bool, bool>? KeyPressed;
    public event Action? CtrlReleased;

    public GlobalKeyboardHook(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _proc = HookCallback;
    }

    public bool Start()
    {
        if (_hookId != IntPtr.Zero) return true;
        _hookId = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
        return _hookId != IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                int vk = Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode is the first field

                if (msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN)
                {
                    bool ctrl = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;
                    _dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                        KeyPressed?.Invoke(vk, ctrl, IsOwnAppForeground())));
                }
                else if ((msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP) &&
                         (vk == NativeMethods.VK_CONTROL || vk == NativeMethods.VK_LCONTROL || vk == NativeMethods.VK_RCONTROL))
                {
                    _dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => CtrlReleased?.Invoke()));
                }
            }
        }
        catch
        {
            // Never let an exception escape a hook callback.
        }

        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private bool IsOwnAppForeground()
    {
        var fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;

        NativeMethods.GetWindowThreadProcessId(fg, out uint pid);
        if (pid != _ownPid) return false;

        // Ignore common dialogs (file pickers, message boxes).
        var cls = new StringBuilder(64);
        NativeMethods.GetClassName(fg, cls, cls.Capacity);
        return cls.ToString() != "#32770";
    }

    public void Dispose()
    {
        if (_hookId != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }
}
#endif
