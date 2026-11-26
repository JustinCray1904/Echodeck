using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace EchoDeck;

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
