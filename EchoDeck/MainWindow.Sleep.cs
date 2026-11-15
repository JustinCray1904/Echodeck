using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace EchoDeck;

// Sleep timer: after the chosen time (or at the end of the current song) the sound fades out over a few seconds and playback pauses.
public partial class MainWindow
{
    private DispatcherTimer? _sleepTimer;
    private DateTime _sleepAt;            // when the fade starts
    private bool _sleepAfterSong;         // stop when the current song ends
    private DispatcherTimer? _fadeTimer;
    private DateTime _fadeStart;
    private int _fadeFromVolume;
    private const double FadeSeconds = 8;

    private bool SleepActive => _sleepAfterSong || _sleepAt != default;

    private void ShowSleepMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };

        void Add(string text, bool checkedNow, Action action)
        {
            var item = new MenuItem { Header = text, IsChecked = checkedNow };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        Add("Off", !SleepActive, ClearSleep);
        foreach (int minutes in new[] { 15, 30, 45, 60, 120 })
        {
            int m = minutes;
            string label = m < 60 ? m + " minutes" : m == 60 ? "1 hour" : (m / 60) + " hours";
            Add(label, _sleepAt != default && !_sleepAfterSong && Math.Abs((_sleepAt - DateTime.UtcNow).TotalMinutes - m) < 1, () => StartSleep(m));
        }
        Add("End of this song", _sleepAfterSong, () =>
        {
            ClearSleep();
            _sleepAfterSong = true;
            Notify("Sleep timer: playback stops when this song ends");
        });
        menu.IsOpen = true;
    }

    private void StartSleep(int minutes)
    {
        ClearSleep();
        _sleepAt = DateTime.UtcNow.AddMinutes(minutes);
        _sleepTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _sleepTimer.Tick += (_, _) =>
        {
            if (DateTime.UtcNow >= _sleepAt) BeginFade();
        };
        _sleepTimer.Start();
        Notify("Sleep timer: playback fades out in " + (minutes < 60 ? minutes + " minutes" : minutes == 60 ? "1 hour" : (minutes / 60) + " hours"));
    }

    /// <summary>Cancels a running timer (and a fade that has started; the volume is put back).</summary>
    private void ClearSleep()
    {
        _sleepTimer?.Stop();
        _sleepTimer = null;
        _sleepAt = default;
        _sleepAfterSong = false;
        if (_fadeTimer != null)
        {
            _fadeTimer.Stop();
            _fadeTimer = null;
            if (_player != null) _player.Volume = _fadeFromVolume;
        }
    }

    private void BeginFade()
    {
        _sleepTimer?.Stop();
        _sleepTimer = null;
        _sleepAt = default;
        if (_player is not { IsPlaying: true }) return;   // nothing playing: nothing to fade

        _fadeFromVolume = (int)VolumeSlider.Value;
        _fadeStart = DateTime.UtcNow;
        _fadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _fadeTimer.Tick += (_, _) =>
        {
            if (_player == null) { ClearSleep(); return; }
            double t = (DateTime.UtcNow - _fadeStart).TotalSeconds / FadeSeconds;
            if (t < 1)
            {
                _player.Volume = (int)Math.Round(_fadeFromVolume * (1 - t));
                return;
            }

            _fadeTimer?.Stop();
            _fadeTimer = null;
            if (_player.IsPlaying) _player.SetPause(true);
            _player.Volume = _fadeFromVolume;   // back to normal for when playback is started again
            Notify("Sleep timer: paused");
        };
        _fadeTimer.Start();
    }

    /// <summary>Called when a song ends: true if the sleep timer wants playback to stop there (nothing else is played).</summary>
    private bool SleepStopsAfterSong()
    {
        if (!_sleepAfterSong) return false;
        ClearSleep();
        Notify("Sleep timer: stopped after the song");
        return true;
    }
}
