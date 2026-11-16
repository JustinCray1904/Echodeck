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

        player.Playing += (s, e) => { if (ReferenceEquals(s, _player)) Player_Playing(s, e); };
        player.Paused += (s, _) =>
        {
            if (ReferenceEquals(s, _player))
                Dispatcher.InvokeAsync(() => { SetPlayGlyph(false); NativeMethods.KeepAwake(false); SetPulse(false); });
        };
        player.Stopped += (s, _) => { if (ReferenceEquals(s, _player)) Dispatcher.InvokeAsync(ResetTransport); };
        player.EndReached += (s, _) => { if (ReferenceEquals(s, _player)) Dispatcher.InvokeAsync(OnEndReached); };
        player.LengthChanged += (s, e) => { if (ReferenceEquals(s, _player)) Player_LengthChanged(s, e); };
        player.TimeChanged += (s, e) => { if (ReferenceEquals(s, _player)) Player_TimeChanged(s, e); };
        player.EncounteredError += (s, _) => { if (ReferenceEquals(s, _player)) Dispatcher.InvokeAsync(OnPlaybackError); };
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
        if (_xfPlanned) { _xfActive = false; _xfPlanned = false; }   // playback never got that far: nothing is running
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

        int volume = (int)VolumeSlider.Value;
        incoming.Volume = _xfFade ? 0 : volume;
        _xfOutgoing = outgoing;
        _player = incoming;
        VideoView.MediaPlayer = incoming;
        incoming.Play(media);

        _xfStart = DateTime.UtcNow;
        _xfFadeTimer?.Stop();
        _xfFadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _xfFadeTimer.Tick += (_, _) => FadeStep();
        _xfFadeTimer.Start();
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
        _xfActive = false;
        _xfPlanned = false;

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
