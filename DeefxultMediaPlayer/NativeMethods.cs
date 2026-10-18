using System.Runtime.InteropServices;
#if GLOBAL_KEY_HOOK
using System.Text;
#endif

namespace DeefxultMediaPlayer;

#if GLOBAL_KEY_HOOK
internal delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
#endif

internal static class NativeMethods
{
    // Virtual-key codes used by MainWindow
    public const int VK_CONTROL = 0x11;
    public const int VK_LCONTROL = 0xA2;
    public const int VK_RCONTROL = 0xA3;
    public const int VK_ESCAPE = 0x1B;
    public const int VK_SPACE = 0x20;
    public const int VK_F11 = 0x7A;
    public const int VK_O = 0x4F;

    // ---- window styles (overlay must never steal focus from the player)
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>Clicking the window won't activate it (focus stays with the player) and it's hidden from Alt-Tab.</summary>
    public static void MakeNonActivating(IntPtr hwnd)
    {
        int style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    // ---- dark title bar
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Dark title bar (Win10 20H1+) and dark caption/border colours (Win11). Failures are ignored.</summary>
    public static void ApplyDarkTitleBar(IntPtr hwnd)
    {
        try
        {
            int on = 1;
            DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));        // DWMWA_USE_IMMERSIVE_DARK_MODE
            int caption = 0x000E0B0B;                                    // COLORREF 0x00BBGGRR => #0B0B0E
            DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));   // DWMWA_CAPTION_COLOR
            int border = 0x00362D2A;                                     // #2A2D36
            DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));    // DWMWA_BORDER_COLOR
        }
        catch { /* older Windows: ignore */ }
    }

#if GLOBAL_KEY_HOOK
    // ---- only compiled with -p:EnableGlobalKeyHook=true
    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);
#endif
}
