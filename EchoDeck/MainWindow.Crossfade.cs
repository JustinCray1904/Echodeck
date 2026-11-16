using System.Windows;
using System.Windows.Threading;
using LibVLCSharp.Shared;

namespace EchoDeck;

// Crossfade and gapless playback between two songs of the queue. LibVLC plays one song per player, so the next song is started on a
// second player a few seconds before the end of the current one; the two overlap while the old one fades out and the new one fades in
// (crossfade), or for a moment with no fade (gapless: the next song is already running when the old one ends, so there is no silence).
// The new player then simply becomes THE player: the title, cover, lyrics, waveform and everything else switch to the new song when the
// overlap starts, like other players do. Only audio into audio, never a video, never Anonymous Mode, never "repeat one".
public partial class MainWindow
{
    private DispatcherTimer? _xfWatch;       // looks for the moment to start
    private DispatcherTimer? _xfFadeTimer;   // runs the overlap
    private MediaPlayer? _xfOutgoing;        // the song that is fading out
    private bool _xfPlanned;                 // the next StartPlayback is the incoming song of a crossfade
    private bool _xfActive;                  // a crossfade or gapless overlap is under way
    private bool _xfWaitingForPlaying;       // don't begin the fade until LibVLC confirms the incoming player is playing
    private bool _xfFade;                    // fading (crossfade) or not (gapless)
    private double _xfSeconds;
    private DateTime _xfStart;

    private void InitCrossfade()
    {
        _xfWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _xfWatch.Tick += (_, _) => WatchSongEnd();
        _xfWatch.Start();
    }

