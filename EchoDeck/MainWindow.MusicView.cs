using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;

namespace EchoDeck;

public partial class MainWindow
{
    // ------------------------------------------------------------------ music view + waveform

    private void ShowMusic(bool on, string title, string subtitle)
    {
        _currentIsAudio = on;
        SyncNativeSurface();   // a song never needs the native video window
        MusicPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        SyncAmbience();
        EmptyHint.Visibility = Visibility.Collapsed;
        MusicTitle.Text = title;
        MusicSubtitle.Text = subtitle;

        // default look (accent colour) until the cover's own colour is known
        ApplyDefaultCoverLook();
        SetPulse(false); // the ripples only run while something is actually playing
        Visualizer.Reset();   // new song / stopped: no waveform, no motion
        OnMusicViewChanged(on);
        if (!on) ClearLyrics();

        // Music gets the waveform seek bar, video the plain one (same position, same dragging).
        SeekBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        WaveBar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on) WaveBar.Peaks = null;
    }

    private async void LoadWaveform(string path, EncryptedVaultStream? vaultStream)
    {
        _waveCts?.Cancel();
        var cts = _waveCts = new CancellationTokenSource();
        WaveBar.Peaks = null;
        Visualizer.SetPeaks(null);
        Visualizer.SetSpectrum(null);

        try
        {
            // the bar fills in while the song is read: the parts that are ready are shown at once
            Action<float[]> partial = peaks =>
                Dispatcher.InvokeAsync(() => { if (!cts.IsCancellationRequested) WaveBar.Peaks = peaks; }, DispatcherPriority.Background);

            var analysis = vaultStream != null
                ? await WaveformLoader.FromStreamAsync(vaultStream, cts.Token, partial)
                : await WaveformLoader.FromFileAsync(path, cts.Token, partial);
            if (!cts.IsCancellationRequested)
            {
                WaveBar.Peaks = analysis?.Peaks;
                Visualizer.SetPeaks(analysis?.Peaks);
                Visualizer.SetSpectrum(analysis?.Spectrum);
            }
        }
        catch (OperationCanceledException) { /* a newer track started */ }
        catch { /* keep the flat placeholder bars */ }
    }

    /// <summary>
    /// A song with no cover (yet): the plain disc, the accent colour for the glow, ripples and visualiser, and the moving background
    /// in the accent's colours. Used when a song starts and when its cover is thrown away, so nothing of the old cover's colours stays.
    /// </summary>
    private void ApplyDefaultCoverLook()
    {
        _coverShown = false;
        CoverCircle.Fill = (System.Windows.Media.Brush)FindResource("CoverFallback");
        MusicGlyph.Visibility = Visibility.Visible;
        PulseRing.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AccentBrush");
        GlowStop.Color = (System.Windows.Media.Color)FindResource("AccentColor");
        Visualizer.Tint = null;
        var around = CoverPalette.Around((System.Windows.Media.Color)FindResource("AccentColor"));   // until the cover's colours are known
        Aurora.SetPalette(around, null);
        WindowAurora.SetPalette(around, null);
        SetAmbientPalette(around);
    }

    /// <summary>Album art / video frame as the big cover, using the same thumbnail Windows Explorer shows.</summary>
    /// <param name="userAsked">The user asked for a (new) cover: say so when none was found or the internet is out.</param>
    private async void LoadCover(string path, bool userAsked = false)
    {
        if (!MediaTypes.IsAudio(path)) return;
        System.Windows.Media.Imaging.BitmapSource? bmp = await Task.Run(() =>
        {
            if (OnlineCovers.SkipsEmbedded(path)) return null;   // the user said the picture in this file is wrong
            var raw = ShellThumbnail.Get(path, 600, 600, biggerOk: true);
            return raw == null ? null : ShellThumbnail.TrimBorders(raw);
        });

        // no cover in the file: if the setting is on, look for one online (never in Anonymous Mode)
        if (bmp == null && !_anon && _cfg.FetchCoversOnline) bmp = await OnlineCovers.GetAsync(path);
        bool stillCurrent = string.Equals(_queueIndex >= 0 && _queueIndex < _queue.Count ? _queue[_queueIndex].FullPath : "", path,
            StringComparison.OrdinalIgnoreCase);
        if (bmp == null && userAsked && !_anon && stillCurrent)
        {
            // nothing came back: was that because there is no such cover, or because there is no internet?
            if (!await OnlineCovers.IsOnlineAsync()) ShowNoInternetPrompt();
            else ShowOsd("No cover found for this song");
            return;
        }
        if (bmp == null || _anon) return;
        if (!stillCurrent) return;
        CoverCircle.Fill = new System.Windows.Media.ImageBrush(bmp) { Stretch = System.Windows.Media.Stretch.UniformToFill };
        MusicGlyph.Visibility = Visibility.Collapsed;

        // the whole music view takes the cover's colours: a blurred copy of it, with its colours drifting over it
        _coverShown = true;
        var palette = CoverPalette.From(bmp);
        Aurora.SetPalette(palette, bmp);
        WindowAurora.SetPalette(palette, bmp);
        SetAmbientPalette(palette);

        // the ripples and glow take the colour of the cover
        if (ShellThumbnail.DominantColor(bmp) is { } color)
        {
            PulseRing.Stroke = new System.Windows.Media.SolidColorBrush(color);
            Visualizer.Tint = new System.Windows.Media.SolidColorBrush(color);
            GlowStop.Color = color;
        }
    }

    /// <summary>
    /// Right click on the cover (or the ring around it): offers "wrong cover" / "look again" instead of the general player menu. The cover
    /// sits inside containers that are not hit-testable, so the position is worked out here instead of with a mouse handler on it.
    /// </summary>
    private bool TryShowCoverMenu(MouseButtonEventArgs e)
    {
        if (_anon || _libraryVisible || !_currentIsAudio || MusicPanel.Visibility != Visibility.Visible) return false;
        if (_queueIndex < 0 || _queueIndex >= _queue.Count) return false;

        double radius = CoverCircle.ActualWidth / 2;
        if (radius <= 0) return false;
        var p = e.GetPosition(CoverCircle);   // in the cover's own coordinates, whatever the scaling
        double distance = Math.Sqrt((p.X - radius) * (p.X - radius) + (p.Y - radius) * (p.Y - radius));
        if (distance > radius * 1.3) return false;

        string path = _queue[_queueIndex].FullPath;
        bool hasCover = MusicGlyph.Visibility != Visibility.Visible;
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        if (hasCover) menu.Items.Add(MenuEntry("This cover is wrong - find another", "IcoCamera", (_, _) => ResetCover(path)));
        else menu.Items.Add(MenuEntry("Look for a cover again", "IcoCamera", (_, _) => RetryCover(path)));
        menu.IsOpen = true;
        return true;
    }

    private async void RetryCover(string path)
    {
        if (!_cfg.FetchCoversOnline)
        {
            Notify("Turn on \"Find missing covers online\" in Settings to look for a cover.");
            return;
        }

        ShowOsd("Looking for a cover…");
        if (!await OnlineCovers.IsOnlineAsync())
        {
            ShowNoInternetPrompt();
            return;
        }
        OnlineCovers.Retry(path);
        LoadCover(path, userAsked: true);
    }

    private async void ResetCover(string path)
    {
        if (!_cfg.FetchCoversOnline)
        {
            Notify("Turn on \"Find missing covers online\" in Settings to look for another cover.");
            return;
        }

        // Offline: say so and leave everything as it is (the current cover is not thrown away for a search that cannot happen).
        ShowOsd("Looking for another cover…");
        if (!await OnlineCovers.IsOnlineAsync())
        {
            ShowNoInternetPrompt();
            return;
        }
        OnlineCovers.Reject(path);

        // back to the plain disc, and to the default colours, while the next best cover is looked for
        ApplyDefaultCoverLook();
        LoadCover(path, userAsked: true);
    }

    /// <summary>Tempo's own message box for "the internet is out" (the cover look-up needs it).</summary>
    private void ShowNoInternetPrompt()
    {
        TempoDialog.Show(this,
            "There is no internet connection, so Tempo can't look for a cover right now.\n\nCheck your connection and try again.",
            AppName, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SetPulse(bool on)
    {
        // the ripples are one of three music visualisers (Settings > Interface)
        if (on && _cfg.VisualizerStyle == 0)
        {
            var span = TimeSpan.FromSeconds(1.9);
            var scale = new DoubleAnimation(1.0, 1.3, span)
            {
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
            };
            var scaleY = scale.Clone();
            var fade = new DoubleAnimation(0.55, 0.0, span) { RepeatBehavior = RepeatBehavior.Forever };
            PulseScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, scale);
            PulseScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, scaleY);
            PulseRing.BeginAnimation(OpacityProperty, fade);
        }
        else
        {
            PulseScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
            PulseScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
            PulseRing.BeginAnimation(OpacityProperty, null);
            PulseRing.Opacity = 0;
        }
    }

    // ------------------------------------------------------------------ shuffle / repeat

    private void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        _cfg.Shuffle = (sender as ToggleButton)?.IsChecked == true;
        if (!_anon) _cfg.Save(notify: false);
        UpdateShuffleUi();
    }

    private void UpdateShuffleUi()
    {
        ShuffleToggle.IsChecked = _cfg.Shuffle;
        PlShuffleToggle.IsChecked = _cfg.Shuffle;
        SideShuffleToggle.IsChecked = _cfg.Shuffle;
    }

    private void Loop_Click(object sender, RoutedEventArgs e)
    {
        _cfg.Loop = _cfg.Loop switch { LoopMode.Off => LoopMode.All, LoopMode.All => LoopMode.One, _ => LoopMode.Off };
        if (!_anon) _cfg.Save(notify: false);
        UpdateLoopUi();
    }

    private void UpdateLoopUi()
    {
        var tip = _cfg.Loop switch
        {
            LoopMode.One => "Repeat: this item",
            LoopMode.All => "Repeat: whole queue",
            _ => "Repeat: off"
        };

        void Apply(System.Windows.Shapes.Path icon, TextBlock badge, Button button)
        {
            badge.Visibility = _cfg.Loop == LoopMode.One ? Visibility.Visible : Visibility.Collapsed;
            if (_cfg.Loop == LoopMode.Off)
            {
                icon.ClearValue(System.Windows.Shapes.Shape.StrokeProperty);
                icon.ClearValue(System.Windows.Shapes.Shape.FillProperty);
            }
            else
            {
                icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AccentBrush");
                icon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentBrush");
            }
            button.ToolTip = tip;
        }

        Apply(LoopIcon, LoopBadge, LoopButton);
        Apply(PlLoopIcon, PlLoopBadge, PlLoopButton);
        Apply(SideLoopIcon, SideLoopBadge, SideLoopButton);
    }
}
