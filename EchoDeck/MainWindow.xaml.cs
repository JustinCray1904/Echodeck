using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;

namespace EchoDeck;

public partial class MainWindow : Window
{
    private const string AppName = "EchoDeck";

    private readonly AppSettings _cfg = AppSettings.Current;

    // Playback
    private LibVLC? _libVlc;
    private MediaPlayer? _player;
    private bool _hasMedia;
    private bool _firstPlay;
    private bool _currentIsAudio;
    private long _lengthMs;
    private long? _pendingSeekMs;
    private bool _isSeeking;
    private string _normalTitle = AppName;

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

    // Side panel: file browser + session queue. In memory only: nothing about what was watched is ever saved.
    private readonly ObservableCollection<BrowserEntry> _entries = new();
    private readonly ObservableCollection<QueueItem> _queue = new();
    private ICollectionView? _entriesView;
    private string? _currentDir;
    private int _typeFilter;     // 0 all, 1 videos, 2 music
    private int _queueIndex = -1;
    private bool _sidebarVisible = true;
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

        BrowserList.ItemsSource = _entries;
        QueueList.ItemsSource = _queue;
        _entriesView = CollectionViewSource.GetDefaultView(_entries);
        _entriesView.Filter = FilterEntry;

#if GLOBAL_KEY_HOOK
        _keys = new GlobalKeyboardHook(Dispatcher);
#else
        _keys = new WpfKeyInput(Dispatcher);
#endif

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

    // ------------------------------------------------------------------ startup / shutdown

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        CreatePlayer();

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
        SetSidebarVisible(_sidebarVisible);

