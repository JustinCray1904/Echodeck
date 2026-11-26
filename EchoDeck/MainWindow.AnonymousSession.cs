using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;

namespace EchoDeck;

public partial class MainWindow
{
    // ------------------------------------------------------------------ entering / leaving

    /// <summary>
    /// Checks the vault key first, then plays the "going dark" splash and, while the screen is fully black, stops the
    /// normal media, sets the normal queue / pages aside and swaps the whole UI to the Anonymous theme with the vault's content.
    /// </summary>
    private async void EnterAnonymous()
    {
        if (_anon || _player is null || _transition || !PinStore.IsSet) return;
        _transition = true;

        AnonymousSplashWindow? splash = null;
        bool pausedByUs = false;
        try
        {
            // 0) Make sure the vault key can be loaded BEFORE anything changes on screen (quick).
            if (!TryUnlockVault()) return;

            // Reading the vault (every file's header has to be opened and decrypted) is the slow part, and used to run BEFORE the splash,
            // so the passcode seemed to do nothing for a while. It now runs in the background while the splash is already playing.
            var listTask = Task.Run(async () =>
            {
                await _vaultService.MigrateLegacyAsync();
                return await _vaultService.ListAsync();
            });

            // 1) Freeze the normal media and remember exactly where it was.
            StopResumeTracking();   // the video's spot is saved for "continue where you left off"
            string? mrl = null;
            if (_hasMedia)
            {
                using var media = _player.Media;
                mrl = media?.Mrl;
            }
            if (_player.IsPlaying) { _player.SetPause(true); pausedByUs = true; }
            _snapshot = mrl != null ? new PlaybackSnapshot(mrl, _player.Time, _currentIsAudio, _normalTitle) : null;

            // 2) Going dark.
            splash = new AnonymousSplashWindow(WindowHelper.GetScreenBoundsDip(this), entering: true);
            splash.Show();
            await splash.PlayDarkenAsync();
            var items = await listTask;   // normally long finished by now

            // 3) Under cover of the black screen: STOP the normal media (it must not keep sounding or be resumable
            //    from the Anonymous controls), set the normal state aside, switch every page over to the vault.
            var player = _player;
            CancelCrossfade();   // an outgoing crossfade player must not keep sounding either
            await Task.Run(() => player.Stop());
            _hasMedia = false;
            _lengthMs = 0;
            ResetTransport();

            _modeStash = _mode;
            _queueStash = new List<QueueItem>(_queue);
            _queueIndexStash = _queueIndex;
            _musicStash = _musicTracks;
            _musicFromCacheStash = _musicFromCache;
            _sideModeStash = _sideMode;
            _sidePlaylistStash = _sidePlaylist;

            _queue.Clear();
            _queueIndex = -1;
            _sidePlaylist = null;
            _openPlaylist = null;
            _sideItems.Clear();
            HideLoading();
            ToastBox.Visibility = Visibility.Collapsed;

            _anon = true;
            QueueItem.Resolver = path =>
            {
                if (_anonItems.TryGetValue(path, out var it)) return ((string Name, bool IsAudio)?)(DisplayTitle(it), it.IsAudio);
                return null;
            };
            ThumbnailLoader.VaultDecoder = path => _vaultService.LoadThumb(path);
            PlaylistStore.UseVault(_vaultService);
            SetAnonItems(items);

            ApplyAnonymousUi(true);
            SideTabQueue.IsChecked = true;
            ShowLibrary(true);
            SetMode(LibMode.Recent);
            RefreshSide();
            UpdateTransportEnabled();

            // 4) Lights up in Anonymous Mode.
            await splash.PlayRevealAsync();
            splash.Close();
            splash = null;

            if (_vaultService.LastUnreadableCount > 0)
            {
                int n = _vaultService.LastUnreadableCount;
                Notify(n + (n == 1 ? " encrypted file couldn't be read" : " encrypted files couldn't be read") +
                       " (damaged, or made with a different key). They were left untouched.");
            }
        }
        catch (Exception ex)
        {
            try { splash?.Close(); } catch { }
            if (!_anon)
            {
                _vaultService.Lock();
                // the normal song was only paused for the splash: it carries on if Anonymous Mode could not start
                try { if (pausedByUs && _player != null) _player.SetPause(false); } catch { }
            }
            TempoDialog.Show(this, "Couldn't open Anonymous Mode: " + ex.Message, AppName,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _transition = false;
        }
    }

    /// <summary>Loads the vault key; if existing encrypted files have lost their key, offers to start a fresh vault.</summary>
    private bool TryUnlockVault()
    {
        try
        {
            _vaultService.Unlock();
            return true;
        }
        catch (FileNotFoundException)
        {
            var answer = TempoDialog.Show(this,
                "Tempo can't find the key for the encrypted files already in the vault, so they can't be opened.\n\n" +
                "Start a new, empty vault? The old files are moved to an \"unreadable\" folder inside the vault folder " +
                "and can't be recovered without their original key.",
                "Anonymous Mode", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return false;

            try
            {
                _vaultService.ResetUnreadable();
                _vaultService.Unlock();
                return true;
            }
            catch (Exception ex)
            {
                TempoDialog.Show(this, "Couldn't start a new vault: " + ex.Message, AppName,
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }
        catch (Exception ex)
        {
            TempoDialog.Show(this, "Couldn't open the vault: " + ex.Message, AppName,
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>Restyles the player for (or back from) Anonymous Mode: violet theme and the lock icon in the title bar.</summary>
    private void ApplyAnonymousUi(bool on)
    {
        ThemeManager.Apply(_cfg.Theme, _cfg.AccentIndex, anonymous: on);

        // The moving background keeps the colours it last had (the cover's, or the accent's): going in or out it must not carry the
        // other mode's colours (violet after leaving, the normal song's colours inside). Back to the current accent; a normal song's
        // cover colours are applied again right after the exit.
        var around = CoverPalette.Around((System.Windows.Media.Color)FindResource("AccentColor"));
        Aurora.SetPalette(around, null);
        WindowAurora.SetPalette(around, null);
        BreadcrumbText.Text = "Library";   // a folder name from the other mode must not stay in the title bar (pages set their own)
        SyncAmbience();
        AnonButton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ClosePopup(QuickPopup, animate: false);
        WaveBar.InvalidateVisual();

        if (on)
        {
            SetTitles(AppName);   // the window / taskbar title never shows a vault file name
            ShowMusic(false, "", "");
            EmptyHint.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Panic exit: wipe everything vault-related from memory and the screen, restore the normal media, queue and pages
    /// exactly. All of that happens immediately; the "lights on" animation is only cosmetic and plays over a black cover.
    /// </summary>
    private async void ExitAnonymous(bool animate = true)
    {
        if (!_anon || _player is null || _transition) return;
        _transition = true;

        AnonymousSplashWindow? splash = null;
        try
        {
            LibraryView.Visibility = Visibility.Hidden;   // the vault's pages are gone from the screen right now

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

            // ---- wipe
            _anon = false;
            _anonSettings?.Close();
            _importBusy = false;
            _modalOpen = false;
            _waveCts?.Cancel();
            WaveBar.Peaks = null;
            QueueItem.Resolver = null;
            ThumbnailLoader.VaultDecoder = null;
            PlaylistStore.UseVault(null);
            HideLoading();
            ToastBox.Visibility = Visibility.Collapsed;

            LibraryList.ItemsSource = null;
            _allEntries = new List<BrowserEntry>();
            _visibleEntries = new List<BrowserEntry>();
            _selectedEntry = null;
            SelectionState.Instance.Reset();
            _sideItems.Clear();
            ClearAnonData();
            _lyricsEditor?.Close();   // anything typed in Anonymous Mode goes with it
            _anonLyrics.Clear();
            ClearLyrics();
            Visualizer.SetPeaks(null);
            Visualizer.SetSpectrum(null);
            if (SearchBox.Text.Length > 0) SearchBox.Text = "";

            // ---- put the normal state back
            _queue.Clear();
            _queue.AddRange(_queueStash);
            _queueIndex = _queueIndexStash;
            _queueStash = new List<QueueItem>();
            _musicTracks = _musicStash;
            _musicFromCache = _musicFromCacheStash;
            _musicStash = new List<Track>();
            _openPlaylist = null;
            _musicScope = null;
            _currentDir = null;
            _navBack.Clear();
            _navForward.Clear();

            var snapshot = _snapshot;
            _snapshot = null;

            if (snapshot != null)
            {
                // Reload the saved path, resume at the saved timestamp.
                string folder = "";
                try { folder = Path.GetFileName(Path.GetDirectoryName(new Uri(snapshot.Mrl).LocalPath) ?? ""); } catch { /* no subtitle */ }
                ShowMusic(snapshot.WasAudio, DisplayNames.Clean(Path.GetFileNameWithoutExtension(snapshot.Title)), folder);
                StartPlayback(snapshot.Mrl, snapshot.TimeMs, isMrl: true);
                RetireCurrentVault();
                _normalTitle = snapshot.Title;
                if (snapshot.WasAudio)
                {
                    try { LoadWaveform(new Uri(snapshot.Mrl).LocalPath, null); } catch { /* flat bars */ }
                }
            }
            else if (_hasMedia)
            {
                // Nothing was playing before: just stop the vault media.
                _hasMedia = false;
                var player = _player;
                _ = Task.Run(() => player.Stop()).ContinueWith(_ => Dispatcher.InvokeAsync(RetireCurrentVault));   // fire and forget: the UI does not wait for the stop
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
            if (snapshot != null && snapshot.WasAudio)
            {
                // the song's cover and the background colours made from it (they were not touched while the vault was open)
                try { LoadCover(new Uri(snapshot.Mrl).LocalPath); } catch { /* the plain accent colours stay */ }
            }

            // side panel tabs back to what they were
            if (_sideModeStash == SideMode.Playlists) SideTabPlaylists.IsChecked = true; else SideTabQueue.IsChecked = true;
            _sidePlaylist = _sidePlaylistStash;
            _sidePlaylistStash = null;

            SetMode(_modeStash == LibMode.PlaylistDetail ? LibMode.Playlists : _modeStash);
            if (snapshot == null)
            {
                ResetTransport();
                ShowLibrary(true);
            }
            else
            {
                ShowLibrary(false);
            }
            LibraryView.Visibility = _libraryVisible ? Visibility.Visible : Visibility.Collapsed;
            RefreshSide();
            UpdateTransportEnabled();

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
            LibraryView.Visibility = _libraryVisible ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _transition = false;
        }
    }

    private async void OpenAnonymousSettings()
    {
        if (!_anon || _anonSettings != null) return;

        var win = new AnonymousSettingsWindow(_vaultService, PlayFromSettings, DeleteVaultItemAsync) { Owner = this };
        _anonSettings = win;
        _modalOpen = true;
        try { win.ShowDialog(); }
        finally
        {
            _anonSettings = null;
            _modalOpen = false;
        }

        if (_anon) await ReloadAnonAsync();
        RootGrid.Focus();
    }
}
