using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace EchoDeck;

// ---------------------------------------------------------------------------------------------------------------------
// Native pieces for the taskbar (ITaskbarList3), global hotkeys and auto-hide taskbar handling.
// ---------------------------------------------------------------------------------------------------------------------

internal static class NativeShell
{
    public const int WM_GETMINMAXINFO = 0x0024;
    public const int WM_COMMAND = 0x0111;
    public const int WM_HOTKEY = 0x0312;
    public const int THBN_CLICKED = 0x1800;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_NOREPEAT = 0x4000;

    public const int MONITOR_DEFAULTTONEAREST = 2;
    public const uint ABM_GETSTATE = 0x00000004;
    public const uint ABM_GETTASKBARPOS = 0x00000005;
    public const uint ABM_GETAUTOHIDEBAREX = 0x0000000B;
    public const uint ABS_AUTOHIDE = 0x1;
    public const uint MONITORINFOF_PRIMARY = 0x1;
    public const int SM_CXSIZEFRAME = 32;
    public const int SM_CYSIZEFRAME = 33;
    public const int SM_CXPADDEDBORDER = 92;

    public const uint SHCNE_ASSOCCHANGED = 0x08000000;
    public const uint SHCNF_IDLIST = 0x0000;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

    [DllImport("shell32.dll")]
    public static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("shell32.dll")]
    public static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    [DllImport("comctl32.dll")]
    public static extern IntPtr ImageList_Create(int cx, int cy, uint flags, int cInitial, int cGrow);

    [DllImport("comctl32.dll")]
    public static extern int ImageList_ReplaceIcon(IntPtr himl, int i, IntPtr hicon);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ImageList_Destroy(IntPtr himl);

    [DllImport("user32.dll")]
    public static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint cPlanes, uint cBitsPerPel, byte[]? lpvBits);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// ITaskbarList3 (thumbnail toolbar + progress on the taskbar button)
// ---------------------------------------------------------------------------------------------------------------------

[ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
internal class TaskbarListCoClass { }

internal enum TaskbarProgressState
{
    NoProgress = 0,
    Indeterminate = 1,
    Normal = 2,
    Error = 4,
    Paused = 8
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct ThumbButton
{
    public uint dwMask;
    public uint iId;
    public uint iBitmap;
    public IntPtr hIcon;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szTip;
    public uint dwFlags;
}

[ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITaskbarList3
{
    // ITaskbarList
    [PreserveSig] int HrInit();
    [PreserveSig] int AddTab(IntPtr hwnd);
    [PreserveSig] int DeleteTab(IntPtr hwnd);
    [PreserveSig] int ActivateTab(IntPtr hwnd);
    [PreserveSig] int SetActiveAlt(IntPtr hwnd);

    // ITaskbarList2
    [PreserveSig] int MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);

    // ITaskbarList3
    [PreserveSig] int SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
    [PreserveSig] int SetProgressState(IntPtr hwnd, TaskbarProgressState tbpFlags);
    [PreserveSig] int RegisterTab(IntPtr hwndTab, IntPtr hwndMDI);
    [PreserveSig] int UnregisterTab(IntPtr hwndTab);
    [PreserveSig] int SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
    [PreserveSig] int SetTabActive(IntPtr hwndTab, IntPtr hwndMDI, uint dwReserved);
    [PreserveSig] int ThumbBarAddButtons(IntPtr hwnd, uint cButtons, [MarshalAs(UnmanagedType.LPArray)] ThumbButton[] pButtons);
    [PreserveSig] int ThumbBarUpdateButtons(IntPtr hwnd, uint cButtons, [MarshalAs(UnmanagedType.LPArray)] ThumbButton[] pButtons);
    [PreserveSig] int ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
    [PreserveSig] int SetOverlayIcon(IntPtr hwnd, IntPtr hIcon, [MarshalAs(UnmanagedType.LPWStr)] string? pszDescription);
    [PreserveSig] int SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string pszTip);
    [PreserveSig] int SetThumbnailClip(IntPtr hwnd, IntPtr prcClip);
}

[StructLayout(LayoutKind.Sequential)]
internal struct ClipRect
{
    public int Left, Top, Right, Bottom;
}

/// <summary>
/// Previous / Play-Pause / Next buttons in the taskbar preview and a progress bar on the taskbar icon.
/// Every call is wrapped: the taskbar is a nicety and must never take the player down.
/// </summary>
internal sealed class TaskbarController : IDisposable
{
    public const int IdPrevious = 100;
    public const int IdPlayPause = 101;
    public const int IdNext = 102;

    private const uint THB_BITMAP = 0x1, THB_TOOLTIP = 0x4, THB_FLAGS = 0x8;
    private const uint THBF_ENABLED = 0x0, THBF_DISABLED = 0x1, THBF_NOBACKGROUND = 0x4;
    private const int ImgPrev = 0, ImgPlay = 1, ImgPause = 2, ImgNext = 3;

    private readonly IntPtr _hwnd;
    private ITaskbarList3? _list;
    private IntPtr _imageList;
    private bool _buttonsAdded;
    private bool _playing;
    private bool _enabled;
    private TaskbarProgressState _state = TaskbarProgressState.NoProgress;
    private ulong _lastValue = ulong.MaxValue;

    public TaskbarController(IntPtr hwnd)
    {
        _hwnd = hwnd;
        try
        {
            _list = (ITaskbarList3)new TaskbarListCoClass();
            _list.HrInit();
        }
        catch
        {
            _list = null;
        }
    }

    public bool Available => _list != null;

    /// <summary>Limits the taskbar preview to a part of the window (client pixels); null shows the whole window again.</summary>
    public void SetClip(ClipRect? rect)
    {
        if (_list == null) return;
        IntPtr ptr = IntPtr.Zero;
        try
        {
            if (rect is { } r)
            {
                ptr = Marshal.AllocHGlobal(Marshal.SizeOf<ClipRect>());
                Marshal.StructureToPtr(r, ptr, false);
            }
            _list.SetThumbnailClip(_hwnd, ptr);
        }
        catch { /* taskbar busy */ }
        finally
        {
            if (ptr != IntPtr.Zero) Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>Called when Windows says the taskbar button exists (TaskbarButtonCreated); safe to call again after an Explorer restart.</summary>
    public void CreateButtons(double dpiScale)
    {
        if (_list == null) return;
        try
        {
            if (_imageList != IntPtr.Zero) { NativeShell.ImageList_Destroy(_imageList); _imageList = IntPtr.Zero; }

            int size = Math.Max(16, (int)Math.Round(16 * dpiScale));
            _imageList = NativeShell.ImageList_Create(size, size, 0x20 /* ILC_COLOR32 */, 4, 0);
            foreach (var kind in new[] { IconKind.Previous, IconKind.Play, IconKind.Pause, IconKind.Next })
            {
                var icon = MakeIcon(kind, size);
                if (icon != IntPtr.Zero)
                {
                    NativeShell.ImageList_ReplaceIcon(_imageList, -1, icon);
                    NativeShell.DestroyIcon(icon);
                }
            }
            _list.ThumbBarSetImageList(_hwnd, _imageList);

            var buttons = new[]
            {
                Button(IdPrevious, ImgPrev, "Previous", enabled: false),
                Button(IdPlayPause, ImgPlay, "Play", enabled: false),
                Button(IdNext, ImgNext, "Next", enabled: false)
            };
            int hr = _list.ThumbBarAddButtons(_hwnd, (uint)buttons.Length, buttons);
            _buttonsAdded = hr >= 0;
            _enabled = false;
            _playing = false;
        }
        catch
        {
            _buttonsAdded = false;
        }
    }

    private static ThumbButton Button(int id, int image, string tip, bool enabled) => new()
    {
        dwMask = THB_BITMAP | THB_TOOLTIP | THB_FLAGS,
        iId = (uint)id,
        iBitmap = (uint)image,
        szTip = tip,
        dwFlags = enabled ? THBF_ENABLED : THBF_DISABLED   // with the standard button background, like other players
    };

    /// <summary>Updates the three buttons: <paramref name="playing"/> chooses the Play or Pause picture.</summary>
    public void UpdateButtons(bool playing, bool hasMedia, bool canSkip)
    {
        if (_list == null || !_buttonsAdded) return;
        _playing = playing;
        try
        {
            var buttons = new[]
            {
                Button(IdPrevious, ImgPrev, "Previous", canSkip),
                Button(IdPlayPause, playing ? ImgPause : ImgPlay, playing ? "Pause" : "Play", hasMedia),
                Button(IdNext, ImgNext, "Next", canSkip)
            };
            _list.ThumbBarUpdateButtons(_hwnd, (uint)buttons.Length, buttons);
            _enabled = hasMedia;
        }
        catch { /* taskbar busy */ }
    }

    /// <summary>Progress along the taskbar icon. <paramref name="total"/> 0 clears it.</summary>
    public void SetProgress(long current, long total, bool paused)
    {
        if (_list == null) return;
        try
        {
            if (total <= 0)
            {
                Clear();
                return;
            }

            var state = paused ? TaskbarProgressState.Paused : TaskbarProgressState.Normal;
            if (state != _state)
            {
                _list.SetProgressState(_hwnd, state);
                _state = state;
            }

            // 1000 steps are plenty for a taskbar icon and avoid redundant updates
            ulong value = (ulong)Math.Clamp(current * 1000 / Math.Max(1, total), 0, 1000);
            if (value != _lastValue)
            {
                _list.SetProgressValue(_hwnd, value, 1000);
                _lastValue = value;
            }
        }
        catch { /* taskbar busy */ }
    }

    public void Clear()
    {
        if (_list == null) return;
        try
        {
            if (_state != TaskbarProgressState.NoProgress)
            {
                _list.SetProgressState(_hwnd, TaskbarProgressState.NoProgress);
                _state = TaskbarProgressState.NoProgress;
                _lastValue = ulong.MaxValue;
            }
        }
        catch { /* ignore */ }
    }

    public void Dispose()
    {
        try { Clear(); } catch { /* ignore */ }
        if (_imageList != IntPtr.Zero) { NativeShell.ImageList_Destroy(_imageList); _imageList = IntPtr.Zero; }
        if (_list != null)
        {
            try { Marshal.ReleaseComObject(_list); } catch { /* ignore */ }
            _list = null;
        }
    }

    // ---- button pictures, drawn with WPF so they match at any DPI

    private enum IconKind { Previous, Play, Pause, Next }

    private static IntPtr MakeIcon(IconKind kind, int size)
    {
        var visual = new DrawingVisual();
        double s = size / 16.0;
        using (var dc = visual.RenderOpen())
        {
            var fill = Brushes.White;
            var edge = new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), 0.8 * s);
            edge.Freeze();

            Geometry Tri(double x1, double y1, double x2, double y2, double x3, double y3)
            {
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(x1 * s, y1 * s), true, true);
                    c.LineTo(new Point(x2 * s, y2 * s), true, false);
                    c.LineTo(new Point(x3 * s, y3 * s), true, false);
                }
                g.Freeze();
                return g;
            }

            switch (kind)
            {
                case IconKind.Play:
                    dc.DrawGeometry(fill, edge, Tri(4.5, 2.5, 13, 8, 4.5, 13.5));
                    break;
                case IconKind.Pause:
                    dc.DrawRectangle(fill, edge, new Rect(3.5 * s, 2.5 * s, 3.2 * s, 11 * s));
                    dc.DrawRectangle(fill, edge, new Rect(9.3 * s, 2.5 * s, 3.2 * s, 11 * s));
                    break;
                case IconKind.Previous:
                    dc.DrawRectangle(fill, edge, new Rect(2.8 * s, 2.5 * s, 2.2 * s, 11 * s));
                    dc.DrawGeometry(fill, edge, Tri(13.5, 2.5, 5.5, 8, 13.5, 13.5));
                    break;
                case IconKind.Next:
                    dc.DrawGeometry(fill, edge, Tri(2.5, 2.5, 10.5, 8, 2.5, 13.5));
                    dc.DrawRectangle(fill, edge, new Rect(11 * s, 2.5 * s, 2.2 * s, 11 * s));
                    break;
            }
        }

        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);

        int stride = size * 4;
        var pixels = new byte[stride * size];
        bmp.CopyPixels(pixels, stride, 0);

        // premultiplied -> straight alpha, which is what an icon bitmap expects
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte a = pixels[i + 3];
            if (a == 0 || a == 255) continue;
            pixels[i] = (byte)Math.Min(255, pixels[i] * 255 / a);
            pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * 255 / a);
            pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * 255 / a);
        }

        IntPtr color = NativeShell.CreateBitmap(size, size, 1, 32, pixels);
        IntPtr mask = NativeShell.CreateBitmap(size, size, 1, 1, null);
        if (color == IntPtr.Zero || mask == IntPtr.Zero)
        {
            if (color != IntPtr.Zero) NativeShell.DeleteObject(color);
            if (mask != IntPtr.Zero) NativeShell.DeleteObject(mask);
            return IntPtr.Zero;
        }

        var info = new NativeShell.ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
        IntPtr icon = NativeShell.CreateIconIndirect(ref info);
        NativeShell.DeleteObject(color);
        NativeShell.DeleteObject(mask);
        return icon;
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Windows file associations (per user, no admin rights needed)
// ---------------------------------------------------------------------------------------------------------------------

