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
    private const string AppName = "Tempo";
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
    private bool _anon;   // Anonymous Mode is on (the vault feeds the library pages, queue and playlists)
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
                if (_lengthMs <= 0) return;
                long ms = (long)(frac * _lengthMs);
                TimeText.Text = Fmt(ms);

                // the song follows the handle while it is dragged (about every 150 ms), like VLC
                if (_player != null && _hasMedia && Environment.TickCount64 - _lastScrubSeek > 150)
                {
                    _lastScrubSeek = Environment.TickCount64;
                    if (_xfActive) FinishCrossfadeNow();
                    _player.Time = ms;
                    OnSeeked(ms);
                }
            };
            bar.SeekCommitted += frac =>
            {
                if (_player == null || !_hasMedia || _lengthMs <= 0) return;
                long ms = (long)(frac * _lengthMs);
                if (_playbackEnded) { _playbackEnded = false; RestartCurrent(); return; }
                if (_xfActive) FinishCrossfadeNow();
                _player.Time = ms;
                OnSeeked(ms);
            };
        }

        RootGrid.MouseRightButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && IsInside(d, TopBar)) return;
            if (TryShowCoverMenu(e)) return;
            ShowContextMenu();
        };

        StateChanged += (_, _) =>
        {
            OnWindowStateChanged();
            if (WindowState == WindowState.Minimized && _anon && _cfg.AnonymousExitOnMinimize)
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
        // letting go of Ctrl forgets a half-typed passcode, except while the passcode box is open: there the digits are typed into the box
        _keys.CtrlReleased += () => { if (_codeBox == null) _pinDetector.Reset(); };
        if (!_keys.Start())
        {
            TempoDialog.Show(this, "Keyboard input could not be initialised; Ctrl+passcode and Esc won't work.",
                AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        AppSettings.Changed += OnSettingsChanged;

        VolumeSlider.Value = Math.Clamp(_cfg.Volume, 0, 200);
        _sidebarVisible = _cfg.ShowSidebarAtStart;
        InitLibrary();
        InitMini();
        InitPopups();
        InitLyrics();
        InitResponsive();
        ApplyHideUi();
        ApplyVisualizerStyle();
        ApplyViewMode();
        ApplyRail();
        _folderSig = FolderSignature();
        UpdateLoopUi();
        UpdateShuffleUi();
        UpdateTransportEnabled();

        // The window (with the Tempo logo) is already on screen here. Starting LibVLC scans its plugins, which is the
        // slow part of start-up, so it runs off the UI thread instead of freezing the window.
        await CreatePlayerAsync();

        // The video surface was created while visible; only now is the library shown on top of it.
        ShowLibrary(true);
        await StartupFlowAsync();
    }

    private async Task CreatePlayerAsync()
    {
        var args = new List<string>
        {
            "--no-video-title-show",
            "--freetype-rel-fontsize=" + new[] { 18, 16, 12, 6 }[Math.Clamp(_cfg.SubtitleSize, 0, 3)],
            "--network-caching=" + Math.Clamp(_cfg.NetworkCachingMs, 100, 10000)
        };

        // Hardware decoding: automatic picks the best GPU path; Direct3D 11 asks for the zero-copy route
        // (decoded frames stay on the GPU and go straight to the D3D11 video output).
        if (!_cfg.HardwareDecoding) args.Add("--avcodec-hw=none");
        else if (_cfg.HardwareMode == "d3d11") { args.Add("--avcodec-hw=d3d11va"); args.Add("--vout=direct3d11"); }
        else if (_cfg.HardwareMode == "dxva2") args.Add("--avcodec-hw=dxva2");
        else args.Add("--avcodec-hw=any");

        // On Windows the default audio output (WASAPI) gives every player of this program ONE shared volume, so a crossfade (one song
        // fading out while another fades in) cannot work: whichever volume was set last applied to both songs (sudden jumps, crackling,
        // or both songs going silent). DirectSound gives every song its own volume. Used only when crossfade is switched on; if it
        // is not available LibVLC falls back to its normal output ("any").
        _xfIndependentAudio = _cfg.CrossfadeEnabled;
        if (_xfIndependentAudio) args.Add("--aout=directsound,any");

        // Sound space (3D) and volume levelling are LibVLC audio filters, which can only be chosen when LibVLC starts.
        _audioFilterChain = WantedAudioFilterChain();
        if (_audioFilterChain.Length > 0) args.Add("--audio-filter=" + _audioFilterChain);

        var argArray = args.ToArray();
        _libVlc = await Task.Run(() => new LibVLC(argArray));
        _player = BuildPlayer();
        VideoView.MediaPlayer = _player;
        InitCrossfade();
    }

    private async Task StartupFlowAsync()
    {
        // First launch: the logo splash has covered the screen while the player started; let it finish, then the tour.
        await App.FinishSplashAsync();
        if (!_cfg.OnboardingDone) ShowOnboarding();

        PlaylistStore.EnsureLoaded();
        SetMode(_cfg.StartPage switch { "Home" => LibMode.Home, "Blank" => LibMode.Blank, _ => LibMode.Recent });

        // Command line / "Open with": Tempo.exe "C:\path\video.mp4"
        var file = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(MediaTypes.IsSupportedFile);
        if (file != null) PlayOpenedFile(file);

        // from now on, files opened from Explorer arrive here instead of starting another Tempo
        App.ExternalOpenRequested += OnExternalOpen;
        foreach (var pending in App.TakePendingOpens()) OnExternalOpen(pending);
    }

    private DateTime _lastExternalOpen = DateTime.MinValue;

    /// <summary>A file was opened from Explorer while this Tempo was already running (or "" = Tempo was started again).</summary>
    private void OnExternalOpen(string path)
    {
        BringToFront();
        if (path.Length == 0 || _libVlc is null) return;

        // Anonymous Mode never plays outside files: nothing is opened or recorded; the window is just brought forward
        if (_anon) return;

        // Explorer starts one Tempo per selected file: the first one plays, the ones right after it join the queue
        bool burst = (DateTime.UtcNow - _lastExternalOpen).TotalMilliseconds < 1500;
        _lastExternalOpen = DateTime.UtcNow;
        if (burst) Enqueue(path, next: false);
        else PlayOpenedFile(path);
    }

    private void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        bool wasTopmost = Topmost;
        Topmost = true;          // in front of the Explorer window...
        Topmost = wasTopmost;    // ...without staying on top
        Focus();
    }

    private void ShowOnboarding()
    {
        _modalOpen = true;
        try { new OnboardingWindow { Owner = this }.ShowDialog(); }
        finally { _modalOpen = false; }

        ThemeManager.Apply(_cfg.Theme, _cfg.AccentIndex);
        VolumeSlider.Value = Math.Clamp(_cfg.Volume, 0, 200);
    }

    protected override void OnClosed(EventArgs e)
    {
        AppSettings.Changed -= OnSettingsChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        App.ExternalOpenRequested -= OnExternalOpen;
        NativeMethods.KeepAwake(false);
        _waveCts?.Cancel();
        _volumeGuardCts?.Cancel();
        _equalizer?.Dispose();

        _keys.Dispose();
        _anonSettings?.Close();
        LibraryScanner.Shutdown();   // keep what the scan has read so far
        CommitResume();
        Visualizer.Dispose();
        UiAnim.CancelAll();
        _lyricsCts?.Cancel();
        _lyricsEditor?.Close();
        ReleaseShell();
        _vaultStream?.Dispose();
        _vaultService.Lock();

        var player = _player;
        CancelCrossfade();
        try { _discord?.Post(null); _discord?.Dispose(); } catch { }
        var vlc = _libVlc;
        _player = null;
        _libVlc = null;

        base.OnClosed(e);

        // Stop/dispose off the UI thread to avoid LibVLC deadlocks on shutdown.
        _ = Task.Run(() =>
        {
            try { player?.Stop(); player?.Dispose(); vlc?.Dispose(); } catch { /* shutting down */ }
        });
    }

    private string _folderSig = "";

    private string FolderSignature() =>
        string.Join("|", _cfg.MusicFolders) + "#" + string.Join("|", _cfg.VideoFolders) + "#" + LibraryScanner.Version;

    private void OnSettingsChanged()
    {
        if (!_anon) ThemeManager.Apply(_cfg.Theme, _cfg.AccentIndex);
        // A song without a cover wears the accent colour (glow, ripples and the moving background): follow it at once when the accent
        // changes, instead of waiting for the next song or for this one to start again.
        if (!_anon && !_coverShown) ApplyDefaultCoverLook();
        SyncAmbience();
        ApplySoundSettings();
        ApplyVideoAdjust();
        UpdateLoopUi();
        WaveBar.InvalidateVisual();
        SeekBar.InvalidateVisual();
        VideoCorners.InvalidateVisual();
        ApplyViewMode();
        ApplyHideUi();
        ApplyMotion();
        _discordKey = null;
        UpdateDiscord(_player?.IsPlaying == true);   // the Discord settings may have changed
        if (_lyricsOpen) ApplyLyricFocus(_lyricIndex);   // the word-by-word setting may have changed
        ApplyGlobalKeys();
        ApplyVisualizerStyle();

        var sig = FolderSignature();
        if (sig != _folderSig)
        {
            LibraryScanner.Invalidate();
            _folderSig = FolderSignature();
            _musicTracks = new List<Track>();
            if (IsLoaded && _libraryVisible && _mode is LibMode.Home or LibMode.Videos or LibMode.Music) SetMode(_mode);
        }
        else if (IsLoaded && _libraryVisible)
        {
            RebuildRows();
        }
    }

    // ------------------------------------------------------------------ views (library <-> player)

    private void ShowLibrary(bool on)
    {
        _libraryVisible = on;
        if (on) UiAnim.FadeIn(LibraryView, 170);
        else { UiAnim.Cancel(LibraryView); LibraryView.Opacity = 1; LibraryView.Visibility = Visibility.Collapsed; }
        ApplyHideUi();
        if (!ChromeHidden) ControlBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;

        // Expanded rail (icons + labels) in the library, icon-only next to a playing video.
        ApplyRail();
        Rail.Padding = new Thickness(on ? 8 : 4);
        NavRecent.Content = on ? "Recent" : null;
        NavHome.Content = on ? "Home" : null;
        NavVideos.Content = on ? "Videos" : null;
        NavMusic.Content = on ? "Music" : null;
        NavPlaylists.Content = on ? "Playlists" : null;
        NavAdd.Content = on ? "Add item" : null;

        if (on) { ClosePopup(QuickPopup, animate: false); HighlightNav(); }
        else SetNavChecked(null);

        ApplySidebar();

        UpdateMiniPlayer();
        UpdateBatchUi();
    }

    private void NavAdd_Click(object sender, RoutedEventArgs e)
    {
        NavAdd.IsChecked = false;
        var menu = new ContextMenu { PlacementTarget = NavAdd, Placement = PlacementMode.Right };
        if (_anon)
        {
            menu.Items.Add(MenuEntry("Import files…", "IcoImport", ImportFiles_Click, "Ctrl+O"));
            menu.Items.Add(MenuEntry("Import folder…", "IcoFolder", ImportFolder_Click));
            menu.IsOpen = true;
            return;
        }
        menu.Items.Add(MenuEntry("Open file(s)…", "IcoVideo", Open_Click, "Ctrl+O"));
        menu.Items.Add(MenuEntry("Open folder…", "IcoFolder", OpenFolder_Click));
        menu.IsOpen = true;
    }
}
