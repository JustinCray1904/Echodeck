using System.Windows;
using System.Windows.Threading;
using LibVLCSharp.Shared;

namespace EchoDeck;

// Crossfade and gapless playback between two songs of the queue. LibVLC plays one song per player, so the next song is started on a
// second player a little before the end of the current one; the two overlap while the old one fades out and the new one fades in
// (crossfade), or for a moment with no fade (gapless: the next song is already running when the old one ends, so there is no silence).
// The new player then simply becomes THE player: the title, cover, lyrics, waveform and everything else switch to the new song when the
// overlap starts, like other players do. Only audio into audio, never a video, never Anonymous Mode, never "repeat one".
//
// IMPORTANT: every player event is matched to its player with the player object captured in BuildPlayer, never with the event's
// `sender`. (An earlier version compared `sender` with `_player`; when LibVLCSharp does not pass the MediaPlayer as the sender every
// event was dropped: the play icon, the seek bar, the arrow keys and repeat all went dead.)
public partial class MainWindow
{
    private DispatcherTimer? _xfWatch;       // looks for the moment to start
    private DispatcherTimer? _xfFadeTimer;   // runs the overlap
    private MediaPlayer? _xfOutgoing;        // the song that is fading out
    private bool _xfPlanned;                 // the next StartPlayback is the incoming song of a crossfade
    private bool _xfActive;                  // a crossfade or gapless overlap is under way
    private bool _xfFade;                    // fading (crossfade) or not (gapless)
    private bool _xfMuted;                   // the old player was muted: the new one is too
    private double _xfSeconds;
    private DateTime _xfStart;
    private bool _xfStarted;                // the incoming song reported "playing": only then does the overlap clock run
    private DateTime _xfAskedAt;             // when the incoming song was asked to play (gives up after 10 s)
    private string? _xfAttemptKey;           // the song an overlap was already tried for (a failed try is not repeated 10 times a second)
    private long _playAskedAt;               // when Play was last asked for (ms), to learn how long this PC takes to start a song
    private double _startLatencyMs = 450;    // learned: Play() until LibVLC reports "playing"

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

        bool IsCurrent() => ReferenceEquals(player, _player);

