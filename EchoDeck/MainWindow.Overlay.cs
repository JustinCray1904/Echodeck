using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LibVLCSharp.Shared;

namespace EchoDeck;

/// <summary>On-screen indicator, floating fullscreen control bar, mini player layout, speed menu and equaliser.</summary>
public partial class MainWindow
{
    private DispatcherTimer? _osdTimer;
    private DispatcherTimer? _barTimer;
    private Equalizer? _equalizer;

    // ------------------------------------------------------------------ on-screen indicator (volume, seek, speed ...)

    private void ShowOsd(string text, double? fraction = null)
    {
        if (_libraryVisible) return;

        OsdText.Text = text;
        OsdBarHost.Visibility = fraction.HasValue ? Visibility.Visible : Visibility.Collapsed;
        if (fraction.HasValue) OsdBarFill.Width = 140 * Math.Clamp(fraction.Value, 0, 1);
        UiAnim.FadeIn(OsdBox, 110);

        if (_osdTimer == null)
        {
            _osdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1300) };
            _osdTimer.Tick += (_, _) =>
            {
                _osdTimer!.Stop();
                UiAnim.FadeOut(OsdBox, 240);
            };
        }
        _osdTimer.Stop();
        _osdTimer.Start();
    }

    // ------------------------------------------------------------------ mini player (video preview above the controls)

    /// <summary>
    /// Full player, or - while browsing the library with a video running - a small live preview in the corner with the
    /// mini controls right under it.
    /// </summary>
    private void ApplyPlayerLayout()
    {
        bool mini = _libraryVisible && _hasMedia && !_currentIsAudio;
        SyncResumeCard();   // never inside the small player
        SyncAmbience();

        // a song shows just a pause button (and tools on hover); a video's controls sit on the video itself
        MiniAudioPause.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
        MiniAudioTools.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;

        // a small video fills the card edge to edge on a black card (no grey frame or corners around the picture);
        // a song's card keeps the normal panel look
        if (mini)
        {
            MiniPlayer.Background = System.Windows.Media.Brushes.Black;
            MiniVideoSpacer.Margin = new Thickness(-5, 1, -5, 6);
            VideoCorners.Radius = 0;
        }
        else
        {
            MiniPlayer.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
            MiniVideoSpacer.Margin = new Thickness(0, 1, 0, 6);
            VideoCorners.Radius = _isFullscreen ? 0 : 14;
        }

        if (mini)
        {
            PlayerView.Visibility = Visibility.Visible;
            MiniVideoSpacer.Visibility = Visibility.Visible;
            ApplySidebar(animate: false);
            ClosePopup(QuickPopup, animate: false);
            // the overlay window sits on the small video and carries only the previous / next buttons
            MiniOverlay.Visibility = Visibility.Visible;
            if (!MiniOverlay.IsMouseOver) { UiAnim.Cancel(MiniControls); MiniControls.Opacity = 0; }
            VideoSurface.Visibility = Visibility.Visible;
            PositionMiniVideo();
            return;
        }

        MiniVideoSpacer.Visibility = Visibility.Collapsed;
        MiniOverlay.Visibility = Visibility.Collapsed;
        PlayerView.HorizontalAlignment = HorizontalAlignment.Stretch;
        PlayerView.VerticalAlignment = VerticalAlignment.Stretch;
        PlayerView.Width = double.NaN;
        PlayerView.Height = double.NaN;
        PlayerView.Margin = new Thickness(0);
        PlayerView.Visibility = _libraryVisible ? Visibility.Collapsed : Visibility.Visible;
        // The overlay is a separate window that doesn't hide with its parent: hide its content explicitly.
        VideoSurface.Visibility = _libraryVisible ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void MiniClose_Click(object sender, RoutedEventArgs e)
    {
        var player = _player;
        if (player == null) return;

        StopResumeTracking();

        _waveCts?.Cancel();
        await Task.Run(() => player.Stop());
        _hasMedia = false;
        _lengthMs = 0;
        ResetTransport();
        ShowMusic(false, "", "");
        EmptyHint.Visibility = Visibility.Visible;
        SetTitles(AppName);
        UpdateTransportEnabled();
    }

    // ------------------------------------------------------------------ floating bar in fullscreen

    private const double FloatingBarHeight = 50;

    private void AttachFloatingBar()
    {
        if (ReferenceEquals(FloatingBar.Child, ControlBar)) return;

        RootGrid.Children.Remove(ControlBar);
        ControlBar.Background = System.Windows.Media.Brushes.Transparent;
        ControlBar.Visibility = Visibility.Visible;
        FloatingBar.Child = ControlBar;
        FloatingBar.Visibility = Visibility.Visible;
        VideoCorners.Radius = 0;

        // a compact bar: shorter and tighter, at full size inside (scaling the contents made the text and seek bar soft)
        ControlBar.Height = FloatingBarHeight;
        ControlBar.Padding = new Thickness(12, 0, 12, 0);
        RevealFloatingBar();
    }

    private void DetachFloatingBar()
    {
        _barTimer?.Stop();
        if (!ReferenceEquals(FloatingBar.Child, ControlBar)) return;

        FloatingBar.Child = null;
        FloatingBar.Visibility = Visibility.Collapsed;

        ControlBar.Height = 68;
        ControlBar.Padding = new Thickness(18, 0, 18, 10);
        VideoSurface.Cursor = Cursors.Arrow;
        MusicLayer.Cursor = Cursors.Arrow;
        RestoreBarBackground();
        RootGrid.Children.Add(ControlBar);
        Grid.SetRow(ControlBar, 2);
        ControlBar.Visibility = _libraryVisible ? Visibility.Collapsed : Visibility.Visible;
        VideoCorners.Radius = 14;
    }

    /// <summary>Mouse moved: show the bar at once, then fade it out again after a short pause.</summary>
    private void RevealFloatingBar()
    {
        if (!_isFullscreen || !ReferenceEquals(FloatingBar.Child, ControlBar)) return;

        FloatingBar.BeginAnimation(OpacityProperty, null);   // drop any fade that is still running
        FloatingBar.Opacity = 1;
        FloatingBar.IsHitTestVisible = true;
        VideoSurface.Cursor = Cursors.Arrow;
        MusicLayer.Cursor = Cursors.Arrow;

        if (_barTimer == null)
        {
            _barTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
            _barTimer.Tick += (_, _) => HideFloatingBar();
        }
        _barTimer.Stop();
        _barTimer.Start();
    }

    private void HideFloatingBar()
    {
        _barTimer?.Stop();
        if (!_isFullscreen) return;

        // never hide while the pointer is on the bar or a popup (volume, quick settings) is open
        if (FloatingBar.IsMouseOver || VolumePopup.IsOpen || QuickPopup.IsOpen) { _barTimer?.Start(); return; }

        FloatingBar.IsHitTestVisible = false;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) =>
        {
            FloatingBar.BeginAnimation(OpacityProperty, null);
            FloatingBar.Opacity = 0;
        };
        FloatingBar.BeginAnimation(OpacityProperty, fade);
        VideoSurface.Cursor = Cursors.None;
        MusicLayer.Cursor = Cursors.None;
    }

    private void VideoSurface_MouseMove(object sender, MouseEventArgs e) => RevealFloatingBar();

    // ------------------------------------------------------------------ speed menu + equaliser

    private void ShowSpeedMenu()
    {
        if (_player is null) return;
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        for (int i = 0; i < SpeedValues.Length; i++)
        {
            double speed = SpeedValues[i];
            var item = new MenuItem { Header = SpeedNames[i], IsChecked = Math.Abs(_cfg.DefaultSpeed - speed) < 0.001 };
            item.Click += (_, _) =>
            {
                _cfg.DefaultSpeed = speed;
                _player?.SetRate((float)speed);
                CommitPlayerSetting();
                ShowOsd("Speed " + speed.ToString("0.##") + "x");
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void QuickEq_Click(object sender, RoutedEventArgs e)
    {
        ClosePopup(QuickPopup, animate: false);
        OpenEqualizer();
    }

    private void OpenEqualizer()
    {
        if (_modalOpen) return;
        _modalOpen = true;
        try { new EqualizerWindow(ApplyEq) { Owner = this }.ShowDialog(); }
        finally { _modalOpen = false; }
    }

    /// <summary>
    /// "Off" removes the equalizer from the audio path. A flat equalizer (all bands 0 dB) is not the same: LibVLC's filter stays
    /// in the signal and makes everything quieter than it was before the equalizer was ever switched on, even with the volume at
    /// 100 %. VLC's own flat preset therefore carries a 12 dB preamp; the same is used here, reduced by the biggest boost so the
    /// peaks stay where they were.
    /// </summary>
    private void ApplyEq(bool enabled, float[] gains)
    {
        if (_player is null) return;
        try
        {
            if (!enabled)
            {
                if (TryUnsetEqualizer())
                {
                    _equalizer?.Dispose();
                    _equalizer = null;
                    return;
                }
            }

            float boost = 0f;
            if (enabled)
                for (int band = 0; band < 10; band++) boost = Math.Max(boost, gains[band]);

            var next = new Equalizer();
            next.SetPreamp(Math.Clamp(12f - boost, 0f, 12f));
            for (uint band = 0; band < 10; band++) next.SetAmp(enabled ? gains[band] : 0f, band);
            _player.SetEqualizer(next);
            _equalizer?.Dispose();
            _equalizer = next;
        }
        catch { /* not available for this media */ }
    }

    /// <summary>libvlc_media_player_set_equalizer(NULL): looked up by name so a different LibVLCSharp build can't break the build.</summary>
    private bool TryUnsetEqualizer()
    {
        if (_player is null) return false;
        try
        {
            var method = typeof(MediaPlayer).GetMethod("UnsetEqualizer", Type.EmptyTypes);
            if (method == null) return false;
            method.Invoke(_player, null);
            return true;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ crop (C), like VLC

    private static readonly string[] CropValues = { "", "16:10", "16:9", "4:3", "185:100", "221:100", "235:100", "239:100", "5:3", "5:4", "1:1" };
    private static readonly string[] CropNames = { "Default", "16:10", "16:9", "4:3", "1.85:1", "2.21:1", "2.35:1", "2.39:1", "5:3", "5:4", "1:1" };
    private int _cropIndex;

    /// <summary>C: crops the picture to the next ratio (Default, 16:10, 16:9, 4:3 ...), shown on screen. Videos only.</summary>
    private void CycleCrop()
    {
        if (_player is null || !_hasMedia || _currentIsAudio) return;

        int next = (_cropIndex + 1) % CropValues.Length;
        if (!TrySetCrop(CropValues[next]))
        {
            ShowOsd("Crop isn't available in this player build");
            return;
        }
        _cropIndex = next;
        ShowOsd("Crop: " + CropNames[_cropIndex]);
    }

    /// <summary>
    /// libvlc_video_set_crop_geometry through MediaPlayer.CropGeometry, looked up by name so a LibVLCSharp build without it
    /// can't break the build (the same approach as the equaliser reset).
    /// </summary>
    private bool TrySetCrop(string geometry)
    {
        if (_player is null) return false;
        try
        {
            var property = typeof(MediaPlayer).GetProperty("CropGeometry");
            if (property == null || !property.CanWrite) return false;
            property.SetValue(_player, geometry);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write("Crop", ex);
            return false;
        }
    }
}
