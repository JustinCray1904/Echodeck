using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;

namespace EchoDeck;

/// <summary>
/// Anonymous Mode. The same library pages, queue and playlists as normal mode, but fed from the encrypted vault:
/// the lock icon in the title bar is the indicator and holds import / restore / settings.
/// </summary>
public partial class MainWindow
{
    // What the vault holds (details come from each file's encrypted header; nothing here ever touches the disk)
    private readonly Dictionary<string, VaultItem> _anonItems = new(StringComparer.OrdinalIgnoreCase);
    private List<VaultItem> _anonList = new();
    private readonly List<string> _anonRecent = new();   // "Recent" while the mode is on: memory only, wiped on exit

    // Virtual folders, built from each file's original folder
    private readonly Dictionary<string, List<VaultItem>> _vFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _vKids = new(StringComparer.OrdinalIgnoreCase);
    private bool _anonRootMulti;

    // The normal-mode state that is set aside while Anonymous Mode is on and put back on exit
    private List<QueueItem> _queueStash = new();
    private int _queueIndexStash = -1;
    private List<Track> _musicStash = new();
    private bool _musicFromCacheStash;
    private LibMode _modeStash = LibMode.Recent;
    private SideMode _sideModeStash = SideMode.Queue;
    private Playlist? _sidePlaylistStash;

    // ------------------------------------------------------------------ vault data -> pages

    private static string DisplayTitle(VaultItem it) =>
        string.IsNullOrWhiteSpace(it.Title) ? Path.GetFileNameWithoutExtension(it.Name) : it.Title;

    private static string FirstArtistOf(string artist)
    {
        int i = artist.IndexOf(';');
        return (i > 0 ? artist.Substring(0, i) : artist).Trim();
    }

    private static string OriginLeaf(VaultItem it) =>
        string.IsNullOrWhiteSpace(it.Origin) ? "Imported" : LastSegment(VKey(it.Origin));

    private BrowserEntry VaultEntry(VaultItem it, string? subtitle = null)
    {
        string artist = it.Artist.Length > 0 ? FirstArtistOf(it.Artist) : "";
        return new BrowserEntry(DisplayTitle(it), it.FullPath, it.IsAudio ? EntryKind.Audio : EntryKind.Video,
            subtitle ?? (it.IsAudio && artist.Length > 0 ? artist : OriginLeaf(it)))
        {
            Artist = artist,
            Album = it.Album,
            Genre = it.Genre,
            Duration = LibraryScanner.FormatDuration(it.DurationMs / 1000),
            IsVault = true
        };
    }