internal static class FileAssociations
{
    private static readonly string[] VideoExt = { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".m4v", ".flv", ".mpg", ".mpeg", ".ts", ".m2ts", ".3gp" };
    private static readonly string[] AudioExt = { ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".opus", ".wma" };

    private const string VideoProgId = "Tempo.Video";
    private const string AudioProgId = "Tempo.Audio";
    private const string BackupKey = @"Software\Tempo\Associations";

    private static string ExePath => Environment.ProcessPath ?? "";

    /// <summary>True when this build of Tempo is registered as the handler for the common formats.</summary>
    public static bool IsRegistered()
    {
        try
        {
            using var cmd = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + VideoProgId + @"\shell\open\command");
            var value = cmd?.GetValue(null) as string;
            if (string.IsNullOrEmpty(value) || ExePath.Length == 0) return false;
            if (value.IndexOf(ExePath, StringComparison.OrdinalIgnoreCase) < 0) return false;

            using var ext = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.mp4");
            return string.Equals(ext?.GetValue(null) as string, VideoProgId, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>What happened when registering / unregistering. A blocked registry (policy, antivirus) is reported, never thrown.</summary>
    public readonly record struct Outcome(bool Succeeded, int Failures, string? FirstProblem);

    /// <summary>
    /// Runs one registry step. A failure (access denied by a domain policy or security software, a locked key ...) is logged
    /// and counted; the remaining steps still run and nothing propagates to the caller.
    /// </summary>
    private static bool Step(string what, Action action, ref int failures, ref string? first)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            failures++;
            first ??= ex.Message;
            AppLog.Write("File associations", ex, what);
            return false;
        }
    }