        Dispatcher.InvokeAsync(StartupFlow, DispatcherPriority.ApplicationIdle);
    }

    private void CreatePlayer()
    {
        var args = new List<string>
        {
            "--no-video-title-show",
            _cfg.HardwareDecoding ? "--avcodec-hw=any" : "--avcodec-hw=none",
            "--freetype-rel-fontsize=" + new[] { 18, 16, 12, 6 }[Math.Clamp(_cfg.SubtitleSize, 0, 3)]
        };

        _libVlc = new LibVLC(args.ToArray());
        _player = new MediaPlayer(_libVlc)
        {
            // Let WPF see keyboard/mouse instead of libvlc's native window.
            EnableKeyInput = false,
            EnableMouseInput = false
        };

        _player.Playing += Player_Playing;
        _player.Paused += (_, _) => Dispatcher.InvokeAsync(() => { SetPlayGlyph(false); NativeMethods.KeepAwake(false); });
        _player.Stopped += (_, _) => Dispatcher.InvokeAsync(ResetTransport);
        _player.EndReached += (_, _) => Dispatcher.InvokeAsync(OnEndReached);
        _player.LengthChanged += Player_LengthChanged;
        _player.TimeChanged += Player_TimeChanged;

        VideoView.MediaPlayer = _player;
    }

    private void StartupFlow()
    {
        if (!_cfg.OnboardingDone) ShowOnboarding();

        var folder = _cfg.MediaFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            folder = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (Directory.Exists(folder)) NavigateTo(folder);

        // Command line / "Open with": EchoDeck.exe "C:\path\video.mp4"
        var file = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(MediaTypes.IsSupportedFile);
        if (file != null) PlayNormal(file);
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
        QueueList.SelectedItem = existing;
        UpdateSideUi();

        var name = Path.GetFileName(path);
        _normalTitle = name;
        SetTitles(name);

        bool audio = MediaTypes.IsAudio(path);
        ShowMusic(audio, Path.GetFileNameWithoutExtension(path), Path.GetFileName(Path.GetDirectoryName(path) ?? ""));
        StartPlayback(path, 0, isMrl: false);
    }

    private void SetTitles(string name)
    {
        TitleText.Text = name;
        Title = name == AppName ? AppName : name + " - " + AppName;
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
        _player.Play(media);
    }

    private void TogglePlayPause()
    {
        if (_player is null || !_hasMedia) return;
        if (_player.IsPlaying) _player.Pause(); else _player.Play();
    }

    // Player events arrive on LibVLC threads: marshal to the UI thread, never call back into the player inline.
    private void Player_Playing(object? sender, EventArgs e) => Dispatcher.InvokeAsync(() =>
    {
        if (_player is null) return;
        SetPlayGlyph(true);
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
            if (!_isSeeking && _lengthMs > 0)
                SeekSlider.Value = Math.Min(SeekSlider.Maximum, t * SeekSlider.Maximum / _lengthMs);
        }, DispatcherPriority.Background);
    }

    private void ResetTransport()
    {
        SetPlayGlyph(false);
        NativeMethods.KeepAwake(false);
        TimeText.Text = Fmt(0);
        if (!_isSeeking) SeekSlider.Value = 0;
    }

    private void SetPlayGlyph(bool playing) =>
        PlayPauseIcon.Data = (System.Windows.Media.Geometry)FindResource(playing ? "IcoPause" : "IcoPlay");

    private static string Fmt(long ms)
    {
        var ts = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
    }

    private void SeekBy(int seconds)
    {
        if (_player is null || !_hasMedia || _lengthMs <= 0) return;
        _player.Time = Math.Clamp(_player.Time + seconds * 1000L, 0, _lengthMs);
    }

    private void ChangeVolume(int delta) =>
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + delta, 0, 100);

    private void ToggleMute()
    {
        if (_player is null) return;
        _player.Mute = !_player.Mute;
        VolumeIcon.Opacity = _player.Mute ? 0.4 : 1.0;
    }

    // ------------------------------------------------------------------ music view

    private void ShowMusic(bool on, string title, string subtitle)
    {
        _currentIsAudio = on;
        MusicPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Visibility = Visibility.Collapsed;
        MusicTitle.Text = title;
        MusicSubtitle.Text = subtitle;
        SetPulse(on);
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

    // ------------------------------------------------------------------ side panel (browser + queue)

    private bool FilterEntry(object o)
    {
        if (o is not BrowserEntry entry) return false;

        var text = SearchBox?.Text?.Trim() ?? "";
        if (text.Length > 0 && entry.Name.IndexOf(text, StringComparison.CurrentCultureIgnoreCase) < 0) return false;
        if (entry.IsDirectory) return true;
        return _typeFilter switch
        {
            1 => !entry.IsAudio,
            2 => entry.IsAudio,
            _ => true
        };
    }

    private void NavigateTo(string dir)
    {
        var list = new List<BrowserEntry>();
        try
        {
            foreach (var d in Directory.EnumerateDirectories(dir).OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase))
            {
                var attr = File.GetAttributes(d);
                if ((attr & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                list.Add(new BrowserEntry(Path.GetFileName(d), d, true));
            }

            foreach (var f in Directory.EnumerateFiles(dir)
                         .Where(f => MediaTypes.IsSupportedExtension(Path.GetExtension(f)))
                         .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase))
                list.Add(new BrowserEntry(Path.GetFileName(f), f, false));
        }
        catch (Exception)
        {
            return; // folder vanished or access denied: stay where we are
        }

        _currentDir = dir;
        _entries.Clear();
        foreach (var entry in list) _entries.Add(entry);

        var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(name)) name = dir;
        BrowserPathText.Text = name;
        BreadcrumbText.Text = name;
        _entriesView?.Refresh();
        UpdateSideUi();
    }

    private void UpdateSideUi()
    {
        bool files = TabFiles.IsChecked == true;
        BrowserList.Visibility = files ? Visibility.Visible : Visibility.Collapsed;
        QueueList.Visibility = files ? Visibility.Collapsed : Visibility.Visible;

        int count = files
            ? (_entriesView?.Cast<object>().Count() ?? 0)
            : _queue.Count;
        CountText.Text = count == 1 ? "1 item" : $"{count} items";
        SideEmptyHint.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SideEmptyHint.Text = files
            ? "Nothing here. Open a folder or drop media onto the window."
            : "The queue is empty. Double-click a file to start playing.";
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _entriesView?.Refresh();
        UpdateSideUi();
    }

    private void SideTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateSideUi();
    }

    private void RailFilter_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (sender == RailVideosToggle) RailMusicToggle.IsChecked = false;
        if (sender == RailMusicToggle) RailVideosToggle.IsChecked = false;
        _typeFilter = RailVideosToggle.IsChecked == true ? 1 : RailMusicToggle.IsChecked == true ? 2 : 0;

        TabFiles.IsChecked = true;
        SetSidebarVisible(true);
        _entriesView?.Refresh();
        UpdateSideUi();
    }

    private void BrowserUp_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDir == null) return;
        var parent = Directory.GetParent(_currentDir);
        if (parent != null) NavigateTo(parent.FullName);
    }

    private void BrowserList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (BrowserList.SelectedItem is not BrowserEntry entry) return;
        if (entry.IsDirectory) { NavigateTo(entry.FullPath); return; }
        if (_vault != null) return;

        // The visible files of this folder become the queue, so previous / next walk through the folder.
        _queue.Clear();
        foreach (var f in _entriesView!.Cast<BrowserEntry>().Where(x => !x.IsDirectory))
            _queue.Add(new QueueItem(f.FullPath));

        PlayNormal(entry.FullPath);
        RootGrid.Focus();
    }

    private void QueueList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (QueueList.SelectedItem is QueueItem item) PlayNormal(item.FullPath);
        RootGrid.Focus();
    }

    private void PlayAdjacent(int delta)
    {
        if (_vault != null || _queue.Count == 0) return;
        int i = _queueIndex + delta;
        if (i < 0 || i >= _queue.Count) return;
        PlayNormal(_queue[i].FullPath);
    }

    private void OnEndReached()
    {
        ResetTransport();
        if (_vault != null) return; // a vault item that ends never touches the playlist

        if (_cfg.Loop == LoopMode.One && _queueIndex >= 0 && _queueIndex < _queue.Count)
        {
            PlayNormal(_queue[_queueIndex].FullPath);
            return;
        }

        if (!_cfg.AutoAdvance && _cfg.Loop != LoopMode.All) return;

        if (_queueIndex >= 0 && _queueIndex + 1 < _queue.Count)
            PlayNormal(_queue[_queueIndex + 1].FullPath);
        else if (_cfg.Loop == LoopMode.All && _queue.Count > 0)
            PlayNormal(_queue[0].FullPath);
    }

    private void SidebarToggle_Click(object sender, RoutedEventArgs e)
    {
        bool on = (sender as ToggleButton)?.IsChecked == true;
        SetSidebarVisible(on);
    }

    private void SetSidebarVisible(bool visible)
    {
        _sidebarVisible = visible;
        RailLibraryToggle.IsChecked = visible;
        TopSidebarToggle.IsChecked = visible;
        ControlsPlaylistToggle.IsChecked = visible;
        SidebarPanel.Visibility = visible && !_isFullscreen && _vault == null ? Visibility.Visible : Visibility.Collapsed;
        SchedulePositionVault();
    }

    // ------------------------------------------------------------------ UI handlers

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MenuButton, Placement = PlacementMode.Bottom };

        MenuItem Item(string header, RoutedEventHandler click, string gesture = "")
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture };
            mi.Click += click;
            return mi;
        }

        if (_vault == null)
        {
            menu.Items.Add(Item("Open file…", Open_Click, "Ctrl+O"));
            menu.Items.Add(Item("Open folder…", OpenFolder_Click));
            if (_cfg.ShowAnonymousInMenu || !PinStore.IsSet)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(Item(PinStore.IsSet ? "Anonymous Mode…" : "Set up Anonymous Mode…", AnonMenu_Click));
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Settings", Settings_Click, "Ctrl+,"));
        }
        menu.Items.Add(Item("Keyboard shortcuts", Shortcuts_Click));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Exit", Close_Click));
        menu.IsOpen = true;
    }

    private void AnonMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_vault != null) return;
        if (!PinStore.IsSet)
        {
            // No passcode yet: Settings > Privacy is where it is created.
            ShowSettings("privacy");
            return;
        }

        _modalOpen = true;
        bool ok;
        try { ok = new PasscodePromptWindow { Owner = this }.ShowDialog() == true; }
        finally { _modalOpen = false; }

        if (ok) EnterAnonymous();
    }

    private void Shortcuts_Click(object sender, RoutedEventArgs e) => ShowSettings("controls");

    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings(null);

    private void ShowSettings(string? page)
    {
        if (_vault != null || _modalOpen) return;
        _modalOpen = true;
        try { new SettingsWindow(page) { Owner = this }.ShowDialog(); }
        finally { _modalOpen = false; }
        RootGrid.Focus();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_vault != null) return;
        var dlg = new OpenFileDialog { Title = "Open media", Filter = MediaTypes.DialogFilter };
        if (dlg.ShowDialog(this) != true) return;

        var dir = Path.GetDirectoryName(dlg.FileName);
        if (dir != null) NavigateTo(dir);
        PlayNormal(dlg.FileName);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_vault != null) return;
        var dlg = new OpenFolderDialog { Title = "Choose a media folder" };
        if (dlg.ShowDialog(this) != true) return;

        NavigateTo(dlg.FolderName);
        TabFiles.IsChecked = true;
        SetSidebarVisible(true);
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_vault != null) return;
        var dlg = new OpenFileDialog { Title = "Add to queue", Filter = MediaTypes.DialogFilter, Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;

        foreach (var f in dlg.FileNames.Where(MediaTypes.IsSupportedFile))
            if (!_queue.Any(q => string.Equals(q.FullPath, f, StringComparison.OrdinalIgnoreCase)))
                _queue.Add(new QueueItem(f));

        if (!_hasMedia && _queue.Count > 0) PlayNormal(_queue[0].FullPath);
        TabQueue.IsChecked = true;
        UpdateSideUi();
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

    private void Volume_Click(object sender, RoutedEventArgs e) => ToggleMute();

    private void Audio_Click(object sender, RoutedEventArgs e) => ShowTrackMenu(AudioButton, audio: true);

    private void Subtitle_Click(object sender, RoutedEventArgs e) => ShowTrackMenu(SubtitleButton, audio: false);

    private void ShowTrackMenu(FrameworkElement anchor, bool audio)
    {
        if (_player is null) return;

        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Top };
        var tracks = audio ? _player.AudioTrackDescription : _player.SpuDescription;
        int current = audio ? _player.AudioTrack : _player.Spu;

        if (tracks == null || tracks.Length == 0)
        {
            menu.Items.Add(new MenuItem { Header = audio ? "No audio tracks" : "No subtitle tracks", IsEnabled = false });
        }
        else
        {
            foreach (var t in tracks)
            {
                int id = t.Id;
                var mi = new MenuItem { Header = string.IsNullOrEmpty(t.Name) ? "Track " + id : t.Name, IsChecked = id == current };
                mi.Click += (_, _) =>
                {
                    if (_player == null) return;
                    if (audio) _player.SetAudioTrack(id); else _player.SetSpu(id);
                };
                menu.Items.Add(mi);
            }
        }

        if (!audio && _vault == null)
        {
            menu.Items.Add(new Separator());
            var add = new MenuItem { Header = "Add subtitle file…" };
            add.Click += (_, _) => AddSubtitleFile();
            menu.Items.Add(add);
        }
        menu.IsOpen = true;
    }

    private void AddSubtitleFile()
    {
        if (_player is null || !_hasMedia) return;
        var dlg = new OpenFileDialog
        {
            Title = "Add subtitle file",
            Filter = "Subtitles|*.srt;*.ass;*.ssa;*.sub;*.vtt|All files|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;
        _player.AddSlave(MediaSlaveType.Subtitle, new Uri(dlg.FileName).AbsoluteUri, true);
    }

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
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), AppName);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "snapshot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png");
            _player.TakeSnapshot(0, file, 0, 0);
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// A borderless (WindowChrome) window that's maximized extends past the screen by its resize border, so inset the
    /// content by that amount; also keeps the maximize/restore icon in sync.
    /// </summary>
    private void OnWindowStateChanged()
    {
        bool maximized = WindowState == WindowState.Maximized && !_isFullscreen;
        RootGrid.Margin = maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        MaxIcon.Data = (System.Windows.Media.Geometry)FindResource(
            WindowState == WindowState.Maximized ? "IcoRestore" : "IcoMaximize");
    }

    /// <summary>Fullscreen shows only the video: title bar, rail, side panel and control bar are all hidden.</summary>
    private void SetChromeVisible(bool visible)
    {
        var v = visible ? Visibility.Visible : Visibility.Collapsed;
        TopBar.Visibility = v;
        Rail.Visibility = v;
        ControlBar.Visibility = v;
        SidebarPanel.Visibility = visible && _sidebarVisible && _vault == null ? Visibility.Visible : Visibility.Collapsed;

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
            NavigateTo(folder);
            TabFiles.IsChecked = true;
            SetSidebarVisible(true);
            return;
        }

        var files = items.Where(MediaTypes.IsSupportedFile).ToList();
        if (files.Count == 0) return;

        foreach (var f in files.Skip(1))
            if (!_queue.Any(q => string.Equals(q.FullPath, f, StringComparison.OrdinalIgnoreCase)))
                _queue.Add(new QueueItem(f));
        PlayNormal(files[0]);
    }

    private void SeekSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e) => _isSeeking = true;

    private void SeekSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isSeeking && _player != null && _hasMedia && _lengthMs > 0)
            _player.Time = (long)(SeekSlider.Value / SeekSlider.Maximum * _lengthMs);
        _isSeeking = false;
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isSeeking && _lengthMs > 0)
            TimeText.Text = Fmt((long)(e.NewValue / SeekSlider.Maximum * _lengthMs));
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_player != null) _player.Volume = (int)e.NewValue;
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

    private void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            _prevState = WindowState;
            _prevStyle = WindowStyle;
            _prevResize = ResizeMode;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            _isFullscreen = true;
            SetChromeVisible(false);
            OnWindowStateChanged();
        }
        else
        {
            WindowStyle = _prevStyle;
            ResizeMode = _prevResize;
            WindowState = _prevState;
            _isFullscreen = false;
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
            else if (vk == NativeMethods.VK_OEM_COMMA) Settings_Click(this, new RoutedEventArgs());
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
                if (_sidebarVisible && TabQueue.IsChecked == true) SetSidebarVisible(false);
                else { TabQueue.IsChecked = true; SetSidebarVisible(true); UpdateSideUi(); }
                break;
            case NativeMethods.VK_L:
                if (_sidebarVisible && TabFiles.IsChecked == true) SetSidebarVisible(false);
                else { TabFiles.IsChecked = true; SetSidebarVisible(true); UpdateSideUi(); }
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
    /// Freezes the normal media, plays the "going dark" splash, and (while the screen is fully black) swaps the whole
    /// UI to the Anonymous theme and loads the vault.
    /// </summary>
    private async void EnterAnonymous()
    {
        if (_vault != null || _player is null || _transition || !PinStore.IsSet) return;
        _transition = true;

        AnonymousSplashWindow? splash = null;
        try
        {
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

            // 3) Under cover of the black screen: load the key, restyle the UI, build the pill.
            string? unlockError = null;
            try { _vaultService.Unlock(); }
            catch (Exception ex) { unlockError = ex.Message; }

            ApplyAnonymousUi(true);
            _vault = new AnonymousPanelWindow(_vaultService, unlockError)
            {
                Owner = this,
                PlayHandler = PlayVaultItem,
                DeleteHandler = DeleteVaultItemAsync
            };
            _vault.EscapePressed += () => ExitAnonymous();
            _vault.SettingsRequested += OpenAnonymousSettings;
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

    /// <summary>Restyles the player for (or back from) Anonymous Mode.</summary>
    private void ApplyAnonymousUi(bool on)
    {
        ThemeManager.Apply(_cfg.Theme, _cfg.AccentIndex, anonymous: on);
        Rail.IsEnabled = !on;

        if (on)
        {
            SidebarPanel.Visibility = Visibility.Collapsed;
            SetTitles(AppName);
            BreadcrumbText.Text = "Anonymous";
            ShowMusic(false, "", "");
            EmptyHint.Visibility = Visibility.Visible;
        }
        else
        {
            SidebarPanel.Visibility = _sidebarVisible && !_isFullscreen ? Visibility.Visible : Visibility.Collapsed;
            BreadcrumbText.Text = string.IsNullOrEmpty(_currentDir)
                ? "Library"
                : (Path.GetFileName(_currentDir.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : _currentDir);
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

            var snapshot = _snapshot;
            _snapshot = null;

            if (snapshot != null)
            {
                // Reload the saved path, resume at the saved timestamp.
                ShowMusic(snapshot.WasAudio, Path.GetFileNameWithoutExtension(snapshot.Title), "");
                StartPlayback(snapshot.Mrl, snapshot.TimeMs, isMrl: true);
                RetireCurrentVault();
                _normalTitle = snapshot.Title;
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
            if (snapshot == null) ResetTransport();

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

            ShowMusic(item.IsAudio, Path.GetFileNameWithoutExtension(item.Name), "Anonymous Mode");
            if (!item.IsAudio) EmptyHint.Visibility = Visibility.Collapsed;

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
            await Task.Run(() => player.Stop()); // off the UI thread: Stop can block
            _hasMedia = false;
            RetireCurrentVault();
            ResetTransport();
            ShowMusic(false, "", "");
            EmptyHint.Visibility = Visibility.Visible;
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

/// <summary>One row in the side-panel file browser.</summary>
public sealed record BrowserEntry(string Name, string FullPath, bool IsDirectory)
{
    public bool IsAudio => !IsDirectory && MediaTypes.IsAudio(FullPath);
    public System.Windows.Media.Geometry Icon => IconSet.Get(IsDirectory ? "IcoFolder" : IsAudio ? "IcoMusic" : "IcoVideo");
}

/// <summary>One row in the session queue.</summary>
public sealed record QueueItem(string FullPath)
{
    public string Name => Path.GetFileName(FullPath);
    public System.Windows.Media.Geometry Icon => IconSet.Get(MediaTypes.IsAudio(FullPath) ? "IcoMusic" : "IcoVideo");
}

internal static class IconSet
{
    public static System.Windows.Media.Geometry Get(string key) =>
        (System.Windows.Media.Geometry)Application.Current.FindResource(key);
}
