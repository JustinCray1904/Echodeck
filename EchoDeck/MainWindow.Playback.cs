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
    // ------------------------------------------------------------------ playback

    /// <param name="stayInLibrary">Queue / playlist moves (next, previous, auto-advance) keep the mini player instead of opening the full player.</param>
    private void PlayNormal(string path, bool stayInLibrary = false)
    {
        if (_libVlc is null) return;
        if (_anon) { PlayVaultPath(path, stayInLibrary); return; }

        if (!File.Exists(path))
        {
            Notify("“" + Path.GetFileName(path) + "” isn't there any more. It may have been moved, renamed or deleted.");
            return;
        }

        StopResumeTracking();   // remember where the previous video was left

        var existing = _queue.FirstOrDefault(q => string.Equals(q.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            existing = new QueueItem(path);
            _queue.Add(existing);
        }
        _queueIndex = _queue.IndexOf(existing);
        if (_cfg.RememberRecent) RecentStore.Add(path);

        var name = DisplayNames.CleanFileName(Path.GetFileName(path));
        _normalTitle = name;
        SetTitles(name);

        bool audio = MediaTypes.IsAudio(path);
        ShowMusic(audio, DisplayNames.Clean(Path.GetFileNameWithoutExtension(path)), Path.GetFileName(Path.GetDirectoryName(path) ?? ""));
        ApplyMotion();   // the moving-background setting always applies to the song that starts
        if (stayInLibrary && _libraryVisible) UpdateMiniPlayer(); else ShowLibrary(false);
        StartPlayback(path, 0, isMrl: false);

        // while a crossfade / gapless overlap is opening, the heavy parts wait until the new song is really playing (no crackling)
        if (_xfActive) { ClearLyrics(); WaveBar.Peaks = null; }
        RunWhenQuiet(path, () => LoadLyricsFor(path, audio));
        BeginResumeTracking(path, audio);
        MaybeOfferResume(path, audio, inMini: stayInLibrary && _libraryVisible);

        if (audio) RunWhenQuiet(path, () => LoadWaveform(path, null));
        else WaveBar.Peaks = null;
        RunWhenQuiet(path, () => LoadCover(path));
        RefreshSide();
        UpdateTransportEnabled();
    }

    private void SetTitles(string name)
    {
        TitleText.Text = name;
        Title = AppName;   // the taskbar preview and Alt+Tab show just "Tempo", never what is playing (the title bar above the video still shows it)
        MiniTitle.Text = name == AppName ? "" : name;
    }

    private void StartPlayback(string pathOrMrl, long startMs, bool isMrl)
    {
        if (_libVlc is null || _player is null) return;
        if (!string.Equals(pathOrMrl, _modFile, StringComparison.OrdinalIgnoreCase))
        {
            _modFile = null;   // anything but the modded copy itself starts the original, and the mod is over
            _modFor = null;
            _modTempo = 1.0;
        }
        if (_playback.Park(pathOrMrl, startMs, isMrl)) return;   // a Stop is running: this request waits for it (PlaybackCoordinator)

        try
        {
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
            if (_xfPlanned && !isMrl) media.AddOption(":file-caching=1500");   // a bigger read-ahead: the next song opens without stalling the audio

            _hasMedia = true;
            _firstPlay = true;
            _lengthMs = 0;
            SeekBar.Progress = 0;
            WaveBar.Progress = 0;
            UpdateTransportEnabled();

            _playAskedAt = Environment.TickCount64;   // learns how long a song takes to start (gapless lead time)
            if (_xfPlanned) StartIncoming(media);   // crossfade / gapless: the old song carries on while this one starts
            else
            {
                _xfAttemptKey = null;
                CancelCrossfade();                  // anything else that starts ends a crossfade that was still running
                _player.Play(media);
                StartVolumeGuard();                 // the new song must not start at LibVLC's own 100 %
                ApplyStereoMode(_player);           // and keeps the chosen channel mode (mono, left only ...)
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("Start playback", ex);
            _xfPlanned = false;
            CancelCrossfade();
            _hasMedia = false;
            UpdateTransportEnabled();
            Notify("Couldn't start playback: " + ex.Message);
        }
    }

    private long _lastScrubSeek;
    private bool _playbackEnded;   // the song / video played to its end and nothing followed: Play must start it again

    /// <summary>Plays the current item from the start again (LibVLC does not restart a finished item with a plain Play).</summary>
    private async void RestartCurrent()
    {
        var player = _player;
        if (player is null) return;
        try
        {
            if (!_anon && _queueIndex >= 0 && _queueIndex < _queue.Count)
            {
                PlayNormal(_queue[_queueIndex].FullPath, stayInLibrary: _libraryVisible);
                return;
            }
            await Task.Run(() => player.Stop());
            player.Play();
        }
        catch (Exception ex)
        {
            AppLog.Write("Restart", ex);
        }
    }

    // ------------------------------------------------------------------ start-up volume

    private CancellationTokenSource? _volumeGuardCts;
    private bool _coverShown;   // the cover's own colours are on screen (otherwise the accent's)
    private int _volumeTarget = 100;   // the slider's value, readable from any thread (the guard below runs off the UI thread)

    /// <summary>
    /// LibVLC 3 only accepts a volume once the song's audio output exists, and it creates a new output for every song, which starts
    /// at LibVLC's own 100 %. Setting the volume when the "playing" event reaches the UI thread was therefore too late: for as long as
    /// the UI thread was busy (cover, waveform, lyrics, thumbnails) the song played at full volume. This watches the new player
    /// from a background thread (which does not depend on the UI thread at all) and puts the slider's volume in place the moment
    /// the output accepts it, and keeps it there during the first seconds of the song.
    /// </summary>
    private void StartVolumeGuard()
    {
        var player = _player;
        if (player is null || _xfActive || _fadeTimer != null) return;   // a crossfade / sleep fade sets the volume itself

        _volumeGuardCts?.Cancel();
        var cts = _volumeGuardCts = new CancellationTokenSource();
        Volatile.Write(ref _volumeTarget, (int)VolumeSlider.Value);
        var token = cts.Token;

        _ = Task.Run(() =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!token.IsCancellationRequested && clock.ElapsedMilliseconds < 3500)
            {
                try
                {
                    if (!ReferenceEquals(player, _player)) return;   // another player took over (crossfade) or the window is closing
                    int want = Volatile.Read(ref _volumeTarget);
                    if (player.Volume != want) player.Volume = want;   // refused (and read back as -1) until the audio output exists
                }
                catch { return; }   // the player is going away
                Thread.Sleep(8);
            }
        });
    }

    // ------------------------------------------------------------------ sound mode (Settings > Audio)

    private CancellationTokenSource? _stereoGuardCts;
    private static readonly int[] StereoRetryMs = { 0, 120, 300, 600, 1000, 1600, 2400, 3500, 5000 };
    private int _stereoCodeSeen;
    private string _audioFilterChain = "";        // the --audio-filter chain this LibVLC instance was started with
    private string _audioRestartNotedFor = "";

    /// <summary>The audio filters the settings ask for ("normvol:stereo_widen:spatializer"), in LibVLC's chain syntax.</summary>
    private string WantedAudioFilterChain() => SoundFilters.Chain(_cfg.NormalizeVolume, _cfg.SoundSpace);

    /// <summary>
    /// Puts the chosen channel mode on a playing song. Every song gets a new audio output that starts in normal mode, so (like the
    /// start-up volume) this watches from a background thread and applies the mode the moment the output accepts it.
    /// <paramref name="report"/>: the user just changed the mode, so say so if the audio device does not take it.
    /// </summary>
    private void ApplyStereoMode(MediaPlayer? player, bool report = false)
    {
        if (player is null) return;
        int code = StereoModes.CodeFor(_cfg.StereoMode);
        _stereoCodeSeen = code;   // what is on the playing song now, so a later change back to Normal is noticed
        if (code == 0 && !report) return;   // a new song already starts in normal mode

        // Going back to Normal in the middle of a song: plain stereo (1), a value LibVLC always accepts ("unset", 0, may be refused)
        int send = code == 0 ? 1 : code;

        _stereoGuardCts?.Cancel();
        var cts = _stereoGuardCts = new CancellationTokenSource();
        var token = cts.Token;

        _ = Task.Run(() =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            bool applied = false;
            foreach (int at in StereoRetryMs)
            {
                while (clock.ElapsedMilliseconds < at)
                {
                    if (token.IsCancellationRequested) return;
                    Thread.Sleep(10);
                }
                if (token.IsCancellationRequested) return;

                try
                {
                    if (!ReferenceEquals(player, _player)) return;
                    if (StereoModes.TrySet(player, send)) applied = true;
                }
                catch { return; }

                // The user just changed the mode in the middle of a song: the audio output exists, one success is enough.
                // At the start of a song it is NOT: the previous song's output can still be alive for a moment and accepts the
                // mode, then the new song gets a fresh output in normal stereo. So the mode is put on again a few times.
                if (applied && report) break;
            }

            if (!applied && report && !token.IsCancellationRequested)
            {
                bool playing;
                try { playing = player.IsPlaying; } catch { return; }
                if (playing) Dispatcher.InvokeAsync(() => Notify("Your audio device doesn't support that sound mode."));
            }
        });
    }

    /// <summary>The sound settings changed: the channel mode applies at once, the filters need a restart (said once per change).</summary>
    private void ApplySoundSettings()
    {
        int code = StereoModes.CodeFor(_cfg.StereoMode);
        if (code != _stereoCodeSeen)
        {
            _stereoCodeSeen = code;
            ApplyStereoMode(_player, report: true);
        }

        string wanted = WantedAudioFilterChain();
        if (wanted != _audioFilterChain && wanted != _audioRestartNotedFor)
        {
            _audioRestartNotedFor = wanted;
            Notify("Restart Tempo to apply the sound space / volume levelling.");
        }
    }

    private void TogglePlayPause()
    {
        if (_player is null || _playback.IsStopping) return;
        if (_xfActive) FinishCrossfadeNow();   // pausing in the middle of a crossfade: the old song is let go first

        if (_playbackEnded && _hasMedia)
        {
            _playbackEnded = false;
            RestartCurrent();
            return;
        }

        if (!_hasMedia)
        {
            // Nothing loaded: start the first queued item (never in Anonymous Mode, which has its own list).
            if (_queue.Count > 0) PlayNormal(_queue[Math.Max(0, _queueIndex)].FullPath);
            return;
        }
        if (_player.IsPlaying) _player.Pause(); else _player.Play();
    }

    // Player events arrive on LibVLC threads: marshal to the UI thread, never call back into the player inline.
    private void Player_Playing(object? sender, EventArgs e) => Dispatcher.InvokeAsync(() =>
    {
        if (_player is null) return;
        _errorSkips = 0;
        _playbackEnded = false;
        SetPlayGlyph(true);
        SetPulse(_currentIsAudio);
        EmptyHint.Visibility = Visibility.Collapsed;
        if (_xfActive)
        {
            NoteIncomingPlaying();
            ApplyCrossfadeVolumes();   // during an overlap the fade sets the volume
            if (_xfMuted) { try { _player.Mute = true; } catch { } }
        }
        else _player.Volume = (int)VolumeSlider.Value;
        ApplyStereoMode(_player);   // the chosen channel mode again, now that this song's audio output exists (no-op in Normal)
        if (_cropIndex > 0) TrySetCrop(CropValues[_cropIndex]);   // a new video starts uncropped: bring the chosen crop back
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
            if (_cfg.StartFullscreen && !_isFullscreen && !_currentIsAudio && !_anon) ToggleFullscreen();
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
            UpdateLyrics(t);
            TrackResume(t);
            UpdateTaskbarProgress(t);
            if (_lengthMs > 0) Visualizer.SetPosition((double)t / _lengthMs, _lengthMs / 1000.0);
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
        UpdateDiscord(playing);
        var data = (System.Windows.Media.Geometry)FindResource(playing ? "IcoPause" : "IcoPlay");
        PlayPauseIcon.Data = data;
        MiniPlayIcon.Data = data;
        MiniOvPlayIcon.Data = data;
        SyncPlayingState(playing);
    }

    private static string Fmt(long ms) => TimeFormat.Clock(ms);

    private void SeekBy(int seconds)
    {
        if (_player is null || !_hasMedia || _lengthMs <= 0) return;
        if (_xfActive) FinishCrossfadeNow();
        long target = Math.Clamp(_player.Time + seconds * 1000L, 0, _lengthMs);
        _player.Time = target;
        OnSeeked(target);
        ShowOsd((seconds > 0 ? "+" : "") + seconds + " s");
    }

    private void ChangeVolume(int delta)
    {
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + delta, 0, 200);   // up to 200 % like VLC (LibVLC accepts 0-200)
        ShowOsd("Volume " + (int)VolumeSlider.Value + "%", VolumeSlider.Value / 200.0);
    }

    private void ToggleMute()
    {
        if (_player is null) return;
        _player.Mute = !_player.Mute;
        UpdateVolumeGlyphs(animate: true);
        ShowOsd(_player.Mute ? "Muted" : "Volume " + (int)VolumeSlider.Value + "%", _player.Mute ? 0.0 : VolumeSlider.Value / 200.0);
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
        PrevButton.IsEnabled = _queue.Count > 1;
        NextButton.IsEnabled = _queue.Count > 1;
        PlayPauseButton.IsEnabled = has || _queue.Count > 0;
        UpdateMiniPlayer();
    }

    private void UpdateMiniPlayer()
    {
        bool show = _libraryVisible && _hasMedia;
        if (show) UiAnim.FadeIn(MiniPlayer, 200); else { UiAnim.Cancel(MiniPlayer); MiniPlayer.Visibility = Visibility.Collapsed; MiniPlayer.Opacity = 1; }

        // the cards keep their full width: the small player just floats above them at its docked side
        var listMargin = LibraryList.Margin;
        if (listMargin.Right != 0) LibraryList.Margin = new Thickness(listMargin.Left, listMargin.Top, 0, listMargin.Bottom);
        var emptyMargin = LibEmptyPanel.Margin;
        if (emptyMargin.Right != 0) LibEmptyPanel.Margin = new Thickness(emptyMargin.Left, emptyMargin.Top, 0, emptyMargin.Bottom);

        ApplyPlayerLayout();
        if (show) Dispatcher.InvokeAsync(() => { PositionMini(animate: false); PositionMiniVideo(); }, DispatcherPriority.Loaded);
    }

    private void MiniExpand_Click(object sender, RoutedEventArgs e) => ShowLibrary(false);
}
