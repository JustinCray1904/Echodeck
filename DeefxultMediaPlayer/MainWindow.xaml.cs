using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;

namespace DeefxultMediaPlayer;

public partial class MainWindow : Window
{
    private const string BaseTitle = "Deefxult Media Player";

    // Playback
    private LibVLC? _libVlc;
    private MediaPlayer? _player;
    private bool _hasMedia;
    private long _lengthMs;
    private long? _pendingSeekMs;
    private bool _isSeeking;
    private string _normalTitle = BaseTitle;

    // Stealth / vault state
    private readonly IKeyInputSource _keys;
    private readonly PinDetector _pinDetector = new(PinStore.Verify);
    private readonly VaultService _vaultService = new();
    private VaultOverlayWindow? _vault;
    private PlaybackSnapshot? _snapshot;

    // In-app PIN change (typed digits are captured here; the overlay never needs keyboard focus)
    private enum PinEntry { None, EnterNew, ConfirmNew }
    private PinEntry _pinEntry = PinEntry.None;
    private readonly StringBuilder _pinInput = new();
    private string? _newPin;

    // The vault video currently feeding LibVLC (decrypted on the fly, never written to disk)
    private EncryptedVaultStream? _vaultStream;
    private StreamMediaInput? _vaultInput;
    private Media? _vaultMedia;
    private string? _vaultPath;

    private bool _modalOpen; // a WPF dialog is open: hotkeys/PIN trigger are ignored

    // Fullscreen
    private bool _isFullscreen;
    private WindowState _prevState;
    private WindowStyle _prevStyle;
    private ResizeMode _prevResize;

    public MainWindow()
    {
        InitializeComponent();

#if GLOBAL_KEY_HOOK
        _keys = new GlobalKeyboardHook(Dispatcher);
#else
        _keys = new WpfKeyInput(Dispatcher);
#endif

        LocationChanged += (_, _) => SchedulePositionVault();
        SizeChanged += (_, _) => SchedulePositionVault();
        StateChanged += (_, _) => SchedulePositionVault();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeMethods.ApplyDarkTitleBar(new WindowInteropHelper(this).Handle);
    }

    // ------------------------------------------------------------------ startup / shutdown

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _libVlc = new LibVLC("--no-video-title-show");
        _player = new MediaPlayer(_libVlc)
        {
            // Let WPF see keyboard/mouse instead of libvlc's native window.
            EnableKeyInput = false,
            EnableMouseInput = false
        };

        _player.Playing += Player_Playing;
        _player.Paused += (_, _) => Dispatcher.InvokeAsync(() => SetPlayGlyph(false));
        _player.Stopped += (_, _) => Dispatcher.InvokeAsync(ResetTransport);
        _player.EndReached += (_, _) => Dispatcher.InvokeAsync(ResetTransport);
        _player.LengthChanged += Player_LengthChanged;
        _player.TimeChanged += Player_TimeChanged;

        VideoView.MediaPlayer = _player;

