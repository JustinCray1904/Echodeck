using System.Windows;

namespace EchoDeck;

public partial class MainWindow
{
    // ------------------------------------------------------------------ Audio Mod (speed, pitch, reverb ... on the playing song)

    private string? _modFile;        // the modded copy that is playing now
    private string? _modFor;         // the song it was made from
    private double _modTempo = 1.0;  // how much faster than the original the copy plays (its time x this = the original's time)

    private void QuickMod_Click(object sender, RoutedEventArgs e)
    {
        ClosePopup(QuickPopup, animate: false);
        OpenAudioMod();
    }

    private string? CurrentSongPath() =>
        _queueIndex >= 0 && _queueIndex < _queue.Count ? _queue[_queueIndex].FullPath : null;

    private void OpenAudioMod()
    {
        if (_modalOpen) return;

        if (_anon)
        {
            TempoDialog.Show(this,
                "Audio Mod isn't available in Anonymous Mode: it makes a temporary copy of the song on your disk, and Anonymous Mode never writes anything readable.",
                AppName, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string? song = CurrentSongPath();
        if (song == null || !_hasMedia || !File.Exists(song) || !MediaTypes.IsAudio(song))
        {
            TempoDialog.Show(this, "Audio Mod works on music files. Play a song, then open it again.",
                AppName, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _modalOpen = true;
        try
        {
            var window = new AudioModWindow(
                DisplayNames.Clean(Path.GetFileNameWithoutExtension(song)),
                AudioModSettings.FromJson(_cfg.AudioModJson),
                (settings, progress, ct) => ApplyAudioModAsync(song, settings, progress, ct),
                () => RevertAudioMod(song),
                () => _modFile != null && string.Equals(_modFor, song, StringComparison.OrdinalIgnoreCase),
                settings =>
                {
                    _cfg.AudioModJson = settings.ToJson();
                    _cfg.Save(notify: false);
                })
            { Owner = this };
            window.ShowDialog();
        }
        finally { _modalOpen = false; }
    }

    /// <summary>
    /// Makes the modded copy of <paramref name="song"/> and plays it from the same moment of the song. The queue, the title and the cover
    /// stay those of the original: only the sound being played is another file.
    /// </summary>
    private async Task<bool> ApplyAudioModAsync(string song, AudioModSettings settings, IProgress<double> progress, CancellationToken ct)
    {
        if (_player is null) return false;

        if (settings.IsNeutral)
        {
            RevertAudioMod(song);   // nothing to make: back to the original (if a modded copy was playing)
            return true;
        }

        string file = await AudioModRenderer.RenderAsync(song, settings, progress, ct);
        ct.ThrowIfCancellationRequested();

        // the song may have been changed while the copy was being made
        if (_player is null || !string.Equals(CurrentSongPath(), song, StringComparison.OrdinalIgnoreCase)) return false;

        long originalMs = (long)(Math.Max(0, _player.Time) * _modTempo);   // where the listener is, in the ORIGINAL song's time
        double tempo = settings.Clamp().SpeedPercent / 100.0;

        _modFile = file;
        _modFor = song;
        _modTempo = tempo;
        StartPlayback(file, (long)(originalMs / tempo), isMrl: false);
        var name = AudioModPresets.Match(settings)?.Name ?? "custom";
        ShowOsd("Audio Mod: " + name);
        return true;
    }

    /// <summary>Plays the original again from the same moment. Returns false when no modded copy was playing.</summary>
    private bool RevertAudioMod(string song)
    {
        if (_modFile == null || _player is null) return false;

        long originalMs = (long)(Math.Max(0, _player.Time) * _modTempo);
        _modFile = null;
        _modFor = null;
        _modTempo = 1.0;
        if (string.Equals(CurrentSongPath(), song, StringComparison.OrdinalIgnoreCase) && File.Exists(song))
            StartPlayback(song, originalMs, isMrl: false);
        return true;
    }
}
