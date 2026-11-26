using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace EchoDeck;

public partial class MainWindow
{
    // ------------------------------------------------------------------ playback errors

    private int _errorSkips;

    /// <summary>
    /// LibVLC could not play the item: a damaged file, a codec it doesn't have, or (in Anonymous Mode) an encrypted file that
    /// fails its integrity check. Say so, and keep a queue moving instead of leaving a silent, frozen player.
    /// </summary>
    private void OnPlaybackError()
    {
        if (_player is null || _transition) return;
        // If the incoming side of an overlap fails, retire the outgoing side as well; otherwise it can
        // continue audibly while the UI believes playback has stopped or moved to another queue item.
        CancelCrossfade();

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
    private int _ambientThemeKey;   // the theme + accent the glass was last built for

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
    /// they stay readable on any cover. The light theme gets the same dark glass (its surfaces are swapped for dark ones inside
    /// these elements only, see PutAmbientResources), so the colours fill the window there too.
    /// </summary>
    private void SyncAmbience()
    {
        bool music = _currentIsAudio && !_libraryVisible && MusicPanel.Visibility == Visibility.Visible;
        bool on = music;

        WindowAurora.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Aurora.Visibility = music && !on ? Visibility.Visible : Visibility.Collapsed;

        // the music view itself (its buttons, text and hover colours) always sits on the cover's colours
        SetAmbientSkin(MusicLayer, music);

        // the video area is black behind a video; behind a song it must let the window's colours through, or the music view
        // showed a flat black rectangle in the middle of the coloured window
        VideoHost.Background = on ? Brushes.Transparent : Brushes.Black;
        UpdateTaskbarClip();

        // the theme or accent changed while the colours are showing: the glass has to be put together again for it
        int themeKey = (ThemeManager.IsDark ? 1 : 0) + AppSettings.Current.AccentIndex * 2;
        if (on == _ambience)
        {
            if (on && themeKey != _ambientThemeKey)
            {
                _ambientThemeKey = themeKey;
                try { UpdateAmbientColours(animate: false); } catch (Exception ex) { AppLog.Write("Ambient colours", ex); }
            }
            return;
        }
        _ambience = on;
        _ambientThemeKey = themeKey;

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
        VideoView.Visibility = _currentIsAudio || _nativeHeld || _miniVideoHeld ? Visibility.Hidden : Visibility.Visible;

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
