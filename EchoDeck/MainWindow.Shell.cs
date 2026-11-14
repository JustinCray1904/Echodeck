using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace EchoDeck;

// Windows integration for the main window: global media keys, taskbar buttons + progress, auto-hide taskbar fix,
// and the "now playing" state that the queue bars, visualiser and taskbar share.
public partial class MainWindow
{
    private const int HkPlayPause = 1, HkNext = 2, HkPrev = 3, HkStop = 4;
    private const int HkVolUp = 5, HkVolDown = 6, HkMute = 7;
    private const int HkAltPlay = 10, HkAltNext = 11, HkAltPrev = 12, HkAltVolUp = 13, HkAltVolDown = 14;

    private IntPtr _hwnd;
    private TaskbarController? _taskbar;
    private uint _msgTaskbarButtonCreated;
    private readonly List<int> _hotkeys = new();
    private string _hotkeySig = "";
    private Thickness _maxMargin = SystemParameters.WindowResizeBorderThickness;
    private bool _playingNow;   // something is really playing (not paused / stopped)

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)?.AddHook(ShellWndProc);

        _msgTaskbarButtonCreated = NativeShell.RegisterWindowMessage("TaskbarButtonCreated");
        _taskbar = new TaskbarController(_hwnd);
        ApplyGlobalKeys();
    }

    private IntPtr ShellWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeShell.WM_HOTKEY)
        {
            OnHotKey(wParam.ToInt32());
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == NativeShell.WM_COMMAND && ((wParam.ToInt64() >> 16) & 0xFFFF) == NativeShell.THBN_CLICKED)
        {
            OnThumbButton((int)(wParam.ToInt64() & 0xFFFF));
            handled = true;
            return IntPtr.Zero;
        }

        if (_msgTaskbarButtonCreated != 0 && (uint)msg == _msgTaskbarButtonCreated)
        {
            InitTaskbar();
            return IntPtr.Zero;
        }

        if (msg == NativeShell.WM_GETMINMAXINFO && ApplyAutoHideMaximize(hwnd, lParam))
        {
            handled = true;
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    // ------------------------------------------------------------------ auto-hide taskbar

    /// <summary>
    /// A maximised window covers the whole monitor when the taskbar is set to auto-hide, so the mouse can never reach the
    /// taskbar's edge. Leave a thin strip on that edge. Returns false (and changes nothing) in every other situation.
    /// </summary>
    private bool ApplyAutoHideMaximize(IntPtr hwnd, IntPtr lParam)
    {
        try
        {
            if (_isFullscreen && WindowStyle == WindowStyle.None) return false;   // real fullscreen covers everything on purpose

            var monitor = NativeShell.MonitorFromWindow(hwnd, NativeShell.MONITOR_DEFAULTTONEAREST);
            var info = new NativeShell.MONITORINFO { cbSize = Marshal.SizeOf<NativeShell.MONITORINFO>() };
            if (monitor == IntPtr.Zero || !NativeShell.GetMonitorInfo(monitor, ref info)) return false;

            var edges = DetectAutoHideEdges(info);
            bool left = edges[0], top = edges[1], right = edges[2], bottom = edges[3];

            string seen = $"L{(left ? 1 : 0)} T{(top ? 1 : 0)} R{(right ? 1 : 0)} B{(bottom ? 1 : 0)}";
            if (seen != _lastTaskbarLog)
            {
                _lastTaskbarLog = seen;
                AppLog.Write("Taskbar", null, "auto-hide edges on this monitor: " + seen);
            }

            if (!left && !top && !right && !bottom)
            {
                _maxMargin = SystemParameters.WindowResizeBorderThickness;
                return false;
            }

            uint dpi = NativeShell.GetDpiForWindow(hwnd);
            if (dpi == 0) dpi = 96;
            int frameX = NativeShell.GetSystemMetricsForDpi(NativeShell.SM_CXSIZEFRAME, dpi) + NativeShell.GetSystemMetricsForDpi(NativeShell.SM_CXPADDEDBORDER, dpi);
            int frameY = NativeShell.GetSystemMetricsForDpi(NativeShell.SM_CYSIZEFRAME, dpi) + NativeShell.GetSystemMetricsForDpi(NativeShell.SM_CXPADDEDBORDER, dpi);
            const int strip = 2;

            var mon = info.rcMonitor;
            var work = info.rcWork;
            // normal maximised windows overhang the work area by the sizing frame; on an auto-hide edge stop short instead
            int l = left ? mon.left + strip : work.left - frameX;
            int t = top ? mon.top + strip : work.top - frameY;
            int r = right ? mon.right - strip : work.right + frameX;
            int b = bottom ? mon.bottom - strip : work.bottom + frameY;

            var mmi = Marshal.PtrToStructure<NativeShell.MINMAXINFO>(lParam);
            mmi.ptMaxPosition.x = l - mon.left;
            mmi.ptMaxPosition.y = t - mon.top;
            mmi.ptMaxSize.x = r - l;
            mmi.ptMaxSize.y = b - t;
            mmi.ptMinTrackSize.x = (int)Math.Round(MinWidth * dpi / 96.0);
            mmi.ptMinTrackSize.y = (int)Math.Round(MinHeight * dpi / 96.0);
            mmi.ptMaxTrackSize.x = Math.Max(mmi.ptMaxTrackSize.x, mmi.ptMaxSize.x);
            mmi.ptMaxTrackSize.y = Math.Max(mmi.ptMaxTrackSize.y, mmi.ptMaxSize.y);
            Marshal.StructureToPtr(mmi, lParam, false);

            double scale = dpi / 96.0;
            _maxMargin = new Thickness(left ? 0 : frameX / scale, top ? 0 : frameY / scale, right ? 0 : frameX / scale, bottom ? 0 : frameY / scale);
            return true;
        }
        catch
        {
            return false;   // fall back to the normal behaviour
        }
    }

    private string _lastTaskbarLog = "";

    /// <summary>
    /// Which edges of this monitor carry an auto-hiding taskbar. Asks Windows per edge first (works for any monitor), then
    /// falls back to the classic "is the taskbar auto-hide, and where is it" questions for the primary monitor.
    /// </summary>
    private static bool[] DetectAutoHideEdges(NativeShell.MONITORINFO info)
    {
        var edges = new bool[4];
        for (uint e = 0; e < 4; e++) edges[e] = HasAutoHideBar(e, info.rcMonitor);
        if (edges.Any(x => x)) return edges;

        if ((info.dwFlags & NativeShell.MONITORINFOF_PRIMARY) == 0) return edges;
        try
        {
            var state = new NativeShell.APPBARDATA { cbSize = (uint)Marshal.SizeOf<NativeShell.APPBARDATA>() };
            uint flags = (uint)NativeShell.SHAppBarMessage(NativeShell.ABM_GETSTATE, ref state).ToUInt64();
            if ((flags & NativeShell.ABS_AUTOHIDE) == 0) return edges;

            var pos = new NativeShell.APPBARDATA { cbSize = (uint)Marshal.SizeOf<NativeShell.APPBARDATA>() };
            bool found = NativeShell.SHAppBarMessage(NativeShell.ABM_GETTASKBARPOS, ref pos) != UIntPtr.Zero;
            edges[found ? Math.Clamp((int)pos.uEdge, 0, 3) : 3] = true;   // 0 left, 1 top, 2 right, 3 bottom (the default)
        }
        catch (Exception ex)
        {
            AppLog.Write("Taskbar", ex, "fallback detection");
        }
        return edges;
    }

    private static bool HasAutoHideBar(uint edge, NativeShell.RECT monitorRect)
    {
        var data = new NativeShell.APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeShell.APPBARDATA>(),
            uEdge = edge,
            rc = monitorRect
        };
        return NativeShell.SHAppBarMessage(NativeShell.ABM_GETAUTOHIDEBAREX, ref data) != UIntPtr.Zero;
    }

    // ------------------------------------------------------------------ global keys

    /// <summary>(Re)registers the system-wide hotkeys from the settings. Keys another app already owns are simply skipped.</summary>
    private void ApplyGlobalKeys()
    {
        if (_hwnd == IntPtr.Zero) return;

        string sig = _cfg.GlobalMediaKeys + "|" + _cfg.GlobalVolumeKeys;
        if (sig == _hotkeySig) return;
        _hotkeySig = sig;

        foreach (var id in _hotkeys) NativeShell.UnregisterHotKey(_hwnd, id);
        _hotkeys.Clear();
        if (!_cfg.GlobalMediaKeys) return;

        void Reg(int id, uint mods, uint vk)
        {
            if (NativeShell.RegisterHotKey(_hwnd, id, mods, vk)) _hotkeys.Add(id);
        }

        const uint norepeat = NativeShell.MOD_NOREPEAT;
        const uint ctrlAlt = NativeShell.MOD_CONTROL | NativeShell.MOD_ALT;

        // the keyboard's media keys
        Reg(HkPlayPause, norepeat, 0xB3);   // VK_MEDIA_PLAY_PAUSE
        Reg(HkNext, norepeat, 0xB0);        // VK_MEDIA_NEXT_TRACK
        Reg(HkPrev, norepeat, 0xB1);        // VK_MEDIA_PREV_TRACK
        Reg(HkStop, norepeat, 0xB2);        // VK_MEDIA_STOP

        // for keyboards without media keys: Ctrl + Alt + Space / Left / Right / Up / Down
        Reg(HkAltPlay, ctrlAlt | norepeat, 0x20);
        Reg(HkAltNext, ctrlAlt | norepeat, 0x27);
        Reg(HkAltPrev, ctrlAlt | norepeat, 0x25);
        Reg(HkAltVolUp, ctrlAlt, 0x26);
        Reg(HkAltVolDown, ctrlAlt, 0x28);

        if (_cfg.GlobalVolumeKeys)
        {
            Reg(HkVolUp, 0, 0xAF);          // VK_VOLUME_UP
            Reg(HkVolDown, 0, 0xAE);        // VK_VOLUME_DOWN
            Reg(HkMute, norepeat, 0xAD);    // VK_VOLUME_MUTE
        }
    }

    private void OnHotKey(int id)
    {
        if (_transition || _player is null) return;

        switch (id)
        {
            case HkPlayPause:
            case HkAltPlay: TogglePlayPause(); break;
            case HkNext:
            case HkAltNext: PlayAdjacent(+1); break;
            case HkPrev:
            case HkAltPrev: PlayAdjacent(-1); break;
            case HkStop: if (_hasMedia) MiniClose_Click(this, new RoutedEventArgs()); break;
            case HkVolUp:
            case HkAltVolUp: ChangeVolume(+5); break;
            case HkVolDown:
            case HkAltVolDown: ChangeVolume(-5); break;
            case HkMute: ToggleMute(); break;
        }
    }

    // ------------------------------------------------------------------ taskbar

    private void InitTaskbar()
    {
        if (_taskbar == null || !_cfg.TaskbarControls) return;
        _taskbar.CreateButtons(VisualTreeHelper.GetDpi(this).DpiScaleX);
        UpdateTaskbarButtons();
    }

    private void OnThumbButton(int id)
    {
        if (_transition) return;
        switch (id)
        {
            case TaskbarController.IdPrevious: PlayAdjacent(-1); break;
            case TaskbarController.IdPlayPause: TogglePlayPause(); break;
            case TaskbarController.IdNext: PlayAdjacent(+1); break;
        }
    }

    private void UpdateTaskbarButtons()
    {
        _taskbar?.UpdateButtons(_playingNow, _hasMedia, _queue.Count > 1);
        if (!_playingNow && !_hasMedia) _taskbar?.Clear();
    }

    private void UpdateTaskbarProgress(long timeMs)
    {
        if (_taskbar == null) return;
        // nothing on the taskbar icon in Anonymous Mode: it would show that something is playing
        if (!_cfg.TaskbarControls || _anon || !_hasMedia || _lengthMs <= 0) { _taskbar.Clear(); return; }
        _taskbar.SetProgress(timeMs, _lengthMs, paused: !_playingNow);
    }

    // ------------------------------------------------------------------ now-playing state shared by the UI

    /// <summary>Called whenever playback starts or stops/pauses: queue bars, visualiser and taskbar follow it.</summary>
    private void SyncPlayingState(bool playing)
    {
        _playingNow = playing;

        foreach (var entry in _sideItems) entry.IsAnimating = playing && entry.IsPlaying;

        Visualizer.IsPlaying = playing && _currentIsAudio;
        UpdateTaskbarButtons();
        if (!playing && !_hasMedia) _taskbar?.Clear();
    }

    /// <summary>Applies the chosen music visualiser: 0 ripples (the original), 1 radial bars, 2 wave ring.</summary>
    private void ApplyVisualizerStyle()
    {
        int style = Math.Clamp(_cfg.VisualizerStyle, 0, 2);
        Visualizer.Mode = style;
        Visualizer.Intensity = Math.Clamp(_cfg.VisualizerIntensity, 0.4, 2.0);
        Visualizer.Sensitivity = Math.Clamp(_cfg.VisualizerSensitivity, 0.4, 2.5);
        Visualizer.Visibility = style == 0 ? Visibility.Collapsed : Visibility.Visible;
        SetPulse(_playingNow && _currentIsAudio);   // the ripples only belong to style 0
    }

    // ------------------------------------------------------------------ playback errors

    private int _errorSkips;

    /// <summary>
    /// LibVLC could not play the item: a damaged file, a codec it doesn't have, or (in Anonymous Mode) an encrypted file that
    /// fails its integrity check. Say so, and keep a queue moving instead of leaving a silent, frozen player.
    /// </summary>
    private void OnPlaybackError()
    {
        if (_player is null || _transition) return;

        string name = "this item";
        if (_queueIndex >= 0 && _queueIndex < _queue.Count)
        {
            var path = _queue[_queueIndex].FullPath;
            name = _anon ? "this encrypted item" : "“" + Path.GetFileName(path) + "”";
        }

        ResetTransport();
        _hasMedia = false;
        UpdateTaskbarButtons();
        UpdateMiniPlayer();
        UpdateTransportEnabled();

        string reason = _anon
            ? "The encrypted file may be damaged, or was made with a different key."
            : "The file may be damaged, or it uses a format Tempo can't decode.";

        bool canSkip = _cfg.AutoAdvance && _queueIndex >= 0 && _queueIndex + 1 < _queue.Count && _errorSkips < 5;
        if (canSkip)
        {
            _errorSkips++;
            Notify("Can't play " + name + ". " + reason + " Skipping to the next one.");
            int next = _queueIndex + 1;
            var nextPath = _queue[next].FullPath;
            Dispatcher.InvokeAsync(() => PlayNormal(nextPath, stayInLibrary: _libraryVisible), DispatcherPriority.Background);
        }
        else
        {
            _errorSkips = 0;
            Notify("Can't play " + name + ". " + reason);
        }
    }

    // ------------------------------------------------------------------ video curtain

    /// <summary>
    /// Covers the video (a native window) while a side panel changes the layout next to it. The picture is hidden for those
    /// few moments instead of flashing white, squeezing or showing a black strip.
    /// </summary>
    private bool _ambience;

    private static readonly Brush MusicVeil = FrozenBrush(Color.FromArgb(0x1C, 0x00, 0x00, 0x00));

    private static Brush FrozenBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>
    /// While a song's music view is showing, its colours fill the whole window (dark themes): the side bar, the queue and the
    /// bottom bar become dark glass in the cover's hue over them instead of flat panels that looked like part of another app,
    /// and their text, hover, selection, highlight and icon colours are taken from the cover too (MainWindow.Ambience.cs), so
    /// they stay readable on any cover. Light themes keep the colours behind the music view only (white text on a light window
    /// would not be readable).
    /// </summary>
    private void SyncAmbience()
    {
        bool music = _currentIsAudio && !_libraryVisible && MusicPanel.Visibility == Visibility.Visible;
        bool on = music && ThemeManager.IsDark;

        WindowAurora.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Aurora.Visibility = music && !on ? Visibility.Visible : Visibility.Collapsed;

        // the music view itself (its buttons, text and hover colours) always sits on the cover's colours
        SetAmbientSkin(MusicLayer, music);

        // the video area is black behind a video; behind a song it must let the window's colours through, or the music view
        // showed a flat black rectangle in the middle of the coloured window
        VideoHost.Background = on ? Brushes.Transparent : Brushes.Black;
        UpdateTaskbarClip();

        if (on == _ambience) return;
        _ambience = on;

        SetAmbientSkin(Rail, on);
        SetAmbientSkin(SidebarPanel, on);
        SetAmbientSkin(ControlBar, on, neutral: true);   // the bottom bar keeps the app's accent colour
        SetAmbientSkin(TopBar, on, neutral: true);

        if (on)
        {
            ApplyTints(animate: false);
            TopBar.Background = Brushes.Transparent;
            MusicLayer.Background = MusicVeil;
        }
        else
        {
            Rail.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "PanelBrush");
            SidebarPanel.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "PanelBrush");
            if (!_isFullscreen) ControlBar.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "BgBrush");
            TopBar.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "BgBrush");
            MusicLayer.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "PanelBrush");
        }
    }

    private DispatcherTimer? _curtainWatchdog;

    /// <summary>
    /// The video area slides smoothly only while nothing is loaded. With media loaded, the native video window and the overlay
    /// window on top of it would be resized on every animation frame (heavy, and they never move in the same frame), so the
    /// layout changes once while the panel itself still fades and slides.
    /// </summary>
    private bool SmoothPanels => AppSettings.Current.SmoothResize && !_hasMedia;

    private bool _nativeHeld;   // the native video window is hidden for the moment the layout changes

    /// <summary>
    /// The native video window (a separate Win32 window, drawn above everything in the main window) is only shown while a video
    /// or nothing is playing. A song never needs it, so the music view can't be disturbed by it: no flat grey / black window
    /// peeking out where the sidebar is about to appear.
    /// </summary>
    private void SyncNativeSurface()
    {
        VideoView.Visibility = _currentIsAudio || _nativeHeld ? Visibility.Hidden : Visibility.Visible;

        // a song is shown by the main window itself (MusicLayer): the overlay window above it must let the mouse through
        // (a fully transparent layered window passes clicks on) and must not draw its corner mask
        VideoSurface.Background = _currentIsAudio ? Brushes.Transparent : VideoSurfaceHit;
        VideoCorners.Visibility = _currentIsAudio ? Visibility.Collapsed : Visibility.Visible;
    }

    private static readonly Brush VideoSurfaceHit = MakeHitBrush();

    private static Brush MakeHitBrush()
    {
        var b = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));   // almost invisible, but not click-through
        b.Freeze();
        return b;
    }

    /// <summary>
    /// Layout change with a video on screen. The video window and the overlay window on top of it are resized in different frames:
    /// for a moment the old video window covered the place of the new panel (a flat light-grey block) and, afterwards, a black
    /// strip stayed behind. So the video window is hidden while the layout changes (the overlay curtain covers the picture),
    /// and shown again at its final size, under the curtain, before the picture fades back in.
    /// </summary>
    private void CurtainOn()
    {
        if (_libraryVisible || VideoSurface.Visibility != Visibility.Visible) return;

        Visualizer.Frozen = true;   // nothing to redraw while the layout is changing

        // safety net: whatever happens to the transition, everything comes back
        _curtainWatchdog ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _curtainWatchdog.Tick -= CurtainWatchdog_Tick;
        _curtainWatchdog.Tick += CurtainWatchdog_Tick;
        _curtainWatchdog.Stop();
        _curtainWatchdog.Start();

        if (_currentIsAudio) return;   // a song has no video window underneath: nothing to hide

        VideoCurtain.Background = Brushes.Black;
        UiAnim.Cancel(VideoCurtain);
        VideoCurtain.Opacity = 1;
        VideoCurtain.Visibility = Visibility.Visible;
        _nativeHeld = true;
        SyncNativeSurface();
    }

    private void CurtainWatchdog_Tick(object? sender, EventArgs e)
    {
        _curtainWatchdog?.Stop();
        CurtainOff();
    }

    private void CurtainOff()
    {
        _curtainWatchdog?.Stop();
        QueueMiniReposition();
        Visualizer.Frozen = false;

        if (_nativeHeld)
        {
            // the video window comes back at its final size, while the curtain still covers the picture
            _nativeHeld = false;
            SyncNativeSurface();
            PlayerView.InvalidateMeasure();
            PlayerView.UpdateLayout();
        }

        if (VideoCurtain.Visibility != Visibility.Visible) return;
        VideoSurface.InvalidateVisual();
        UiAnim.FadeOut(VideoCurtain, 110);
    }

    private void ReleaseShell()
    {
        foreach (var id in _hotkeys) NativeShell.UnregisterHotKey(_hwnd, id);
        _hotkeys.Clear();
        _taskbar?.Dispose();
        _taskbar = null;
    }
}