    public static Outcome Register()
    {
        int failures = 0;
        string? first = null;

        var exe = ExePath;
        if (exe.Length == 0)
        {
            AppLog.Write("File associations", null, "Tempo can't find its own program file");
            return new Outcome(false, 1, "Tempo can't find its own program file.");
        }

        Step("video ProgID", () => WriteProgId(VideoProgId, "Tempo video", exe), ref failures, ref first);
        Step("audio ProgID", () => WriteProgId(AudioProgId, "Tempo audio", exe), ref failures, ref first);

        Step("application key", () =>
        {
            using var app = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\Tempo.exe");
            app?.SetValue("FriendlyAppName", "Tempo");
            using var types = app?.CreateSubKey("SupportedTypes");
            foreach (var ext in VideoExt.Concat(AudioExt)) types?.SetValue(ext, "");
            using var cmd = app?.CreateSubKey(@"shell\open\command");
            cmd?.SetValue(null, "\"" + exe + "\" \"%1\"");
        }, ref failures, ref first);

        // Capabilities: makes Tempo appear in Windows' "Default apps" list
        Step("capabilities", () =>
        {
            using (var cap = Registry.CurrentUser.CreateSubKey(@"Software\Tempo\Capabilities"))
            {
                cap?.SetValue("ApplicationName", "Tempo");
                cap?.SetValue("ApplicationDescription", "Video and music player");
                using var assoc = cap?.CreateSubKey("FileAssociations");
                foreach (var ext in VideoExt) assoc?.SetValue(ext, VideoProgId);
                foreach (var ext in AudioExt) assoc?.SetValue(ext, AudioProgId);
            }
            using var reg = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications");
            reg?.SetValue("Tempo", @"Software\Tempo\Capabilities");
        }, ref failures, ref first);

        RegistryKey? backup = null;
        try { backup = Registry.CurrentUser.CreateSubKey(BackupKey); }
        catch (Exception ex) { failures++; first ??= ex.Message; AppLog.Write("File associations", ex, "backup key"); }

        try
        {
            foreach (var (ext, progId) in VideoExt.Select(e => (e, VideoProgId)).Concat(AudioExt.Select(e => (e, AudioProgId))))
            {
                // one extension failing (a protected key) must not stop the others
                Step(ext, () =>
                {
                    using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ext);
                    if (key == null) return;

                    // remember what the extension pointed to, so turning this off puts it back
                    var previous = key.GetValue(null) as string ?? "";
                    if (!string.Equals(previous, progId, StringComparison.OrdinalIgnoreCase) && backup?.GetValue(ext) == null)
                        backup?.SetValue(ext, previous);

                    using (var with = key.CreateSubKey("OpenWithProgids"))
                        with?.SetValue(progId, new byte[0], RegistryValueKind.None);
                    key.SetValue(null, progId);
                }, ref failures, ref first);
            }
        }
        finally
        {
            backup?.Dispose();
        }

