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

public partial class MainWindow : Window
{
    private const string AppName = "EchoDeck";
    private static readonly string[] AspectNames = { "Default", "16:9", "4:3", "1:1", "21:9", "16:10" };
    private static readonly double[] SpeedValues = { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 };
    private static readonly string[] SpeedNames = { "0.5x", "0.75x", "1x (normal)", "1.25x", "1.5x", "2x" };

    private readonly AppSettings _cfg = AppSettings.Current;

    // Playback
    private LibVLC? _libVlc;
    private MediaPlayer? _player;
    private bool _hasMedia;
    private bool _firstPlay;
    private bool _currentIsAudio;
    private long _lengthMs;
    private long? _pendingSeekMs;
    private string _normalTitle = AppName;
    private readonly Random _rng = new();

    // Anonymous Mode (vault) state
    private readonly IKeyInputSource _keys;
    private readonly PinDetector _pinDetector = new(PinStore.Verify, () => PinStore.Length);
    private readonly VaultService _vaultService = new();
    private AnonymousPanelWindow? _vault;
    private AnonymousSettingsWindow? _anonSettings;
    private PlaybackSnapshot? _snapshot;
    private bool _transition;   // the going-dark / lights-on animation is running
    private bool _modalOpen;    // a dialog is open: hotkeys and the passcode gesture are ignored

    // The vault item currently feeding LibVLC (decrypted on the fly, never written to disk)
    private EncryptedVaultStream? _vaultStream;
    private StreamMediaInput? _vaultInput;
    private Media? _vaultMedia;
    private string? _vaultPath;

    // Playback queue (in memory only) and library navigation state
    private readonly List<QueueItem> _queue = new();
    private readonly Stack<string> _navBack = new();
    private readonly Stack<string> _navForward = new();
    private string? _currentDir;
    private int _typeFilter;     // 0 all (Home), 1 videos, 2 music
    private bool _sortDesc;
    private int _queueIndex = -1;
    private bool _libraryVisible = true;
    private bool _sidebarVisible;
    private bool _loadingQuick;
    private readonly List<int> _quickAudioIds = new();
    private readonly List<int> _quickSubIds = new();
    private CancellationTokenSource? _waveCts;
    private System.Windows.Shell.WindowChrome? _chrome;

    // Fullscreen
    private bool _isFullscreen;
    private WindowState _prevState;
    private WindowStyle _prevStyle;
    private ResizeMode _prevResize;

    public MainWindow()
    {
        InitializeComponent();

        _chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);

#if GLOBAL_KEY_HOOK
        _keys = new GlobalKeyboardHook(Dispatcher);
#else
        _keys = new WpfKeyInput(Dispatcher);
#endif

        foreach (var bar in new[] { WaveBar, SeekBar })
        {
            bar.Scrubbing += frac =>
            {
                if (_lengthMs > 0) TimeText.Text = Fmt((long)(frac * _lengthMs));
            };
            bar.SeekCommitted += frac =>
            {
                if (_player != null && _hasMedia && _lengthMs > 0) _player.Time = (long)(frac * _lengthMs);
            };
        }