        _keys.KeyPressed += OnKeyInput;
        _keys.CtrlReleased += _pinDetector.Reset;
        if (!_keys.Start())
        {
            MessageBox.Show(this, "Keyboard input could not be initialised; Ctrl+PIN and Esc won't work.",
                BaseTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // The overlay must never float above other applications: hide it while the app is in the background.
        Application.Current.Activated += App_Activated;
        Application.Current.Deactivated += App_Deactivated;

        // Command-line: DeefxultMediaPlayer.exe "C:\path\video.mp4"
        var file = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(MediaTypes.IsSupportedFile);
        if (file != null)
            Dispatcher.InvokeAsync(() => PlayNormal(file), DispatcherPriority.ApplicationIdle);

        // First launch: let the user choose their own PIN instead of keeping the default.
        Dispatcher.InvokeAsync(ShowFirstRunPinSetup, DispatcherPriority.ApplicationIdle);
    }

    private void ShowFirstRunPinSetup()
    {
        if (!PinStore.IsDefault || PinStore.SetupSkipped) return;

        _modalOpen = true;
        try { new PinSetupWindow { Owner = this }.ShowDialog(); }
        finally { _modalOpen = false; }
    }

    protected override void OnClosed(EventArgs e)
    {
        Application.Current.Activated -= App_Activated;
        Application.Current.Deactivated -= App_Deactivated;

        _keys.Dispose();
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
        if (_vault == null) return;
        _vault.Show();
        PositionVault();
    }

    // ------------------------------------------------------------------ playback

    private void PlayNormal(string path)
    {
        if (_vault != null) return; // don't disturb the paused normal video while the vault is open

        _normalTitle = $"{Path.GetFileName(path)} - {BaseTitle}";
        Title = _normalTitle;
        StartPlayback(path, 0, isMrl: false);
    }

    private void StartPlayback(string pathOrMrl, long startMs, bool isMrl)
    {
        if (_libVlc is null || _player is null) return;

        using var media = new Media(_libVlc, pathOrMrl, isMrl ? FromType.FromLocation : FromType.FromPath);
        if (startMs > 0)
        {
            // start-time makes libvlc open at the position; _pendingSeekMs then sets
            // mediaPlayer.Time = savedTime once playback has begun, to land on the exact millisecond.
            media.AddOption(":start-time=" + (startMs / 1000.0).ToString("0.000", CultureInfo.InvariantCulture));
            _pendingSeekMs = startMs;
        }
        else
        {
            _pendingSeekMs = null;
        }

        _hasMedia = true;
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

        if (_pendingSeekMs is long t)
        {
            _pendingSeekMs = null;
            _player.Time = t;
        }
    });

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
        TimeText.Text = "0:00";
        if (!_isSeeking) SeekSlider.Value = 0;
    }

    private void SetPlayGlyph(bool playing) => PlayPauseButton.Content = playing ? "\uE769" : "\uE768";

    private static string Fmt(long ms)
    {
        var ts = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
    }

    // ------------------------------------------------------------------ UI handlers

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_vault != null) return;
        var dlg = new OpenFileDialog { Title = "Open video", Filter = MediaTypes.DialogFilter };
        if (dlg.ShowDialog(this) == true) PlayNormal(dlg.FileName);
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayPause();

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            var f = files.FirstOrDefault(MediaTypes.IsSupportedFile);
            if (f != null) PlayNormal(f);
        }
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
            ControlBar.Visibility = Visibility.Collapsed;
            _isFullscreen = true;
        }
        else
        {
            WindowStyle = _prevStyle;
            ResizeMode = _prevResize;
            WindowState = _prevState;
            ControlBar.Visibility = Visibility.Visible;
            _isFullscreen = false;
        }
        SchedulePositionVault();
    }

    // ------------------------------------------------------------------ keyboard (stealth trigger + panic)

    private void OnKeyInput(int vk, bool ctrl, bool appActive)
    {
        if (_modalOpen) return; // typing in the PIN setup dialog must not trigger hotkeys

        // Esc is the panic button. (With the optional global hook it also works while another window
        // is in front; with the default WPF input it works whenever this app is active.)
        if (vk == NativeMethods.VK_ESCAPE)
        {
            if (_vault != null) ExitVault();
            else if (appActive && _isFullscreen) ToggleFullscreen();
            return;
        }

        if (!appActive)
        {
            _pinDetector.Reset();
            return;
        }

        // While changing the PIN, digits go to the PIN-entry flow instead of the trigger/hotkeys.
        if (_vault != null && _pinEntry != PinEntry.None)
        {
            HandlePinEntryKey(vk);
            return;
        }

        // Ctrl + 4-digit PIN
        if (ctrl && PinDetector.TryGetDigit(vk, out char digit))
        {
            if (_vault == null && _pinDetector.Push(digit)) EnterVault();
            return;
        }

        // Modifier presses shouldn't break a PIN in progress.
        if (vk is NativeMethods.VK_CONTROL or NativeMethods.VK_LCONTROL or NativeMethods.VK_RCONTROL
            or 0x10 or 0xA0 or 0xA1 or 0x12 or 0xA4 or 0xA5)
            return;

        _pinDetector.Reset();

        if (ctrl && vk == NativeMethods.VK_O) { Open_Click(this, new RoutedEventArgs()); return; }
        if (!ctrl && vk == NativeMethods.VK_SPACE) { TogglePlayPause(); return; }
        if (vk == NativeMethods.VK_F11) ToggleFullscreen();
    }

    // ------------------------------------------------------------------ vault

    private void EnterVault()
    {
        if (_vault != null || _player is null) return;

        // 1) Freeze the normal video and remember exactly where it was.
        string? mrl = null;
        if (_hasMedia)
        {
            using var media = _player.Media;
            mrl = media?.Mrl;
        }
        if (_player.IsPlaying) _player.SetPause(true);
        _snapshot = mrl != null ? new PlaybackSnapshot(mrl, _player.Time) : null;

        // 2) Load the master key into memory (DPAPI) for as long as the vault is open.
        string? unlockError = null;
        try { _vaultService.Unlock(); }
        catch (Exception ex) { unlockError = ex.Message; }

        // 3) Spawn the hover-pill docked to the top-left of the video area.
        _vault = new VaultOverlayWindow(_vaultService, unlockError)
        {
            Owner = this,
            PlayHandler = PlayVaultItem,
            DeleteHandler = DeleteVaultItemAsync
        };
        _vault.EscapePressed += ExitVault;
        _vault.ChangePinRequested += BeginPinChange;
        PositionVault();
        _vault.Show();
        PositionVault();

        if (unlockError == null && PinStore.IsDefault)
            _vault.SetStatus("Default PIN in use. Tap the gear to change it.");
    }

    // ------------------------------------------------------------------ PIN change

    private void BeginPinChange()
    {
        if (_vault == null) return;
        _pinEntry = PinEntry.EnterNew;
        _pinInput.Clear();
        _newPin = null;
        _vault.SetStatus("New PIN: type 4 digits (Backspace clears)");
    }

    private void HandlePinEntryKey(int vk)
    {
        if (_vault == null) return;

        if (vk == 0x08) // Backspace
        {
            _pinInput.Clear();
            _vault.SetStatus(PinPrompt());
            return;
        }

        if (!PinDetector.TryGetDigit(vk, out char digit)) return;
        if (_pinInput.Length < PinDetector.PinLength) _pinInput.Append(digit);

        if (_pinInput.Length < PinDetector.PinLength)
        {
            _vault.SetStatus(PinPrompt());
            return;
        }

        if (_pinEntry == PinEntry.EnterNew)
        {
            _newPin = _pinInput.ToString();
            _pinInput.Clear();
            _pinEntry = PinEntry.ConfirmNew;
            _vault.SetStatus(PinPrompt());
            return;
        }

        // ConfirmNew
        bool match = _pinInput.ToString() == _newPin;
        string result;
        if (match)
        {
            try { PinStore.Set(_newPin!); result = "PIN updated. Use Ctrl + new PIN next time."; }
            catch (Exception ex) { result = "Couldn't save PIN: " + ex.Message; }
        }
        else
        {
            result = "PINs didn't match. Tap the gear to try again.";
        }
        CancelPinEntry();
        _vault.SetStatus(result);
    }

    private string PinPrompt()
    {
        string dots = new string('\u2022', _pinInput.Length);
        return _pinEntry == PinEntry.ConfirmNew ? "Confirm new PIN: " + dots : "New PIN: " + dots;
    }

    private void CancelPinEntry()
    {
        _pinEntry = PinEntry.None;
        _pinInput.Clear();
        _newPin = null;
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
            if (!_player.Play(media))
            {
                stream.Dispose();
                media.Dispose();
                return "LibVLC couldn't start playback.";
            }

            // Swap in the new vault video, then release the previous one.
            var (oldStream, oldInput, oldMedia) = (_vaultStream, _vaultInput, _vaultMedia);
            _vaultStream = stream;
            _vaultInput = input;
            _vaultMedia = media;
            _vaultPath = item.FullPath;
            _hasMedia = true;

            RetireVault(oldStream, oldInput, oldMedia);
            return null; // title bar deliberately keeps showing the normal video's name
        }
        catch (Exception ex)
        {
            stream?.Dispose();
            return "Can't play: " + ex.Message;
        }
    }

    /// <summary>
    /// Permanently deletes a vault file. If it's the video LibVLC is reading, playback is stopped and the
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
            RetireCurrentVault();                // closes the file handle immediately
            ResetTransport();
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

    /// <summary>Panic exit: destroy the overlay, wipe vault state, restore the normal video exactly.</summary>
    private void ExitVault()
    {
        var vault = _vault;
        if (vault == null || _player is null) return;
        _vault = null;
        CancelPinEntry();

        vault.Wipe();
        vault.Close();

        var snapshot = _snapshot;
        _snapshot = null;

        if (snapshot != null)
        {
            // Reload the saved path, resume at the saved timestamp, un-pause.
            StartPlayback(snapshot.Mrl, snapshot.TimeMs, isMrl: true);
            RetireCurrentVault();
        }
        else if (_hasMedia)
        {
            // Nothing was playing before the vault opened: just stop the vault video.
            _hasMedia = false;
            var player = _player;
            Task.Run(() => player.Stop()).ContinueWith(_ => Dispatcher.InvokeAsync(RetireCurrentVault));
            EmptyHint.Visibility = Visibility.Visible;
        }
        else
        {
            RetireCurrentVault();
        }

        _vaultService.Lock(); // zero the master key in memory
        Title = _normalTitle;
    }

    private void SchedulePositionVault() => Dispatcher.InvokeAsync(PositionVault, DispatcherPriority.Loaded);

    private void PositionVault()
    {
        if (_vault == null || !IsLoaded || WindowState == WindowState.Minimized) return;

        var source = PresentationSource.FromVisual(VideoHost);
        if (source?.CompositionTarget == null) return;

        var devicePoint = VideoHost.PointToScreen(new Point(0, 0));
        var dip = source.CompositionTarget.TransformFromDevice.Transform(devicePoint);
        _vault.Left = dip.X + 12;
        _vault.Top = dip.Y + 12;
    }
}