        NotifyShell();
        return new Outcome(failures == 0, failures, first);
    }

    public static Outcome Unregister()
    {
        int failures = 0;
        string? first = null;

        RegistryKey? backup = null;
        try { backup = Registry.CurrentUser.OpenSubKey(BackupKey); } catch (Exception ex) { AppLog.Write("File associations", ex, "backup key"); }

        try
        {
            foreach (var (ext, progId) in VideoExt.Select(e => (e, VideoProgId)).Concat(AudioExt.Select(e => (e, AudioProgId))))
            {
                Step(ext, () =>
                {
                    using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + ext, writable: true);
                    if (key == null) return;

                    using (var with = key.OpenSubKey("OpenWithProgids", writable: true))
                        with?.DeleteValue(progId, throwOnMissingValue: false);

                    if (string.Equals(key.GetValue(null) as string, progId, StringComparison.OrdinalIgnoreCase))
                    {
                        var previous = backup?.GetValue(ext) as string ?? "";
                        if (previous.Length > 0) key.SetValue(null, previous);
                        else key.DeleteValue("", throwOnMissingValue: false);
                    }
                }, ref failures, ref first);
            }
        }
        finally
        {
            backup?.Dispose();
        }

        foreach (var sub in new[]
        {
            @"Software\Classes\" + VideoProgId, @"Software\Classes\" + AudioProgId,
            @"Software\Classes\Applications\Tempo.exe", @"Software\Tempo\Capabilities", BackupKey
        })
        {
            var subKey = sub;
            Step(subKey, () => Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false), ref failures, ref first);
        }
        Step("registered applications", () =>
        {
            using var reg = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true);
            reg?.DeleteValue("Tempo", throwOnMissingValue: false);
        }, ref failures, ref first);

        NotifyShell();
        return new Outcome(failures == 0, failures, first);
    }

    private static void NotifyShell()
    {
        try { NativeShell.SHChangeNotify(NativeShell.SHCNE_ASSOCCHANGED, NativeShell.SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero); }
        catch (Exception ex) { AppLog.Write("File associations", ex, "shell notification"); }
    }

    private static void WriteProgId(string progId, string description, string exe)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + progId);
        key?.SetValue(null, description);
        using (var icon = key?.CreateSubKey("DefaultIcon")) icon?.SetValue(null, "\"" + exe + "\",0");
        using (var cmd = key?.CreateSubKey(@"shell\open\command")) cmd?.SetValue(null, "\"" + exe + "\" \"%1\"");
    }
}
