using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;

namespace EchoDeck;

public partial class MainWindow
{
    // ------------------------------------------------------------------ playback

    /// <summary>Plays a vault item from anywhere in the app (tile, queue, playlist): it joins the queue like a normal file.</summary>
    private void PlayVaultPath(string path, bool stayInLibrary = false)
    {
        var error = StartVault(path, stayInLibrary);
        if (error != null) Notify(error);
    }

    private string? PlayFromSettings(VaultItem item) => StartVault(item.FullPath);

    private string? StartVault(string path, bool stayInLibrary = false)
    {
        _resumePath = null;                   // never remembered in Anonymous Mode
        HideResumePrompt(animate: false);
        if (!_anon || !_anonItems.TryGetValue(path, out var item)) return "That item isn't in the vault any more.";

        var existing = _queue.FirstOrDefault(q => string.Equals(q.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            existing = new QueueItem(path);
            _queue.Add(existing);
        }
        _queueIndex = _queue.IndexOf(existing);

        var error = PlayVaultItem(item);
        if (error != null) return error;

        // "Recent" in Anonymous Mode lives in memory only
        _anonRecent.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
        _anonRecent.Insert(0, path);
        if (_anonRecent.Count > 50) _anonRecent.RemoveRange(50, _anonRecent.Count - 50);

        if (stayInLibrary && _libraryVisible) UpdateMiniPlayer(); else ShowLibrary(false);
        LoadLyricsFor(path, item.IsAudio);
        RefreshSide();
        UpdateTransportEnabled();
        return null;
    }

    /// <summary>Plays an encrypted vault file by decrypting it on the fly into LibVLC. Returns an error message or null.</summary>
    private string? PlayVaultItem(VaultItem item)
    {
        if (!_anon || _libVlc is null || _player is null) return "The player isn't ready.";

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

            StartVolumeGuard();   // the new song must not start at LibVLC's own 100 %
            ApplyStereoMode(_player);

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

            ShowMusic(item.IsAudio, DisplayTitle(item), item.Artist.Length > 0 ? FirstArtistOf(item.Artist) : "");
            MiniTitle.Text = DisplayTitle(item);   // in-app only; the window / taskbar title stays "Tempo"
            if (!item.IsAudio) EmptyHint.Visibility = Visibility.Collapsed;
            UpdateTransportEnabled();

            // The waveform reads through its OWN decrypting stream, so nothing is written anywhere.
            if (item.IsAudio)
            {
                try { LoadWaveform(item.FullPath, _vaultService.OpenRead(item.FullPath)); } catch { /* flat bars */ }
            }
            else
            {
                WaveBar.Peaks = null;
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
            await _vaultService.SecureDeleteAsync(item.FullPath);   // also drops it from the encrypted playlists
            RemoveFromQueue(item.FullPath);
            _anonRecent.RemoveAll(x => string.Equals(x, item.FullPath, StringComparison.OrdinalIgnoreCase));
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
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            try { media?.Dispose(); } catch { /* already released */ }
            try { input?.Dispose(); } catch { /* already released */ }
        });
    }
}