    /// <summary>The player of this window, with its events wired. Events from a player that is no longer THE player are ignored.</summary>
    private MediaPlayer BuildPlayer()
    {
        var player = new MediaPlayer(_libVlc!)
        {
            // Let WPF see keyboard/mouse instead of libvlc's native window.
            EnableKeyInput = false,
            EnableMouseInput = false,
        };

        player.Playing += (s, e) =>
        {
            if (!ReferenceEquals(s, _player)) return;
            Player_Playing(s, e);
            Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(s, _player) && _xfActive && _xfWaitingForPlaying)
                    BeginIncomingOverlap();
            });
        };
        player.Paused += (s, _) =>
        {
            if (ReferenceEquals(s, _player))
                Dispatcher.InvokeAsync(() =>
                {
                    if (!ReferenceEquals(s, _player) || _player?.IsPlaying == true) return;
                    SetPlayGlyph(false); NativeMethods.KeepAwake(false); SetPulse(false);
                });
        };
        player.Stopped += (s, _) =>
        {
            if (!ReferenceEquals(s, _player)) return;
            Dispatcher.InvokeAsync(() => { if (ReferenceEquals(s, _player) && _player?.IsPlaying != true) ResetTransport(); });
        };
        player.EndReached += (s, _) =>
        {
            if (ReferenceEquals(s, _player)) Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(s, _player)) OnEndReached();
            });
        };
        player.LengthChanged += (s, e) => { if (ReferenceEquals(s, _player)) Player_LengthChanged(s, e); };
        player.TimeChanged += (s, e) => { if (ReferenceEquals(s, _player)) Player_TimeChanged(s, e); };
        player.EncounteredError += (s, _) =>
        {
            if (ReferenceEquals(s, _player)) Dispatcher.InvokeAsync(() => { if (ReferenceEquals(s, _player)) OnPlaybackError(); });
        };
        return player;
    }

    /// <summary>The song that follows the current one, decided the same way as when a song ends. Null when playback would stop.</summary>
    private string? PeekNextPath()
    {
        if (_queue.Count == 0 || _queueIndex < 0 || _queueIndex >= _queue.Count) return null;

        if (_cfg.Shuffle && _queue.Count > 1)
        {
            int pick;
            do { pick = _rng.Next(_queue.Count); } while (pick == _queueIndex);
            return _queue[pick].FullPath;
        }
        if (!_cfg.AutoAdvance && _cfg.Loop != LoopMode.All) return null;
        if (_queueIndex + 1 < _queue.Count) return _queue[_queueIndex + 1].FullPath;
        if (_cfg.Loop == LoopMode.All) return _queue[0].FullPath;
        return null;
    }

    private void WatchSongEnd()
    {
        if (_xfWaitingForPlaying && DateTime.UtcNow - _xfStart > TimeSpan.FromSeconds(8))
        {
            // A slow or failed incoming source must not leave the old player orphaned indefinitely.
            OnPlaybackError();
            return;
        }
        if (!(_cfg.CrossfadeEnabled || _cfg.GaplessEnabled) || _xfActive || _anon || _libVlc is null) return;
        if (_player is not { IsPlaying: true } || !_currentIsAudio || _lengthMs < 8000) return;
        if (_cfg.Loop == LoopMode.One || _sleepAfterSong) return;

        bool fade = _cfg.CrossfadeEnabled;
        double lead = fade ? Math.Clamp(_cfg.CrossfadeSeconds, 1, 12) : 0.7;   // seconds before the end
        long remaining = _lengthMs - _player.Time;
        if (remaining < 0 || remaining > lead * 1000) return;

        var next = PeekNextPath();
        if (next == null || !File.Exists(next) || !MediaTypes.IsAudio(next)) return;

        _xfActive = true;
        _xfPlanned = true;
        _xfFade = fade;
        _xfSeconds = Math.Max(0.3, Math.Min(lead, remaining / 1000.0));
        PlayNormal(next, stayInLibrary: _libraryVisible);   // reaches StartIncoming() through StartPlayback
        if (_xfPlanned) { _xfActive = false; _xfPlanned = false; _xfWaitingForPlaying = false; }   // playback never got that far
    }

    /// <summary>Called by StartPlayback for the incoming song: it plays on a new player while the old one carries on.</summary>
    private void StartIncoming(Media media)
    {
        _xfPlanned = false;
        var outgoing = _player;
        var incoming = BuildPlayer();
        if (_equalizer != null)
        {
            try { incoming.SetEqualizer(_equalizer); } catch { /* sound without the equaliser rather than no sound */ }
        }

        // Keep the incoming player muted while it opens / buffers. Crossfade raises it gradually;
        // gapless raises it at the outgoing player's natural end.
        incoming.Volume = 0;
        _xfOutgoing = outgoing;
        _player = incoming; // retire the outgoing player's normal event routing before attaching the gapless hand-off
        VideoView.MediaPlayer = incoming;
        if (!_xfFade)
        {
            // Gapless mode must keep the outgoing player alive until its natural end, not stop it after
            // an arbitrary overlap duration. The incoming track is already running underneath it.
            outgoing.EndReached += OutgoingGapless_EndReached;
        }
        _xfWaitingForPlaying = true;
        _xfStart = DateTime.UtcNow; // also bounds a player that never reaches Playing
        incoming.Play(media);
        // Do not start the fade clock here: Play() is asynchronous and opening a slow file can take time.
        // The incoming player's Playing event starts the overlap only after audio is actually ready.
    }

    private void BeginIncomingOverlap()
    {
        if (!_xfActive || !_xfWaitingForPlaying || _player is null || _xfOutgoing is null) return;
        _xfWaitingForPlaying = false;
        _xfStart = DateTime.UtcNow;
        if (_xfFade && _xfOutgoing != null)
        {
            long oldRemaining = _xfOutgoing.Length - _xfOutgoing.Time;
            if (oldRemaining > 0) _xfSeconds = Math.Max(0.3, Math.Min(_xfSeconds, oldRemaining / 1000.0));
        }
        if (!_xfFade)
        {
            // Keep the pre-rolled incoming track muted until the outgoing player's natural EndReached.
            // FinishCrossfadeNow restores its normal volume at that exact hand-off point.
            return;
        }
        _xfFadeTimer?.Stop();
        _xfFadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
        _xfFadeTimer.Tick += (_, _) => FadeStep();
        _xfFadeTimer.Start();
    }

    private void OutgoingGapless_EndReached(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _xfOutgoing) || _xfFade) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (ReferenceEquals(sender, _xfOutgoing) && !_xfFade) FinishCrossfadeNow();
        });
    }

    private void FadeStep()
    {
        var incoming = _player;
        var outgoing = _xfOutgoing;
        if (incoming == null || outgoing == null) { FinishCrossfadeNow(); return; }

        double t = Math.Clamp((DateTime.UtcNow - _xfStart).TotalSeconds / _xfSeconds, 0, 1);
        int volume = (int)VolumeSlider.Value;   // follows the volume slider while it fades
        try
        {
            if (_xfFade)
            {
                // equal-power curves: the loudness stays even in the middle of the crossfade
                incoming.Volume = (int)Math.Round(volume * Math.Sin(t * Math.PI / 2));
                outgoing.Volume = (int)Math.Round(volume * Math.Cos(t * Math.PI / 2));
            }
        }
        catch { /* a player that is going away */ }

        if (t >= 1) FinishCrossfadeNow();
    }

    /// <summary>Ends the overlap at once: the old player goes away, the new one is at the normal volume.</summary>
    private void FinishCrossfadeNow()
    {
        _xfFadeTimer?.Stop();
        _xfFadeTimer = null;

        var outgoing = _xfOutgoing;
        _xfOutgoing = null;
        if (outgoing != null) outgoing.EndReached -= OutgoingGapless_EndReached;
        _xfActive = false;
        _xfPlanned = false;
        _xfWaitingForPlaying = false;

        try { if (_player != null) _player.Volume = (int)VolumeSlider.Value; } catch { }

        if (outgoing != null)
            _ = Task.Run(() =>
            {
                try { outgoing.Stop(); } catch { }
                try { outgoing.Dispose(); } catch { }
            });
    }

    /// <summary>Clean up both players when creating or starting the incoming player throws synchronously.</summary>
    private void AbortCrossfadeStart()
    {
        _xfFadeTimer?.Stop();
        _xfFadeTimer = null;
        var outgoing = _xfOutgoing;
        _xfOutgoing = null;
        if (outgoing != null)
        {
            outgoing.EndReached -= OutgoingGapless_EndReached;
            _ = Task.Run(() =>
            {
                try { outgoing.Stop(); } catch { }
                try { outgoing.Dispose(); } catch { }
            });
        }
        _xfActive = false;
        _xfPlanned = false;
        _xfWaitingForPlaying = false;
    }

    /// <summary>Something else started playing (or the window closes) while a crossfade was running.</summary>
    private void CancelCrossfade()
    {
        if (_xfActive || _xfOutgoing != null) FinishCrossfadeNow();
        _xfPlanned = false;
        _xfWaitingForPlaying = false;
    }
}