        RootGrid.MouseRightButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && IsInside(d, TopBar)) return;
            ShowContextMenu();
        };

        LocationChanged += (_, _) => SchedulePositionVault();
        SizeChanged += (_, _) => SchedulePositionVault();
        StateChanged += (_, _) =>
        {
            OnWindowStateChanged();
            SchedulePositionVault();
            if (WindowState == WindowState.Minimized && _vault != null && _cfg.AnonymousExitOnMinimize)
                ExitAnonymous(animate: false);
        };
    }

    private static bool IsInside(DependencyObject? node, DependencyObject ancestor)
    {
        while (node != null)
        {
            if (ReferenceEquals(node, ancestor)) return true;
            node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    // ------------------------------------------------------------------ startup / shutdown

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _keys.KeyPressed += OnKeyInput;
        _keys.CtrlReleased += _pinDetector.Reset;
        if (!_keys.Start())
        {
            MessageBox.Show(this, "Keyboard input could not be initialised; Ctrl+passcode and Esc won't work.",
                AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // The Anonymous pill must never float above other applications: hide it while the app is in the background.
        Application.Current.Activated += App_Activated;
        Application.Current.Deactivated += App_Deactivated;
        AppSettings.Changed += OnSettingsChanged;

        VolumeSlider.Value = Math.Clamp(_cfg.Volume, 0, 100);
        _sidebarVisible = _cfg.ShowSidebarAtStart;
        ApplyViewMode();
        UpdateLoopUi();
        UpdateShuffleUi();
        UpdateTransportEnabled();

        // The window (with the EchoDeck logo) is already on screen here. Starting LibVLC scans its plugins, which is the
        // slow part of start-up, so it runs off the UI thread instead of freezing the window.
        await CreatePlayerAsync();

        // The video surface was created while visible; only now is the library shown on top of it.
        ShowLibrary(true);
        StartupFlow();
    }

    private async Task CreatePlayerAsync()
    {
        var args = new List<string>
        {
            "--no-video-title-show",
            _cfg.HardwareDecoding ? "--avcodec-hw=any" : "--avcodec-hw=none",
            "--freetype-rel-fontsize=" + new[] { 18, 16, 12, 6 }[Math.Clamp(_cfg.SubtitleSize, 0, 3)],
            "--network-caching=" + Math.Clamp(_cfg.NetworkCachingMs, 100, 10000)
        };

        var argArray = args.ToArray();
        _libVlc = await Task.Run(() => new LibVLC(argArray));
        _player = new MediaPlayer(_libVlc)
        {
            // Let WPF see keyboard/mouse instead of libvlc's native window.
            EnableKeyInput = false,
            EnableMouseInput = false
        };

        _player.Playing += Player_Playing;
        _player.Paused += (_, _) => Dispatcher.InvokeAsync(() => { SetPlayGlyph(false); NativeMethods.KeepAwake(false); SetPulse(false); });
        _player.Stopped += (_, _) => Dispatcher.InvokeAsync(ResetTransport);
        _player.EndReached += (_, _) => Dispatcher.InvokeAsync(OnEndReached);
        _player.LengthChanged += Player_LengthChanged;
        _player.TimeChanged += Player_TimeChanged;

        VideoView.MediaPlayer = _player;
    }

    private void StartupFlow()
    {
        if (!_cfg.OnboardingDone) ShowOnboarding();

        PlaylistStore.EnsureLoaded();
        SetMode(_cfg.StartPage switch { "Home" => LibMode.Home, "Blank" => LibMode.Blank, _ => LibMode.Recent });

        // Command line / "Open with": EchoDeck.exe "C:\path\video.mp4"
        var file = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(MediaTypes.IsSupportedFile);
        if (file != null) PlayNormal(file);
    }

    private string RootFolder()
    {
        if (!string.IsNullOrWhiteSpace(_cfg.MediaFolder) && Directory.Exists(_cfg.MediaFolder)) return _cfg.MediaFolder;
        var fallback = Environment.GetFolderPath(_typeFilter == 2 ? Environment.SpecialFolder.MyMusic : Environment.SpecialFolder.MyVideos);
        return fallback;
    }

    private void ShowOnboarding()
    {
        _modalOpen = true;
        try { new OnboardingWindow { Owner = this }.ShowDialog(); }
        finally { _modalOpen = false; }

        ThemeManager.Apply(_cfg.Theme, _cfg.AccentIndex);
        VolumeSlider.Value = Math.Clamp(_cfg.Volume, 0, 100);
    }

    protected override void OnClosed(EventArgs e)
    {
        Application.Current.Activated -= App_Activated;
        Application.Current.Deactivated -= App_Deactivated;
        AppSettings.Changed -= OnSettingsChanged;
        NativeMethods.KeepAwake(false);
        _waveCts?.Cancel();
        _equalizer?.Dispose();

        _keys.Dispose();
        _anonSettings?.Close();
        _vault?.Close();
        _vaultStream?.Dispose();
        _vaultService.Lock();

        var player = _player;
        var vlc = _libVlc;
        _player = null;
        _libVlc = null;

        base.OnClosed(e);

        // Stop/dispose off the UI thread to avoid LibVLC deadlocks on shutdown.
        Task.Run(() =>
        {
            try { player?.Stop(); player?.Dispose(); vlc?.Dispose(); } catch { /* shutting down */ }
        });
    }

    private void App_Deactivated(object? sender, EventArgs e) => _vault?.Hide();

    private void App_Activated(object? sender, EventArgs e)
    {
        if (_vault == null || _transition) return;
        _vault.Show();
        PositionVault();
    }

    private void OnSettingsChanged()
    {
        if (_vault == null) ThemeManager.Apply(_cfg.Theme, _cfg.AccentIndex);
        ApplyVideoAdjust();
        UpdateLoopUi();
        WaveBar.InvalidateVisual();
        SeekBar.InvalidateVisual();
        VideoCorners.InvalidateVisual();
        ApplyViewMode();
        if (IsLoaded && _libraryVisible) RebuildRows();
    }

    // ------------------------------------------------------------------ views (library <-> player)

    private void ShowLibrary(bool on)
    {
        _libraryVisible = on;
        LibraryView.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!_isFullscreen) ControlBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;

        // Expanded rail (icons + labels) in the library, icon-only next to a playing video.
        Rail.Width = on ? 190 : 56;
        Rail.Padding = new Thickness(on ? 8 : 4);
        NavRecent.Content = on ? "Recent" : null;
        NavHome.Content = on ? "Home" : null;
        NavVideos.Content = on ? "Videos" : null;
        NavMusic.Content = on ? "Music" : null;
        NavPlaylists.Content = on ? "Playlists" : null;
        NavAdd.Content = on ? "Add item" : null;

        if (on) { QuickPanel.Visibility = Visibility.Collapsed; HighlightNav(); }
        else SetNavChecked(null);

        SidebarPanel.Visibility = !on && _sidebarVisible && !_isFullscreen && _vault == null
            ? Visibility.Visible : Visibility.Collapsed;

        UpdateMiniPlayer();
        SchedulePositionVault();
    }

    private void NavAdd_Click(object sender, RoutedEventArgs e)
    {
        NavAdd.IsChecked = false;
        var menu = new ContextMenu { PlacementTarget = NavAdd, Placement = PlacementMode.Right };
        menu.Items.Add(MenuEntry("Open file(s)…", "IcoVideo", Open_Click, "Ctrl+O"));
        menu.Items.Add(MenuEntry("Open folder…", "IcoFolder", OpenFolder_Click));
        menu.IsOpen = true;
    }

    // ------------------------------------------------------------------ playback

    private void PlayNormal(string path)
    {
        if (_vault != null || _libVlc is null) return;

        var existing = _queue.FirstOrDefault(q => string.Equals(q.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            existing = new QueueItem(path);
            _queue.Add(existing);
        }
        _queueIndex = _queue.IndexOf(existing);
        if (_cfg.RememberRecent) RecentStore.Add(path);

        var name = Path.GetFileName(path);
        _normalTitle = name;
        SetTitles(name);

        bool audio = MediaTypes.IsAudio(path);
        ShowMusic(audio, Path.GetFileNameWithoutExtension(path), Path.GetFileName(Path.GetDirectoryName(path) ?? ""));
        ShowLibrary(false);
        StartPlayback(path, 0, isMrl: false);

        if (audio) LoadWaveform(path, null);
        else WaveBar.Peaks = null;
        LoadCover(path);
        RefreshSide();
        UpdateTransportEnabled();
    }

    private void SetTitles(string name)
    {
        TitleText.Text = name;
        Title = name == AppName ? AppName : name + " - " + AppName;
        MiniTitle.Text = name == AppName ? "" : name;
    }

    private void StartPlayback(string pathOrMrl, long startMs, bool isMrl)
    {
        if (_libVlc is null || _player is null) return;

        using var media = new Media(_libVlc, pathOrMrl, isMrl ? FromType.FromLocation : FromType.FromPath);
        if (startMs > 0)
        {
            media.AddOption(":start-time=" + (startMs / 1000.0).ToString("0.000", CultureInfo.InvariantCulture));
            _pendingSeekMs = startMs;
        }
        else
        {
            _pendingSeekMs = null;
        }

        _hasMedia = true;
        _firstPlay = true;
        _lengthMs = 0;
        SeekBar.Progress = 0;
        WaveBar.Progress = 0;
        UpdateTransportEnabled();
        _player.Play(media);
    }

    private void TogglePlayPause()
    {
        if (_player is null) return;

        if (!_hasMedia)
        {
            // Nothing loaded: start the first queued item (never in Anonymous Mode, which has its own list).
            if (_vault == null && _queue.Count > 0) PlayNormal(_queue[Math.Max(0, _queueIndex)].FullPath);
            return;
        }
        if (_player.IsPlaying) _player.Pause(); else _player.Play();
    }

    // Player events arrive on LibVLC threads: marshal to the UI thread, never call back into the player inline.
    private void Player_Playing(object? sender, EventArgs e) => Dispatcher.InvokeAsync(() =>
    {
        if (_player is null) return;
        SetPlayGlyph(true);
        SetPulse(_currentIsAudio);
        EmptyHint.Visibility = Visibility.Collapsed;
        _player.Volume = (int)VolumeSlider.Value;
        _lengthMs = _player.Length;
        if (_lengthMs > 0) DurationText.Text = Fmt(_lengthMs);

        NativeMethods.KeepAwake(_cfg.KeepAwake && !_currentIsAudio);

        if (_pendingSeekMs is long t)
        {
            _pendingSeekMs = null;
            _player.Time = t;
        }

        if (_firstPlay)
        {
            _firstPlay = false;
            ApplyVideoAdjust();
            if (Math.Abs(_cfg.DefaultSpeed - 1.0) > 0.001) _player.SetRate((float)_cfg.DefaultSpeed);
            if (!_cfg.SubtitlesOnByDefault) _player.SetSpu(-1);
            if (_cfg.StartFullscreen && !_isFullscreen && !_currentIsAudio && _vault == null) ToggleFullscreen();
        }
    });

    private void ApplyVideoAdjust()
    {
        if (_player is null) return;
        try
        {
            _player.SetAdjustInt(VideoAdjustOption.Enable, 1);
            _player.SetAdjustFloat(VideoAdjustOption.Hue, (float)_cfg.Hue);
            _player.SetAdjustFloat(VideoAdjustOption.Brightness, (float)_cfg.Brightness);
            _player.SetAdjustFloat(VideoAdjustOption.Contrast, (float)_cfg.Contrast);
            _player.SetAdjustFloat(VideoAdjustOption.Saturation, (float)_cfg.Saturation);
            _player.SetAdjustFloat(VideoAdjustOption.Gamma, (float)_cfg.Gamma);
            _player.AspectRatio = _cfg.AspectRatio == "Default" ? null : _cfg.AspectRatio;
        }
        catch { /* no video output yet */ }
    }

    private void Player_LengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
    {
        long len = e.Length;
        Dispatcher.InvokeAsync(() =>
        {
            _lengthMs = len;
            DurationText.Text = Fmt(len);
        });
    }

    private void Player_TimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        long t = e.Time;
        Dispatcher.InvokeAsync(() =>
        {
            TimeText.Text = Fmt(t);
            if (_lengthMs <= 0) return;

            double frac = Math.Clamp((double)t / _lengthMs, 0, 1);
            if (!SeekBar.IsDragging) SeekBar.Progress = frac;
            if (!WaveBar.IsDragging) WaveBar.Progress = frac;

            if (_libraryVisible && MiniProgress.Parent is FrameworkElement parent)
                MiniProgress.Width = parent.ActualWidth * frac;
        }, DispatcherPriority.Background);
    }

    private void ResetTransport()
    {
        SetPlayGlyph(false);
        SetPulse(false);
        NativeMethods.KeepAwake(false);
        TimeText.Text = Fmt(0);
        SeekBar.Progress = 0;
        WaveBar.Progress = 0;
        MiniProgress.Width = 0;
    }

    private void SetPlayGlyph(bool playing)
    {
        var data = (System.Windows.Media.Geometry)FindResource(playing ? "IcoPause" : "IcoPlay");
        PlayPauseIcon.Data = data;
        MiniPlayIcon.Data = data;
    }

    private static string Fmt(long ms)
    {
        var ts = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
    }

    private void SeekBy(int seconds)
    {
        if (_player is null || !_hasMedia || _lengthMs <= 0) return;
        _player.Time = Math.Clamp(_player.Time + seconds * 1000L, 0, _lengthMs);
        ShowOsd((seconds > 0 ? "+" : "") + seconds + " s");
    }

    private void ChangeVolume(int delta)
    {
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + delta, 0, 100);
        ShowOsd("Volume " + (int)VolumeSlider.Value + "%", VolumeSlider.Value / 100.0);
    }

    private void ToggleMute()
    {
        if (_player is null) return;
        _player.Mute = !_player.Mute;
        double o = _player.Mute ? 0.4 : 1.0;
        VolumeIcon.Opacity = o;
        PopupVolumeIcon.Opacity = o;
        ShowOsd(_player.Mute ? "Muted" : "Volume " + (int)VolumeSlider.Value + "%", _player.Mute ? 0.0 : VolumeSlider.Value / 100.0);
    }

    /// <summary>Controls that need something playing are disabled until there is something to act on.</summary>
    private void UpdateTransportEnabled()
    {
        bool has = _hasMedia;
        SeekBar.IsEnabled = has;
        SeekBar.Opacity = has ? 1.0 : 0.5;
        WaveBar.IsEnabled = has;
        WaveBar.Opacity = has ? 1.0 : 0.5;
        BackButton.IsEnabled = has;
        FwdButton.IsEnabled = has;
        AudioButton.IsEnabled = has;
        SubtitleButton.IsEnabled = has;
        PrevButton.IsEnabled = _vault == null && _queue.Count > 1;
        NextButton.IsEnabled = _vault == null && _queue.Count > 1;
        PlayPauseButton.IsEnabled = has || (_vault == null && _queue.Count > 0);
        UpdateMiniPlayer();
    }

    private void UpdateMiniPlayer()
    {
        MiniPlayer.Visibility = _libraryVisible && _hasMedia && _vault == null ? Visibility.Visible : Visibility.Collapsed;
        ApplyPlayerLayout();
    }

    private void MiniExpand_Click(object sender, RoutedEventArgs e) => ShowLibrary(false);

    // ------------------------------------------------------------------ music view + waveform

    private void ShowMusic(bool on, string title, string subtitle)
    {
        _currentIsAudio = on;
        MusicPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Visibility = Visibility.Collapsed;
        MusicTitle.Text = title;
        MusicSubtitle.Text = subtitle;

        // default look (accent colour) until the cover's own colour is known
        CoverCircle.Fill = (System.Windows.Media.Brush)FindResource("CoverFallback");
        MusicGlyph.Visibility = Visibility.Visible;
        PulseRing.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AccentBrush");
        GlowStop.Color = (System.Windows.Media.Color)FindResource("AccentColor");
        SetPulse(false); // the ripples only run while something is actually playing

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

        try
        {
            var peaks = vaultStream != null
                ? await WaveformLoader.FromStreamAsync(vaultStream, cts.Token)
                : await WaveformLoader.FromFileAsync(path, cts.Token);
            if (!cts.IsCancellationRequested) WaveBar.Peaks = peaks;
        }
        catch (OperationCanceledException) { /* a newer track started */ }
        catch { /* keep the flat placeholder bars */ }
    }

    /// <summary>Album art / video frame as the big cover, using the same thumbnail Windows Explorer shows.</summary>
    private async void LoadCover(string path)
    {
        if (!MediaTypes.IsAudio(path)) return;
        var bmp = await Task.Run(() =>
        {
            var raw = ShellThumbnail.Get(path, 600, 600, biggerOk: true);
            return raw == null ? null : ShellThumbnail.TrimBorders(raw);
        });
        if (bmp == null || _vault != null) return;
        if (!string.Equals(_queueIndex >= 0 && _queueIndex < _queue.Count ? _queue[_queueIndex].FullPath : "", path,
                StringComparison.OrdinalIgnoreCase)) return;
        CoverCircle.Fill = new System.Windows.Media.ImageBrush(bmp) { Stretch = System.Windows.Media.Stretch.UniformToFill };
        MusicGlyph.Visibility = Visibility.Collapsed;

        // the ripples and glow take the colour of the cover
        if (ShellThumbnail.DominantColor(bmp) is { } color)
        {
            PulseRing.Stroke = new System.Windows.Media.SolidColorBrush(color);
            GlowStop.Color = color;
        }
    }

    private void SetPulse(bool on)
    {
        if (on)
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
        if (_vault == null) _cfg.Save(notify: false);
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
        if (_vault == null) _cfg.Save(notify: false);
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
            if (_cfg.Loop == LoopMode.Off) icon.ClearValue(System.Windows.Shapes.Shape.StrokeProperty);
            else icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AccentBrush");
            button.ToolTip = tip;
        }

        Apply(LoopIcon, LoopBadge, LoopButton);
        Apply(PlLoopIcon, PlLoopBadge, PlLoopButton);
        Apply(SideLoopIcon, SideLoopBadge, SideLoopButton);
    }

    // ------------------------------------------------------------------ menus

    private MenuItem MenuEntry(string header, string iconKey, RoutedEventHandler click, string gesture = "")
    {
        var path = new System.Windows.Shapes.Path
        {
            Data = (System.Windows.Media.Geometry)FindResource(iconKey),
            Width = 24,
            Height = 24,
            Stretch = System.Windows.Media.Stretch.None,
            StrokeThickness = 1.5,
            StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
            StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
            StrokeLineJoin = System.Windows.Media.PenLineJoin.Round
        };
        path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AccentBrush");

        var mi = new MenuItem
        {
            Header = header,
            InputGestureText = gesture,
            Icon = new Viewbox { Width = 18, Height = 18, Child = path }
        };
        mi.Click += click;
        return mi;
    }

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = sender as UIElement ?? MenuButton, Placement = PlacementMode.Bottom };

        if (_vault == null)
        {
            menu.Items.Add(MenuEntry("Open file(s)", "IcoVideo", Open_Click, "Ctrl+O"));
            menu.Items.Add(MenuEntry("Open folder", "IcoFolder", OpenFolder_Click));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry("Keyboard shortcuts", "IcoKeyboard", Shortcuts_Click));
        }
        menu.Items.Add(MenuEntry("Settings", "IcoSettings", AllSettings_Click, "Ctrl+,"));
        menu.Items.Add(MenuEntry("About EchoDeck", "IcoInfo", About_Click));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Exit", "IcoClose", Close_Click));
        menu.IsOpen = true;
    }

    /// <summary>Right-click menu (video, library, anywhere in the window).</summary>
    private void ShowContextMenu()
    {
        if (_modalOpen || _transition) return;
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };

        if (!_libraryVisible)
        {
            menu.Items.Add(MenuEntry(_player != null && _player.IsPlaying ? "Pause" : "Play", "IcoPlay", PlayPause_Click, "Space"));
            if (_vault == null)
            {
                menu.Items.Add(MenuEntry("Previous", "IcoPrevious", Previous_Click, "Shift+,"));
                menu.Items.Add(MenuEntry("Next", "IcoNext", Next_Click, "Shift+."));
                menu.Items.Add(new Separator());
                menu.Items.Add(MenuEntry("Open file(s)", "IcoVideo", Open_Click, "Ctrl+O"));
                menu.Items.Add(MenuEntry("Show play queue", "IcoPlaylist", (_, _) => ShowSide(SideMode.Queue), "Q"));
                menu.Items.Add(MenuEntry("Show playlists", "IcoPlaylist", (_, _) => ShowSide(SideMode.Playlists), "P"));
                if (_hasMedia && _queueIndex >= 0 && _queueIndex < _queue.Count)
                {
                    var current = _queue[_queueIndex].FullPath;
                    menu.Items.Add(MenuEntry("Properties", "IcoInfo", (_, _) => ShowProperties(current)));
                }
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry("Equaliser", "IcoSliders", (_, _) => OpenQuick(QTabAudio, forceOpen: true)));
            menu.Items.Add(MenuEntry("Speed", "IcoClock", (_, _) => ShowSpeedMenu()));
            menu.Items.Add(MenuEntry("Audio track", "IcoAudio", Audio_Click, "B"));
            menu.Items.Add(MenuEntry("Subtitles", "IcoSubtitles", Subtitle_Click, "V"));
            menu.Items.Add(MenuEntry("Video settings", "IcoSettings", (_, _) => OpenQuick(QTabVideo, forceOpen: true)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry("Fullscreen", "IcoFullscreen", Fullscreen_Click, "F"));
            if (_vault == null)
                menu.Items.Add(MenuEntry("Back to library", "IcoHome", (_, _) => ShowLibrary(true), "Backspace"));
        }
        else if (_vault == null)
        {
            menu.Items.Add(MenuEntry("Open file(s)", "IcoVideo", Open_Click, "Ctrl+O"));
            menu.Items.Add(MenuEntry("Open folder", "IcoFolder", OpenFolder_Click));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry("Settings", "IcoSettings", AllSettings_Click, "Ctrl+,"));
        }

        if (menu.Items.Count > 0) menu.IsOpen = true;
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        MessageBox.Show(this, $"EchoDeck {v?.Major}.{v?.Minor}.{v?.Build}\nA modern media player built on LibVLC.",
            "About EchoDeck", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Shortcuts_Click(object sender, RoutedEventArgs e) => ShowSettings("controls");

    /// <summary>The bundle's gear: opens the quick settings panel on the Video tab.</summary>
    private void Settings_Click(object sender, RoutedEventArgs e) => OpenQuick(QTabVideo);

    private void AllSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_vault != null) OpenAnonymousSettings();
        else ShowSettings(null);
    }

    private void ShowSettings(string? page)
    {
        if (_vault != null || _modalOpen) return;
        _modalOpen = true;
        try { new SettingsWindow(page) { Owner = this }.ShowDialog(); }
        finally { _modalOpen = false; }
        RootGrid.Focus();
    }

    // ------------------------------------------------------------------ quick settings panel

    private void OpenQuick(RadioButton tab, bool forceOpen = false)
    {
        if (_libraryVisible) return;

        if (!forceOpen && QuickPanel.Visibility == Visibility.Visible && tab.IsChecked == true)
        {
            QuickPanel.Visibility = Visibility.Collapsed;
            return;
        }

        tab.IsChecked = true;
        QuickPanel.Visibility = Visibility.Visible;
        RefreshQuick();
    }

    private void QuickClose_Click(object sender, RoutedEventArgs e) => QuickPanel.Visibility = Visibility.Collapsed;

    private void QTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        QPageVideo.Visibility = QTabVideo.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        QPageAudio.Visibility = QTabAudio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        QPageSubs.Visibility = QTabSubs.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RefreshQuick();
    }

    private void RefreshQuick()
    {
        if (!IsLoaded) return;
        _loadingQuick = true;
        try
        {
            QHue.Value = _cfg.Hue;
            QBrightness.Value = _cfg.Brightness;
            QContrast.Value = _cfg.Contrast;
            QSaturation.Value = _cfg.Saturation;
            QGamma.Value = _cfg.Gamma;
            UpdateQuickLabels();

            if (QAspectCombo.Items.Count == 0)
                foreach (var a in AspectNames) QAspectCombo.Items.Add(a);
            QAspectCombo.SelectedIndex = Math.Max(0, Array.IndexOf(AspectNames, _cfg.AspectRatio));

            if (QSpeedCombo.Items.Count == 0)
                foreach (var s in SpeedNames) QSpeedCombo.Items.Add(s);
            int speedIdx = 2;
            for (int i = 0; i < SpeedValues.Length; i++)
                if (Math.Abs(SpeedValues[i] - _cfg.DefaultSpeed) < 0.001) speedIdx = i;
            QSpeedCombo.SelectedIndex = speedIdx;

            FillEqualizerCombo();
            FillTrackCombo(QAudioCombo, _quickAudioIds, audio: true);
            FillTrackCombo(QSubCombo, _quickSubIds, audio: false);
            QAudioHint.Text = QAudioCombo.Items.Count == 0 ? "Nothing is playing yet." : "";
            QAddSubButton.IsEnabled = _hasMedia && _vault == null;
        }
        finally
        {
            _loadingQuick = false;
        }
    }

    private void FillTrackCombo(ComboBox combo, List<int> ids, bool audio)
    {
        combo.Items.Clear();
        ids.Clear();
        if (_player is null || !_hasMedia) return;

        var tracks = audio ? _player.AudioTrackDescription : _player.SpuDescription;
        if (tracks == null) return;
        int current = audio ? _player.AudioTrack : _player.Spu;

        int selected = -1;
        foreach (var t in tracks)
        {
            if (t.Id == current) selected = ids.Count;
            ids.Add(t.Id);
            combo.Items.Add(string.IsNullOrEmpty(t.Name) ? "Track " + t.Id : t.Name);
        }
        combo.SelectedIndex = selected;
    }

    private void UpdateQuickLabels()
    {
        QHueValue.Text = QHue.Value.ToString("0", CultureInfo.CurrentCulture);
        QBrightnessValue.Text = QBrightness.Value.ToString("0.00", CultureInfo.CurrentCulture);
        QContrastValue.Text = QContrast.Value.ToString("0.00", CultureInfo.CurrentCulture);
        QSaturationValue.Text = QSaturation.Value.ToString("0.00", CultureInfo.CurrentCulture);
        QGammaValue.Text = QGamma.Value.ToString("0.00", CultureInfo.CurrentCulture);
    }

    private void QuickSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingQuick || !IsLoaded || sender is not Slider slider) return;

        switch (slider.Name)
        {
            case nameof(QHue): _cfg.Hue = slider.Value; break;
            case nameof(QBrightness): _cfg.Brightness = slider.Value; break;
            case nameof(QContrast): _cfg.Contrast = slider.Value; break;
            case nameof(QSaturation): _cfg.Saturation = slider.Value; break;
            case nameof(QGamma): _cfg.Gamma = slider.Value; break;
            default: return;
        }
        UpdateQuickLabels();
        CommitPlayerSetting();
    }

    private void QuickReset_Click(object sender, RoutedEventArgs e)
    {
        _cfg.Hue = 0; _cfg.Brightness = 1; _cfg.Contrast = 1; _cfg.Saturation = 1; _cfg.Gamma = 1;
        CommitPlayerSetting();
        RefreshQuick();
    }

    private void QuickCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingQuick || !IsLoaded || sender is not ComboBox combo || combo.SelectedIndex < 0 || _player is null) return;
        int i = combo.SelectedIndex;

        switch (combo.Name)
        {
            case nameof(QAudioCombo):
                if (i < _quickAudioIds.Count) _player.SetAudioTrack(_quickAudioIds[i]);
                return;
            case nameof(QSubCombo):
                if (i < _quickSubIds.Count) _player.SetSpu(_quickSubIds[i]);
                return;
            case nameof(QEqCombo):
                ApplyEqualizer(i);
                return;
            case nameof(QAspectCombo):
                _cfg.AspectRatio = AspectNames[i];
                break;
            case nameof(QSpeedCombo):
                _cfg.DefaultSpeed = SpeedValues[i];
                _player.SetRate((float)SpeedValues[i]);
                break;
            default:
                return;
        }
        CommitPlayerSetting();
    }

    /// <summary>Applies picture / aspect settings to the player; saved to disk only outside Anonymous Mode.</summary>
    private void CommitPlayerSetting()
    {
        ApplyVideoAdjust();
        if (_vault == null) _cfg.Save(notify: false);
    }

    private void QuickAddSub_Click(object sender, RoutedEventArgs e) => AddSubtitleFile();

    private void AddSubtitleFile()
    {
        if (_player is null || !_hasMedia || _vault != null) return;
        var dlg = new OpenFileDialog
        {
            Title = "Add subtitle file",
            Filter = "Subtitles|*.srt;*.ass;*.ssa;*.sub;*.vtt|All files|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;
        _player.AddSlave(MediaSlaveType.Subtitle, new Uri(dlg.FileName).AbsoluteUri, true);
        Dispatcher.InvokeAsync(RefreshQuick, DispatcherPriority.Background);
    }

    // ------------------------------------------------------------------ toolbar handlers

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_vault != null) return;
        var dlg = new OpenFileDialog { Title = "Open media", Filter = MediaTypes.DialogFilter, Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;

        var files = dlg.FileNames.Where(MediaTypes.IsSupportedFile).ToList();
        if (files.Count == 0) return;

        var dir = Path.GetDirectoryName(files[0]);
        if (dir != null) NavigateTo(dir);

        foreach (var f in files.Skip(1))
            if (!_queue.Any(q => string.Equals(q.FullPath, f, StringComparison.OrdinalIgnoreCase)))
                _queue.Add(new QueueItem(f));
        PlayNormal(files[0]);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_vault != null) return;
        var dlg = new OpenFolderDialog { Title = "Choose a media folder" };
        if (dlg.ShowDialog(this) != true) return;

        _typeFilter = 0;
        NavHome.IsChecked = true;
        ShowLibrary(true);
        NavigateTo(dlg.FolderName);
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_vault != null) return;
        var dlg = new OpenFileDialog { Title = "Add to playlist", Filter = MediaTypes.DialogFilter, Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;

        foreach (var f in dlg.FileNames.Where(MediaTypes.IsSupportedFile))
            if (!_queue.Any(q => string.Equals(q.FullPath, f, StringComparison.OrdinalIgnoreCase)))
                _queue.Add(new QueueItem(f));

        if (!_hasMedia && _queue.Count > 0) PlayNormal(_queue[0].FullPath);
        RefreshSide();
        UpdateTransportEnabled();
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => PlayAdjacent(-1);

    private void Next_Click(object sender, RoutedEventArgs e) => PlayAdjacent(+1);

    private void SkipBack_Click(object sender, RoutedEventArgs e) => SeekBy(-_cfg.SkipSeconds);

    private void SkipFwd_Click(object sender, RoutedEventArgs e) => SeekBy(_cfg.SkipSeconds);

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayPause();

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e)
    {
        if (_isFullscreen) { ToggleFullscreen(); return; }
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private DateTime _volumePopupClosedAt = DateTime.MinValue;

    /// <summary>The speaker opens the volume slider (a popup), a second click closes it.</summary>
    private void Volume_Click(object sender, RoutedEventArgs e)
    {
        if (VolumePopup.IsOpen) { VolumePopup.IsOpen = false; return; }
        if ((DateTime.UtcNow - _volumePopupClosedAt).TotalMilliseconds < 250) return; // the click that just closed it
        VolumePopup.IsOpen = true;
    }

    private void VolumePopup_Closed(object? sender, EventArgs e) => _volumePopupClosedAt = DateTime.UtcNow;

    private void Mute_Click(object sender, RoutedEventArgs e) => ToggleMute();

    private void Audio_Click(object sender, RoutedEventArgs e) => OpenQuick(QTabAudio);

    private void Subtitle_Click(object sender, RoutedEventArgs e) => OpenQuick(QTabSubs);

    private void CycleTrack(bool audio)
    {
        if (_player is null) return;
        var tracks = audio ? _player.AudioTrackDescription : _player.SpuDescription;
        if (tracks == null || tracks.Length == 0) return;

        int current = audio ? _player.AudioTrack : _player.Spu;
        int idx = Array.FindIndex(tracks, t => t.Id == current);
        var next = tracks[(idx + 1) % tracks.Length];
        if (audio) _player.SetAudioTrack(next.Id); else _player.SetSpu(next.Id);
    }

    private void TakeSnapshot()
    {
        // Never in Anonymous Mode: a screenshot of decrypted video would be written to disk in the clear.
        if (_player is null || !_hasMedia || _vault != null || _currentIsAudio) return;
        try
        {
            var dir = !string.IsNullOrWhiteSpace(_cfg.SnapshotFolder)
                ? _cfg.SnapshotFolder
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), AppName);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "snapshot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png");
            _player.TakeSnapshot(0, file, 0, 0);
        }
        catch { /* best effort */ }
    }

    private void OnWindowStateChanged()
    {
        bool maximized = WindowState == WindowState.Maximized && !_isFullscreen;
        RootGrid.Margin = maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        MaxIcon.Data = (System.Windows.Media.Geometry)FindResource(
            WindowState == WindowState.Maximized ? "IcoRestore" : "IcoMaximize");
    }

    /// <summary>Fullscreen shows only the video: title bar, nav, panels and control bar are all hidden.</summary>
    private void SetChromeVisible(bool visible)
    {
        var v = visible ? Visibility.Visible : Visibility.Collapsed;
        TopBar.Visibility = v;
        Rail.Visibility = v;
        if (visible) ControlBar.Visibility = _libraryVisible ? Visibility.Collapsed : Visibility.Visible;
        SidebarPanel.Visibility = visible && _sidebarVisible && _vault == null && !_libraryVisible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) QuickPanel.Visibility = Visibility.Collapsed;
        ContentGrid.Margin = visible ? new Thickness(8, 0, 8, 8) : new Thickness(0);

        if (_chrome != null)
        {
            _chrome.CaptionHeight = visible ? 40 : 0;
            _chrome.ResizeBorderThickness = visible ? new Thickness(6) : new Thickness(0);
        }
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (_vault != null) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] items || items.Length == 0) return;

        var folder = items.FirstOrDefault(Directory.Exists);
        if (folder != null)
        {
            _typeFilter = 0;
            ShowLibrary(true);
            NavigateTo(folder);
            return;
        }

        var files = items.Where(MediaTypes.IsSupportedFile).ToList();
        if (files.Count == 0) return;

        foreach (var f in files.Skip(1))
            if (!_queue.Any(q => string.Equals(q.FullPath, f, StringComparison.OrdinalIgnoreCase)))
                _queue.Add(new QueueItem(f));
        PlayNormal(files[0]);
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_player != null) _player.Volume = (int)e.NewValue;
        VolumeText.Text = (int)e.NewValue + "%";
        if (IsLoaded && _vault == null && !_transition)
        {
            _cfg.Volume = (int)e.NewValue;
            _volumeSaveTimer ??= CreateVolumeSaveTimer();
            _volumeSaveTimer.Stop();
            _volumeSaveTimer.Start();
        }
    }

    private DispatcherTimer? _volumeSaveTimer;

    private DispatcherTimer CreateVolumeSaveTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) => { t.Stop(); _cfg.Save(notify: false); };
        return t;
    }

    /// <summary>Double-click the picture for fullscreen (a single click does nothing).</summary>
    private void VideoSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_transition || !_hasMedia) return;
        if (e.ClickCount >= 2) ToggleFullscreen();
    }

    private void VideoSurface_MouseRightButtonUp(object sender, MouseButtonEventArgs e) => ShowContextMenu();

    private Rect _prevBounds;

    private void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            if (_libraryVisible)
            {
                if (!_hasMedia) return;
                ShowLibrary(false);
            }

            _prevState = WindowState;
            _prevStyle = WindowStyle;
            _prevResize = ResizeMode;
            _prevBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;

            // Cover the WHOLE monitor (taskbar included), not just the work area.
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null &&
                NativeMethods.TryGetMonitorBounds(new WindowInteropHelper(this).Handle, out var r))
            {
                var tl = source.CompositionTarget.TransformFromDevice.Transform(new Point(r.Left, r.Top));
                var br = source.CompositionTarget.TransformFromDevice.Transform(new Point(r.Right, r.Bottom));
                Left = tl.X;
                Top = tl.Y;
                Width = br.X - tl.X;
                Height = br.Y - tl.Y;
                Topmost = true;
            }
            else
            {
                WindowState = WindowState.Maximized;
            }

            _isFullscreen = true;
            SetChromeVisible(false);
            AttachFloatingBar();
            OnWindowStateChanged();
        }
        else
        {
            Topmost = false;
            WindowStyle = _prevStyle;
            ResizeMode = _prevResize;
            WindowState = WindowState.Normal;
            if (_prevBounds.Width > 0 && _prevBounds.Height > 0)
            {
                Left = _prevBounds.Left;
                Top = _prevBounds.Top;
                Width = _prevBounds.Width;
                Height = _prevBounds.Height;
            }
            if (_prevState == WindowState.Maximized) WindowState = WindowState.Maximized;

            _isFullscreen = false;
            DetachFloatingBar();
            SetChromeVisible(true);
            OnWindowStateChanged();
        }
        SchedulePositionVault();
    }

    // ------------------------------------------------------------------ keyboard (hotkeys, stealth trigger, panic)

    private void OnKeyInput(int vk, bool ctrl, bool appActive)
    {
        // Esc is the panic button. (With the optional global hook it also works while another window
        // is in front; with the default WPF input it works whenever this app is active.)
        if (vk == NativeMethods.VK_ESCAPE)
        {
            if (_vault != null) ExitAnonymous();
            else if (appActive && _isFullscreen && !_modalOpen) ToggleFullscreen();
            return;
        }

        if (_modalOpen || _transition) return; // typing in a dialog must not trigger hotkeys

        if (!appActive)
        {
            _pinDetector.Reset();
            return;
        }

        // Ctrl + passcode digits
        if (ctrl && PinDetector.TryGetDigit(vk, out char digit))
        {
            if (_vault == null && PinStore.IsSet && _pinDetector.Push(digit)) EnterAnonymous();
            return;
        }

        // Modifier presses shouldn't break a passcode in progress.
        if (vk is NativeMethods.VK_CONTROL or NativeMethods.VK_LCONTROL or NativeMethods.VK_RCONTROL
            or 0x10 or 0xA0 or 0xA1 or 0x12 or 0xA4 or 0xA5)
            return;

        _pinDetector.Reset();

        if (Keyboard.FocusedElement is TextBox or PasswordBox) return; // typing in the search box

        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (ctrl)
        {
            if (vk == NativeMethods.VK_O) Open_Click(this, new RoutedEventArgs());
            else if (vk == NativeMethods.VK_OEM_COMMA) AllSettings_Click(this, new RoutedEventArgs());
            return;
        }

        if (_libraryVisible)
        {
            // Library: only a few keys make sense.
            if (vk == NativeMethods.VK_SPACE) TogglePlayPause();
            return;
        }

        switch (vk)
        {
            case NativeMethods.VK_SPACE: TogglePlayPause(); break;
            case NativeMethods.VK_LEFT: SeekBy(-_cfg.SkipSeconds); break;
            case NativeMethods.VK_RIGHT: SeekBy(_cfg.SkipSeconds); break;
            case NativeMethods.VK_UP: ChangeVolume(+5); break;
            case NativeMethods.VK_DOWN: ChangeVolume(-5); break;
            case NativeMethods.VK_F11:
            case NativeMethods.VK_F: ToggleFullscreen(); break;
            case NativeMethods.VK_M: ToggleMute(); break;
            case NativeMethods.VK_V: CycleTrack(audio: false); break;
            case NativeMethods.VK_B: CycleTrack(audio: true); break;
            case NativeMethods.VK_Q:
                ShowSide(SideMode.Queue);
                break;
            case NativeMethods.VK_P:
                ShowSide(SideMode.Playlists);
                break;
            case NativeMethods.VK_BACK:
                if (_vault == null) ShowLibrary(true);
                break;
            case NativeMethods.VK_OEM_COMMA: if (shift) PlayAdjacent(-1); break;
            case NativeMethods.VK_OEM_PERIOD:
                if (shift) PlayAdjacent(+1);
                else if (_player != null && _hasMedia) { _player.SetPause(true); _player.NextFrame(); }
                break;
            case NativeMethods.VK_S: if (shift) TakeSnapshot(); break;
        }
    }

    // ------------------------------------------------------------------ Anonymous Mode

    /// <summary>
    /// Checks the vault key first, then plays the "going dark" splash and, while the screen is fully black,
    /// stops the normal media, swaps the whole UI to the Anonymous theme and builds the pill.
    /// </summary>
    private async void EnterAnonymous()
    {
        if (_vault != null || _player is null || _transition || !PinStore.IsSet) return;
        _transition = true;

        AnonymousSplashWindow? splash = null;
        try
        {
            // 0) Make sure the vault can be opened BEFORE anything changes on screen.
            if (!TryUnlockVault()) return;

            // 1) Freeze the normal media and remember exactly where it was.
            string? mrl = null;
            if (_hasMedia)
            {
                using var media = _player.Media;
                mrl = media?.Mrl;
            }
            if (_player.IsPlaying) _player.SetPause(true);
            _snapshot = mrl != null ? new PlaybackSnapshot(mrl, _player.Time, _currentIsAudio, _normalTitle) : null;

            // 2) Going dark.
            splash = new AnonymousSplashWindow(WindowHelper.GetScreenBoundsDip(this), entering: true);
            splash.Show();
            await splash.PlayDarkenAsync();

            // 3) Under cover of the black screen: STOP the normal media (it must not keep sounding or be resumable
            //    from the Anonymous controls), restyle the UI, build the pill.
            var player = _player;
            await Task.Run(() => player.Stop());
            _hasMedia = false;
            _lengthMs = 0;
            ResetTransport();

            ApplyAnonymousUi(true);
            _vault = new AnonymousPanelWindow(_vaultService)
            {
                Owner = this,
                PlayHandler = PlayVaultItem,
                DeleteHandler = DeleteVaultItemAsync
            };
            _vault.EscapePressed += () => ExitAnonymous();
            _vault.SettingsRequested += OpenAnonymousSettings;
            UpdateTransportEnabled();
            PositionVault();

            // 4) Lights up in Anonymous Mode.
            await splash.PlayRevealAsync();
            splash.Close();
            splash = null;

            _vault.Show();
            PositionVault();
        }
        catch (Exception ex)
        {
            try { splash?.Close(); } catch { }
            MessageBox.Show(this, "Couldn't open Anonymous Mode: " + ex.Message, AppName,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _transition = false;
        }
    }

    /// <summary>Loads the vault key; if existing encrypted files have lost their key, offers to start a fresh vault.</summary>
    private bool TryUnlockVault()
    {
        try
        {
            _vaultService.Unlock();
            return true;
        }
        catch (FileNotFoundException)
        {
            var answer = MessageBox.Show(this,
                "EchoDeck can't find the key for the encrypted files already in the vault, so they can't be opened.\n\n" +
                "Start a new, empty vault? The old files are moved to an \"unreadable\" folder inside the vault folder " +
                "and can't be recovered without their original key.",
                "Anonymous Mode", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return false;

            try
            {
                _vaultService.ResetUnreadable();
                _vaultService.Unlock();
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't start a new vault: " + ex.Message, AppName,
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't open the vault: " + ex.Message, AppName,
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>Restyles the player for (or back from) Anonymous Mode.</summary>
    private void ApplyAnonymousUi(bool on)
    {
        ThemeManager.Apply(_cfg.Theme, _cfg.AccentIndex, anonymous: on);
        Rail.IsEnabled = !on;
        QuickPanel.Visibility = Visibility.Collapsed;
        MiniPlayer.Visibility = Visibility.Collapsed;
        WaveBar.InvalidateVisual();

        if (on)
        {
            ShowLibrary(false);
            SidebarPanel.Visibility = Visibility.Collapsed;
            SetTitles(AppName);
            ShowMusic(false, "", "");
            EmptyHint.Visibility = Visibility.Visible;
        }
        else
        {
            SidebarPanel.Visibility = _sidebarVisible && !_isFullscreen && !_libraryVisible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Panic exit: destroy the pill, wipe vault state, restore the normal media exactly. All of that happens
    /// immediately; the "lights on" animation is only cosmetic and plays over a black cover.
    /// </summary>
    private async void ExitAnonymous(bool animate = true)
    {
        var vault = _vault;
        if (vault == null || _player is null || _transition) return;
        _transition = true;

        AnonymousSplashWindow? splash = null;
        try
        {
            vault.Hide(); // gone from the screen right now

            if (animate && WindowState != WindowState.Minimized)
            {
                try
                {
                    splash = new AnonymousSplashWindow(WindowHelper.GetScreenBoundsDip(this), entering: false);
                    splash.Show();
                    splash.SetBlack();
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render); // let the black cover paint first
                }
                catch { splash = null; }
            }

            _vault = null;
            _anonSettings?.Close();
            vault.Wipe();
            vault.Close();
            _waveCts?.Cancel();
            WaveBar.Peaks = null;

            var snapshot = _snapshot;
            _snapshot = null;

            if (snapshot != null)
            {
                // Reload the saved path, resume at the saved timestamp.
                ShowMusic(snapshot.WasAudio, Path.GetFileNameWithoutExtension(snapshot.Title), "");
                StartPlayback(snapshot.Mrl, snapshot.TimeMs, isMrl: true);
                RetireCurrentVault();
                _normalTitle = snapshot.Title;
                if (snapshot.WasAudio)
                {
                    try { LoadWaveform(new Uri(snapshot.Mrl).LocalPath, null); } catch { /* flat bars */ }
                }
            }
            else if (_hasMedia)
            {
                // Nothing was playing before: just stop the vault media.
                _hasMedia = false;
                var player = _player;
                Task.Run(() => player.Stop()).ContinueWith(_ => Dispatcher.InvokeAsync(RetireCurrentVault));
                ShowMusic(false, "", "");
                EmptyHint.Visibility = Visibility.Visible;
            }
            else
            {
                RetireCurrentVault();
                EmptyHint.Visibility = Visibility.Visible;
            }

            _vaultService.Lock(); // zero the master key in memory
            ApplyAnonymousUi(false);
            SetTitles(snapshot != null ? _normalTitle : AppName);
            if (snapshot == null)
            {
                ResetTransport();
                ShowLibrary(true);
            }
            else
            {
                ShowLibrary(false);
            }
            UpdateTransportEnabled();

            if (splash != null)
            {
                await splash.PlayRevealAsync();
                splash.Close();
                splash = null;
            }
        }
        catch (Exception)
        {
            try { splash?.Close(); } catch { }
        }
        finally
        {
            _transition = false;
        }
    }

    private async void OpenAnonymousSettings()
    {
        if (_vault == null || _anonSettings != null) return;

        var win = new AnonymousSettingsWindow(_vaultService, PlayVaultItem, DeleteVaultItemAsync) { Owner = this };
        _anonSettings = win;
        _modalOpen = true;
        try { win.ShowDialog(); }
        finally
        {
            _anonSettings = null;
            _modalOpen = false;
        }

        if (_vault != null) await _vault.RefreshAsync();
        RootGrid.Focus();
    }

    /// <summary>Plays an encrypted vault file by decrypting it on the fly into LibVLC. Returns an error message or null.</summary>
    private string? PlayVaultItem(VaultItem item)
    {
        if (_vault == null || _libVlc is null || _player is null) return "The player isn't ready.";

        EncryptedVaultStream? stream = null;
        try
        {
            stream = _vaultService.OpenRead(item.FullPath);
            var input = new StreamMediaInput(stream);
            var media = new Media(_libVlc, input);

            _pendingSeekMs = null;
            _firstPlay = true;
            if (!_player.Play(media))
            {
                stream.Dispose();
                media.Dispose();
                return "LibVLC couldn't start playback.";
            }

            // Swap in the new vault media, then release the previous one.
            var (oldStream, oldInput, oldMedia) = (_vaultStream, _vaultInput, _vaultMedia);
            _vaultStream = stream;
            _vaultInput = input;
            _vaultMedia = media;
            _vaultPath = item.FullPath;
            _hasMedia = true;
            _lengthMs = 0;
            SeekBar.Progress = 0;
            WaveBar.Progress = 0;

            ShowMusic(item.IsAudio, Path.GetFileNameWithoutExtension(item.Name), "");
            if (!item.IsAudio) EmptyHint.Visibility = Visibility.Collapsed;
            UpdateTransportEnabled();

            // The waveform reads through its OWN decrypting stream, so nothing is written anywhere.
            if (item.IsAudio)
            {
                try { LoadWaveform(item.FullPath, _vaultService.OpenRead(item.FullPath)); } catch { /* flat bars */ }
            }

            RetireVault(oldStream, oldInput, oldMedia);
            return null;
        }
        catch (Exception ex)
        {
            stream?.Dispose();
            return "Can't play: " + ex.Message;
        }
    }

    /// <summary>
    /// Permanently deletes a vault file. If it's the media LibVLC is reading, playback is stopped and the
    /// stream released first, so Windows doesn't report "file in use".
    /// </summary>
    private async Task<string?> DeleteVaultItemAsync(VaultItem item)
    {
        var player = _player;
        if (player != null && _vaultPath != null &&
            string.Equals(_vaultPath, item.FullPath, StringComparison.OrdinalIgnoreCase))
        {
            _waveCts?.Cancel();
            await Task.Run(() => player.Stop()); // off the UI thread: Stop can block
            _hasMedia = false;
            RetireCurrentVault();
            ResetTransport();
            ShowMusic(false, "", "");
            EmptyHint.Visibility = Visibility.Visible;
            UpdateTransportEnabled();
        }

        try
        {
            await _vaultService.SecureDeleteAsync(item.FullPath);
            return null;
        }
        catch (Exception ex)
        {
            return "Delete failed: " + ex.Message;
        }
    }

    private void RetireCurrentVault()
    {
        var (stream, input, media) = (_vaultStream, _vaultInput, _vaultMedia);
        _vaultStream = null;
        _vaultInput = null;
        _vaultMedia = null;
        _vaultPath = null;
        RetireVault(stream, input, media);
    }

    private static void RetireVault(EncryptedVaultStream? stream, StreamMediaInput? input, Media? media)
    {
        // Closing the stream releases the file handle right now; a late LibVLC read just sees end-of-stream.
        stream?.Dispose();

        if (input == null && media == null) return;

        // Give LibVLC a moment to finish closing before its native wrappers are released.
        Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            try { media?.Dispose(); } catch { /* already released */ }
            try { input?.Dispose(); } catch { /* already released */ }
        });
    }

    private void SchedulePositionVault() => Dispatcher.InvokeAsync(PositionVault, DispatcherPriority.Loaded);

    private void PositionVault()
    {
        if (_vault == null || !IsLoaded || WindowState == WindowState.Minimized) return;

        var source = PresentationSource.FromVisual(VideoHost);
        if (source?.CompositionTarget == null) return;

        var devicePoint = VideoHost.PointToScreen(new Point(0, 0));
        var dip = source.CompositionTarget.TransformFromDevice.Transform(devicePoint);
        _vault.Left = dip.X + 14;
        _vault.Top = dip.Y + 14;
    }
}