    private List<Track> AnonTracks() =>
        _anonList.Where(x => x.IsAudio).Select(x => new Track(
            x.FullPath,
            DisplayTitle(x),
            x.Artist.Length > 0 ? FirstArtistOf(x.Artist) : "Unknown artist",
            x.Album.Length > 0 ? x.Album : "Unknown album",
            x.Genre.Length > 0 ? x.Genre : "Unknown genre",
            LibraryScanner.FormatDuration(x.DurationMs / 1000)))
        .OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase).ToList();

    private void SetAnonItems(IReadOnlyList<VaultItem> items)
    {
        _anonList = items.ToList();
        _anonItems.Clear();
        foreach (var it in _anonList) _anonItems[it.FullPath] = it;
        BuildVirtualTree();
        _musicTracks = AnonTracks();
        _musicFromCache = false;
    }

    private void ClearAnonData()
    {
        _anonItems.Clear();
        _anonList = new List<VaultItem>();
        _vFiles.Clear();
        _vKids.Clear();
        _anonRecent.Clear();
        _anonRootMulti = false;
    }

    /// <summary>Re-reads the vault and rebuilds the open page (after an import, restore or delete).</summary>
    private async Task ReloadAnonAsync()
    {
        if (!_anon) return;
        try
        {
            var items = await _vaultService.ListAsync();
            if (!_anon) return;
            SetAnonItems(items);
            ReloadCurrentPage();
            RefreshSide();
        }
        catch (Exception ex)
        {
            Notify("Couldn't refresh the vault: " + ex.Message);
        }
    }

    private void ReloadCurrentPage()
    {
        switch (_mode)
        {
            case LibMode.Home:
                if (_currentDir != null) NavigateVirtual(_currentDir, record: false); else GoRootAnon();
                break;
            case LibMode.Videos: BuildVideos(); break;
            case LibMode.Music: BuildMusic(); break;
            case LibMode.Recent:
            case LibMode.Playlists:
            case LibMode.PlaylistDetail: RefreshPage(); break;
        }
    }

    // ------------------------------------------------------------------ virtual folders

    private static string VKey(string? origin) =>
        string.IsNullOrWhiteSpace(origin)
            ? "Imported"
            : string.Join("\\", origin.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries));

    private static string LastSegment(string key)
    {
        int i = key.LastIndexOf('\\');
        return i >= 0 ? key.Substring(i + 1) : key;
    }

    private void BuildVirtualTree()
    {
        _vFiles.Clear();
        _vKids.Clear();

        foreach (var item in _anonList)
        {
            var key = VKey(item.Origin);
            if (!_vFiles.TryGetValue(key, out var list)) _vFiles[key] = list = new List<VaultItem>();
            list.Add(item);

            // every ancestor folder exists too
            string parent = "";
            foreach (var seg in key.Split('\\'))
            {
                string cur = parent.Length == 0 ? seg : parent + "\\" + seg;
                if (!_vKids.TryGetValue(parent, out var kids))
                    _vKids[parent] = kids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                kids.Add(cur);
                parent = cur;
            }
        }
    }

    private List<string> KidsOf(string dir)
    {
        var kids = _vKids.TryGetValue(dir, out var set) ? set.ToList() : new List<string>();
        kids.Sort((a, b) => string.Compare(LastSegment(a), LastSegment(b), StringComparison.CurrentCultureIgnoreCase));
        if (_sortDesc) kids.Reverse();
        return kids;
    }

    private IEnumerable<VaultItem> VirtualFiles(string dir) =>
        _vFiles.TryGetValue(dir, out var list) ? list : Enumerable.Empty<VaultItem>();

    /// <summary>A folder card; folders that only contain one sub-folder are merged (C: › Users › me › Videos).</summary>
    private BrowserEntry VirtualFolderEntry(string child)
    {
        var names = new List<string> { LastSegment(child) };
        var cur = child;
        while (!_vFiles.ContainsKey(cur) && _vKids.TryGetValue(cur, out var kids) && kids.Count == 1)
        {
            cur = kids.First();
            names.Add(LastSegment(cur));
        }
        return new BrowserEntry(string.Join("  ›  ", names), cur, EntryKind.Folder, "Folder") { IsVault = true };
    }

    private void GoRootAnon()
    {
        _currentDir = null;
        var kids = KidsOf("");
        _anonRootMulti = kids.Count > 1;

        if (kids.Count == 1 && !_vFiles.ContainsKey(""))
        {
            NavigateVirtual(VirtualFolderEntry(kids[0]).FullPath, record: false);
            return;
        }

        _allEntries = kids.Select(VirtualFolderEntry).ToList();
        BreadcrumbText.Text = "Library";
        FinishPage();
    }

    private void NavigateVirtual(string dir, bool record)
    {
        if (dir.Length == 0 || (!_vKids.ContainsKey(dir) && !_vFiles.ContainsKey(dir)))
        {
            GoRootAnon();
            return;
        }

        var list = new List<BrowserEntry>();
        foreach (var kid in KidsOf(dir)) list.Add(VirtualFolderEntry(kid));

        var files = VirtualFiles(dir).OrderBy(DisplayTitle, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (_sortDesc) files.Reverse();
        foreach (var f in files) list.Add(VaultEntry(f, f.SizeText));

        _mode = LibMode.Home;
        if (record && _currentDir != null && !string.Equals(_currentDir, dir, StringComparison.OrdinalIgnoreCase))
        {
            _navBack.Push(_currentDir);
            _navForward.Clear();
        }

        _currentDir = dir;
        _allEntries = list;
        BreadcrumbText.Text = LastSegment(dir);
        FinishPage();
    }

    private static string VirtualBreadcrumb(string dir) =>
        dir.Length == 0 ? "Home" : "Home  ›  " + string.Join("  ›  ", dir.Split('\\'));

    // ------------------------------------------------------------------ lock icon menu

    private void AnonButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_anon) return;

        var menu = new ContextMenu { PlacementTarget = AnonButton, Placement = PlacementMode.Bottom };
        menu.Items.Add(MenuEntry("Import files…", "IcoImport", ImportFiles_Click, "Ctrl+O"));
        menu.Items.Add(MenuEntry("Import folder…", "IcoFolder", ImportFolder_Click));
        menu.Items.Add(MenuEntry("Restore selected…", "IcoExport", RestoreSelected_Click));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Anonymous settings", "IcoSettings", (_, _) => OpenAnonymousSettings()));
        menu.Items.Add(MenuEntry("Exit Anonymous Mode", "IcoClose", (_, _) => ExitAnonymous(), "Esc"));
        menu.IsOpen = true;
    }

    // ------------------------------------------------------------------ import

    private void ImportFiles_Click(object sender, RoutedEventArgs e)
    {
        if (!_anon || _importBusy) return;

        string[] names;
        _modalOpen = true;
        try
        {
            var dlg = new OpenFileDialog { Title = "Import into Anonymous Mode", Filter = MediaTypes.DialogFilter, Multiselect = true };
            if (dlg.ShowDialog(this) != true) return;
            names = dlg.FileNames;
        }
        finally { _modalOpen = false; }

        _ = ImportPathsAsync(names.Where(MediaTypes.IsSupportedFile).ToList());
    }

    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!_anon || _importBusy) return;

        string folder;
        _modalOpen = true;
        try
        {
            var dlg = new OpenFolderDialog { Title = "Import a folder into Anonymous Mode" };
            if (dlg.ShowDialog(this) != true) return;
            folder = dlg.FolderName;
        }
        finally { _modalOpen = false; }

        var files = await Task.Run(() => VaultImporter.CollectFolder(folder));
        await ImportPathsAsync(files);
    }

    private async Task ImportDroppedAsync(string[] items)
    {
        if (!_anon || _importBusy) return;

        var files = new List<string>();
        foreach (var path in items)
        {
            if (Directory.Exists(path)) files.AddRange(await Task.Run(() => VaultImporter.CollectFolder(path)));
            else if (MediaTypes.IsSupportedFile(path)) files.Add(path);
        }
        await ImportPathsAsync(files);
    }

    private async Task ImportPathsAsync(List<string> files)
    {
        if (!_anon || files.Count == 0 || _importBusy) return;
        _importBusy = true;
        int total = files.Count;

        try
        {
            ShowLoading("Encrypting…", "0 / " + total.ToString("N0"), 0);
            var text = new Progress<string>(t => LoadText.Text = t);
            var fraction = new Progress<double>(f =>
                ShowLoading(LoadText.Text, ((int)Math.Round(f * total)).ToString("N0") + " / " + total.ToString("N0"), f));

            var result = await VaultImporter.ImportAsync(_vaultService, files, _cfg.AnonymousDeleteOriginals,
                text, () => !_anon, fraction);

            if (_anon)
            {
                Notify(VaultImporter.Summary(result));
                await ReloadAnonAsync();
            }
        }
        catch (Exception ex)
        {
            Notify("Import failed: " + ex.Message);
        }
        finally
        {
            _importBusy = false;
            HideLoading();
        }
    }

    // ------------------------------------------------------------------ restore / delete

    private List<VaultItem> ItemsOf(IEnumerable<BrowserEntry> entries) =>
        entries.Select(e => _anonItems.TryGetValue(e.FullPath, out var it) ? it : null)
               .Where(it => it != null).Select(it => it!).ToList();

    /// <summary>The ticked items when the clicked tile is one of them, otherwise just the clicked tile.</summary>
    private List<VaultItem> RestoreTargets(BrowserEntry entry)
    {
        var ticked = CheckedFiles().ToList();
        return ItemsOf(entry.IsChecked && ticked.Count > 0 ? ticked : new List<BrowserEntry> { entry });
    }

    private void RestoreSelected_Click(object sender, RoutedEventArgs e)
    {
        var targets = ItemsOf(CheckedFiles());
        if (targets.Count == 0)
        {
            Notify("Tick the items you want to restore first");
            return;
        }
        _ = RestoreEntriesAsync(targets);
    }

    private async Task RestoreEntriesAsync(List<VaultItem> items)
    {
        if (!_anon || items.Count == 0 || _importBusy) return;
        _importBusy = true;
        _modalOpen = true;

        try
        {
            var fraction = new Progress<double>(f =>
                ShowLoading("Restoring…", ((int)Math.Round(f * items.Count)).ToString("N0") + " / " + items.Count.ToString("N0"), f));
            var message = await VaultRestore.RunManyAsync(this, _vaultService, items, DeleteVaultItemAsync, fraction);
            if (!string.IsNullOrEmpty(message)) Notify(message);
        }
        catch (Exception ex)
        {
            Notify("Restore failed: " + ex.Message);
        }
        finally
        {
            _modalOpen = false;
            _importBusy = false;
            HideLoading();
        }

        await ReloadAnonAsync();
    }

    private async Task DeleteEntriesAsync(List<VaultItem> items)
    {
        if (!_anon || items.Count == 0 || _importBusy) return;

        _modalOpen = true;
        MessageBoxResult answer;
        try
        {
            answer = MessageBox.Show(this,
                (items.Count == 1 ? "Permanently delete “" + DisplayTitle(items[0]) + "”" : $"Permanently delete {items.Count} items") +
                " from Anonymous Mode?\n\nThe encrypted file is overwritten and removed. This can't be undone.",
                "Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        }
        finally { _modalOpen = false; }
        if (answer != MessageBoxResult.Yes) return;

        int failed = 0;
        string? lastError = null;
        foreach (var item in items)
        {
            var error = await DeleteVaultItemAsync(item);
            if (error != null) { failed++; lastError = error; }
        }

        Notify(failed == 0 ? (items.Count == 1 ? "Deleted" : $"Deleted {items.Count} items") : $"{failed} couldn't be deleted. {lastError}");
        await ReloadAnonAsync();
    }

    private void RemoveFromQueue(string path)
    {
        int idx = _queue.FindIndex(q => string.Equals(q.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return;
        _queue.RemoveAt(idx);
        if (idx < _queueIndex) _queueIndex--;
        else if (idx == _queueIndex) _queueIndex = -1;
    }

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
        try
        {
            // 0) Make sure the vault can be opened and read BEFORE anything changes on screen.
            if (!TryUnlockVault()) return;
            await _vaultService.MigrateLegacyAsync();
            var items = await _vaultService.ListAsync();

            // 1) Freeze the normal media and remember exactly where it was.
            StopResumeTracking();   // the video's spot is saved for "continue where you left off"
            string? mrl = null;
            if (_hasMedia)
            {
                using var media = _player.Media;
                mrl = media?.Mrl;
            }
            if (_player.IsPlaying) _player.SetPause(true);
            _snapshot = mrl != null ? new PlaybackSnapshot(mrl, _player.Time, _currentIsAudio, _normalTitle) : null;

            // 2) Going dark.
            splash = new AnonymousSplashWindow(WindowHelper.GetScreenBoundsDip(this), entering: true);
            splash.Show();
            await splash.PlayDarkenAsync();

            // 3) Under cover of the black screen: STOP the normal media (it must not keep sounding or be resumable
            //    from the Anonymous controls), set the normal state aside, switch every page over to the vault.
            var player = _player;
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
            if (!_anon) _vaultService.Lock();
            MessageBox.Show(this, "Couldn't open Anonymous Mode: " + ex.Message, AppName,
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
            var answer = MessageBox.Show(this,
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
                MessageBox.Show(this, "Couldn't start a new vault: " + ex.Message, AppName,
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't open the vault: " + ex.Message, AppName,
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>Restyles the player for (or back from) Anonymous Mode: violet theme and the lock icon in the title bar.</summary>
    private void ApplyAnonymousUi(bool on)
    {
        ThemeManager.Apply(_cfg.Theme, _cfg.AccentIndex, anonymous: on);
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
                ShowMusic(snapshot.WasAudio, Path.GetFileNameWithoutExtension(snapshot.Title), "");
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
                Task.Run(() => player.Stop()).ContinueWith(_ => Dispatcher.InvokeAsync(RetireCurrentVault));
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
        Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            try { media?.Dispose(); } catch { /* already released */ }
            try { input?.Dispose(); } catch { /* already released */ }
        });
    }
}
