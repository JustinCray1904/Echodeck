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
        OsdBox.Visibility = Visibility.Visible;

        if (_osdTimer == null)
        {
            _osdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1300) };
            _osdTimer.Tick += (_, _) =>
            {
                _osdTimer!.Stop();
                OsdBox.Visibility = Visibility.Collapsed;
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
        bool mini = _libraryVisible && _hasMedia && !_currentIsAudio && _vault == null;

        if (mini)
        {
            PlayerView.Visibility = Visibility.Visible;
            PlayerView.HorizontalAlignment = HorizontalAlignment.Right;
            PlayerView.VerticalAlignment = VerticalAlignment.Bottom;
            PlayerView.Width = 296;
            PlayerView.Height = 166;
            PlayerView.Margin = new Thickness(0, 0, 18, 74);
            MiniVideoSpacer.Visibility = Visibility.Visible;
            SidebarPanel.Visibility = Visibility.Collapsed;
            QuickPopup.IsOpen = false;
            VideoSurface.Visibility = Visibility.Collapsed;
            return;
        }

        MiniVideoSpacer.Visibility = Visibility.Collapsed;
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

    private void AttachFloatingBar()
    {
        if (ReferenceEquals(FloatingBar.Child, ControlBar)) return;

        RootGrid.Children.Remove(ControlBar);
        ControlBar.Background = System.Windows.Media.Brushes.Transparent;
        ControlBar.Visibility = Visibility.Visible;
        FloatingBar.Child = ControlBar;
        FloatingBar.Visibility = Visibility.Visible;
        VideoCorners.Radius = 0;
        RevealFloatingBar();
    }

    private void DetachFloatingBar()
    {
        _barTimer?.Stop();
        if (!ReferenceEquals(FloatingBar.Child, ControlBar)) return;

        FloatingBar.Child = null;
        FloatingBar.Visibility = Visibility.Collapsed;
        VideoSurface.Cursor = Cursors.Arrow;
        ControlBar.SetResourceReference(Border.BackgroundProperty, "BgBrush");
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
        QuickPopup.IsOpen = false;
        OpenEqualizer();
    }

    private void OpenEqualizer()
    {
        if (_modalOpen) return;
        _modalOpen = true;
        try { new EqualizerWindow(ApplyEq) { Owner = this }.ShowDialog(); }
        finally { _modalOpen = false; }
    }

    private void ApplyEq(bool enabled, float[] gains)
    {
        if (_player is null) return;
        try
        {
            var next = new Equalizer();
            next.SetPreamp(0f);
            for (uint band = 0; band < 10; band++) next.SetAmp(enabled ? gains[band] : 0f, band);
            _player.SetEqualizer(next);
            _equalizer?.Dispose();
            _equalizer = next;
        }
        catch { /* not available for this media */ }
    }
}