        player.Playing += (s, e) =>
        {
            if (!IsCurrent()) return;
            NoteStartLatency(Environment.TickCount64);
            Player_Playing(s, e);
        };
        player.Paused += (_, _) =>
        {
            if (IsCurrent())
                Dispatcher.InvokeAsync(() => { SetPlayGlyph(false); NativeMethods.KeepAwake(false); SetPulse(false); });
        };
        player.Stopped += (_, _) => { if (IsCurrent()) Dispatcher.InvokeAsync(ResetTransport); };
        player.EndReached += (_, _) => { if (IsCurrent()) Dispatcher.InvokeAsync(OnEndReached); };
        player.LengthChanged += (s, e) => { if (IsCurrent()) Player_LengthChanged(s, e); };
        player.TimeChanged += (s, e) => { if (IsCurrent()) Player_TimeChanged(s, e); };
        player.EncounteredError += (_, _) => { if (IsCurrent()) Dispatcher.InvokeAsync(OnPlaybackError); };
        return player;
    }

    /// <summary>How long this PC takes from Play() to "playing": the gapless overlap starts that much (plus a margin) before the end.</summary>
    private void NoteStartLatency(long nowMs)
    {
        long asked = _playAskedAt;
        if (asked <= 0) return;
        _playAskedAt = 0;   // only the first "playing" after a Play(): resuming from pause raises the event again
        double ms = nowMs - asked;
        if (ms > 0 && ms < 5000) _startLatencyMs = Math.Clamp(_startLatencyMs * 0.5 + ms * 0.5, 150, 1500);
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
        try
        {
            if (!(_cfg.CrossfadeEnabled || _cfg.GaplessEnabled) || _xfActive || _anon || _transition || _stopping || _libVlc is null) return;
            var player = _player;
            if (player is null || !_hasMedia || !_currentIsAudio || _lengthMs < 8000) return;
            if (_cfg.Loop == LoopMode.One || _sleepAfterSong) return;
            if (_queueIndex < 0 || _queueIndex >= _queue.Count) return;
            if (!player.IsPlaying) return;

            bool fade = _cfg.CrossfadeEnabled;
            double lead = fade
                ? Math.Clamp(_cfg.CrossfadeSeconds, 1, 12)
                : Math.Clamp((_startLatencyMs + 350) / 1000.0, 0.6, 2.0);   // seconds before the end
            long remaining = _lengthMs - player.Time;
            if (remaining < 0 || remaining > (long)(lead * 1000)) return;

            string key = _queueIndex + "|" + _queue[_queueIndex].FullPath;
            if (string.Equals(_xfAttemptKey, key, StringComparison.OrdinalIgnoreCase)) return;   // already tried for this song

            var next = PeekNextPath();
            if (next == null || !File.Exists(next) || !MediaTypes.IsAudio(next)) return;

            _xfAttemptKey = key;
            _xfActive = true;
            _xfPlanned = true;
            _xfFade = fade;
            _xfSeconds = Math.Max(0.3, Math.Min(lead, remaining / 1000.0));
            PlayNormal(next, stayInLibrary: _libraryVisible);   // reaches StartIncoming() through StartPlayback
            if (_xfPlanned) { _xfActive = false; _xfPlanned = false; }   // playback never got that far: nothing is running
        }
        catch (Exception ex)
        {
            _xfActive = false;
            _xfPlanned = false;
            AppLog.Write("Crossfade", ex);
        }
    }

    /// <summary>Called by StartPlayback for the incoming song: it plays on a new player while the old one carries on.</summary>
    private void StartIncoming(Media media)
    {
        _xfPlanned = false;
        var outgoing = _player!;
        var incoming = BuildPlayer();
        if (_equalizer != null)
        {
            try { incoming.SetEqualizer(_equalizer); } catch { /* sound without the equaliser rather than no sound */ }
        }

        int volume = (int)VolumeSlider.Value;
        try { _xfMuted = outgoing.Mute; } catch { _xfMuted = false; }
        try { incoming.Volume = _xfFade ? 0 : volume; } catch { /* set again once the song is playing */ }

        _xfOutgoing = outgoing;
        _player = incoming;
        VideoView.MediaPlayer = incoming;   // the old player must not stay attached to the video view: it is disposed later
        incoming.Play(media);
        if (_xfMuted) { try { incoming.Mute = true; } catch { } }

        _xfStarted = false;                 // the clock starts in NoteIncomingPlaying, not now
        _xfAskedAt = DateTime.UtcNow;
        _xfStart = _xfAskedAt;
        _xfFadeTimer?.Stop();
        _xfFadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _xfFadeTimer.Tick += (_, _) => FadeStep();
        _xfFadeTimer.Start();
    }

    /// <summary>The volumes of the two songs for this moment of the overlap. Also called when the new song reports "playing".</summary>
    private void ApplyCrossfadeVolumes()
    {
        var incoming = _player;
        var outgoing = _xfOutgoing;
        if (incoming == null || outgoing == null) return;

        double t = _xfStarted ? Math.Clamp((DateTime.UtcNow - _xfStart).TotalSeconds / Math.Max(0.1, _xfSeconds), 0, 1) : 0;
        int volume = (int)VolumeSlider.Value;   // follows the volume slider while it fades
        try
        {
            if (_xfFade)
            {
                // equal-power curves: the loudness stays even in the middle of the crossfade
                incoming.Volume = (int)Math.Round(volume * Math.Sin(t * Math.PI / 2));
                outgoing.Volume = (int)Math.Round(volume * Math.Cos(t * Math.PI / 2));
            }
            else
            {
                incoming.Volume = volume;
                outgoing.Volume = volume;
            }
        }
        catch { /* a player that is going away */ }
    }

    private void FadeStep()
    {
        if (_player == null || _xfOutgoing == null) { FinishCrossfadeNow(); return; }

        ApplyCrossfadeVolumes();

        // The new song may still be opening (slow disk, hard file). Until it reports "playing" the old song stays at full volume and
        // is kept; if the new one never starts, the overlap is given up after 10 s.
        if (!_xfStarted)
        {
            if ((DateTime.UtcNow - _xfAskedAt).TotalSeconds > 10) FinishCrossfadeNow();
            return;
        }

        double elapsed = (DateTime.UtcNow - _xfStart).TotalSeconds;
        if (elapsed < _xfSeconds) return;
        if (_xfFade) { FinishCrossfadeNow(); return; }

        // gapless: no fading, so the old song is left to play out its last moments instead of being cut
        bool oldDone;
        try { oldDone = !_xfOutgoing.IsPlaying; } catch { oldDone = true; }
        if (oldDone || elapsed >= _xfSeconds + 1.5) FinishCrossfadeNow();
    }

    /// <summary>The incoming song reports "playing": from now on the overlap clock runs, over what is left of the old song.</summary>
    private void NoteIncomingPlaying()
    {
        if (!_xfActive || _xfStarted) return;
        _xfStarted = true;
        _xfStart = DateTime.UtcNow;
        try
        {
            var outgoing = _xfOutgoing;
            if (outgoing != null && outgoing.Length > 0)
            {
                double left = (outgoing.Length - outgoing.Time) / 1000.0;
                _xfSeconds = Math.Max(0.3, Math.Min(_xfSeconds, left));   // never longer than the old song has left
            }
        }
        catch { /* keep the planned length */ }
    }

    /// <summary>Ends the overlap at once: the old player goes away, the new one is at the normal volume.</summary>
    private void FinishCrossfadeNow()
    {
        _xfFadeTimer?.Stop();
        _xfFadeTimer = null;

        var outgoing = _xfOutgoing;
        _xfOutgoing = null;
        _xfActive = false;
        _xfPlanned = false;
        _xfAttemptKey = null;
        _xfStarted = false;

        try { if (_player != null) _player.Volume = (int)VolumeSlider.Value; } catch { }

        if (outgoing != null)
            _ = Task.Run(() =>
            {
                try { outgoing.Stop(); } catch { }
                try { outgoing.Dispose(); } catch { }
            });
    }

    /// <summary>Something else started playing (or the window closes) while a crossfade was running.</summary>
    private void CancelCrossfade()
    {
        if (_xfActive || _xfOutgoing != null) FinishCrossfadeNow();
        _xfPlanned = false;
    }
}
