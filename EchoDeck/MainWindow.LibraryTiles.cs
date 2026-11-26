using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace EchoDeck;

public partial class MainWindow
{
    // ------------------------------------------------------------------ page content

    private void BuildRecent()
    {
        // Anonymous Mode keeps its own list in memory only (never written to disk, wiped on exit).
        IEnumerable<string> source = _anon ? _anonRecent : RecentStore.Items;
        _allEntries = source.Where(PathExists).Select(EntryForPath).ToList();
    }

    /// <summary>A file / vault item exists: real file normally, a known vault item in Anonymous Mode.</summary>
    private bool PathExists(string path) => _anon ? _anonItems.ContainsKey(path) : File.Exists(path);

    private bool IsSupportedPath(string path) => _anon ? _anonItems.ContainsKey(path) : MediaTypes.IsSupportedFile(path);

    /// <summary>Tile / row for a file path (real name in normal mode; the encrypted header's details in Anonymous Mode).</summary>
    private BrowserEntry EntryForPath(string path)
    {
        if (_anon && _anonItems.TryGetValue(path, out var item)) return VaultEntry(item);
        return new BrowserEntry(DisplayNames.Clean(Path.GetFileNameWithoutExtension(path)), path,
            MediaTypes.IsAudio(path) ? EntryKind.Audio : EntryKind.Video,
            Path.GetFileName(Path.GetDirectoryName(path) ?? ""));
    }

    private List<BrowserEntry> VideoEntries(List<string> files)
    {
        var list = files.Select(p => new BrowserEntry(DisplayNames.Clean(Path.GetFileNameWithoutExtension(p)), p, EntryKind.Video,
            Path.GetFileName(Path.GetDirectoryName(p) ?? ""))).ToList();
        if (_sortDesc) list.Reverse();
        return list;
    }

    private async void BuildVideos()
    {
        int token = _pageToken;

        if (_anon)
        {
            _allEntries = AnonFolderEntries(audio: false);   // grouped by the folders they came from, like the Home page
            FinishPage();
            return;
        }

        _allEntries = new List<BrowserEntry>();
        var folders = _cfg.VideoFolders;

        // 1) what was found last time appears at once ...
        var cached = await LibraryScanner.CachedVideosAsync(folders);
        if (token != _pageToken || _mode != LibMode.Videos) return;
        if (cached.Count > 0) _allEntries = VideoEntries(cached);
        else _loadingText = "Looking for videos…";
        FinishPage();
        ShowLoading(cached.Count > 0 ? "Checking for new videos…" : "Looking for videos…");

        // 2) ... while the folders are checked for changes in the background
        List<string> files;
        try { files = await LibraryScanner.VideosAsync(folders); }
        catch { files = cached; }
        if (token != _pageToken || _mode != LibMode.Videos) return;

        _loadingText = null;
        HideLoading();
        bool same = cached.Count > 0 && files.Count == cached.Count &&
                    files.SequenceEqual(cached, StringComparer.OrdinalIgnoreCase);
        if (same) return;   // nothing changed: keep the page (and its scroll position) as it is

        _allEntries = VideoEntries(files);
        FinishPage();
    }

    private async void BuildMusic()
    {
        int token = _pageToken;
        _allEntries = new List<BrowserEntry>();

        if (_anon)
        {
            _musicTracks = AnonTracks();
            BuildMusicEntries();
            FinishPage();
            return;
        }

        if (_musicTracks.Count > 0 && !_musicFromCache)
        {
            BuildMusicEntries();
            FinishPage();
            return;
        }

        var folders = _cfg.MusicFolders;

        // 1) the songs read last time appear at once ...
        var cached = await LibraryScanner.CachedTracksAsync(folders);
        if (token != _pageToken || _mode != LibMode.Music) return;
        if (cached.Count > 0)
        {
            _musicTracks = cached;
            _musicFromCache = true;
            BuildMusicEntries();
        }
        else
        {
            _loadingText = "Reading your music library…";
        }
        FinishPage();
        ShowLoading(cached.Count > 0 ? "Checking for new songs…" : "Reading your music library…");

        // 2) ... while only new or changed files are read in the background
        List<Track> fresh;
        try { fresh = await LibraryScanner.TracksAsync(folders); }
        catch { fresh = cached; }
        if (token != _pageToken || _mode != LibMode.Music) return;

        _loadingText = null;
        HideLoading();
        _musicTracks = fresh;
        _musicFromCache = false;
        bool same = cached.Count > 0 && cached.Count == fresh.Count && cached.Zip(fresh).All(z => z.First == z.Second);
        if (same) return;

        BuildMusicEntries();
        FinishPage();
    }

    private BrowserEntry SongEntry(Track t, bool stripe) =>
        new(t.Title, t.Path, EntryKind.Audio, "") { Artist = t.Artist, Album = t.Album, Genre = t.Genre, Duration = t.Duration, Stripe = stripe, IsVault = _anon };

    private void BuildMusicEntries()
    {
        IEnumerable<Track> source = _musicTracks;
        if (_musicScope != null)
        {
            source = _musicScope.IsAlbum
                ? _musicTracks.Where(t => t.Album == _musicScope.Album && t.Artist == _musicScope.Artist)
                : _musicTracks.Where(t => t.Artist == _musicScope.Name);
        }

        var tracks = (_sortDesc
            ? source.OrderByDescending(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
            : source.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)).ToList();

        int tab = _musicScope != null ? 0 : _musicTab;
        if (tab == 0)
        {
            _allEntries = _anon && _musicScope == null
                ? AnonFolderEntries(audio: true)   // Anonymous Mode: songs grouped by the folders they came from
                : tracks.Select((t, i) => SongEntry(t, i % 2 == 0)).ToList();
        }
        else if (tab == 1)
        {
            var albums = tracks.GroupBy(t => (t.Album, t.Artist))
                .OrderBy(g => g.Key.Album, StringComparer.CurrentCultureIgnoreCase);
            var list = albums.Select(g =>
            {
                var children = g.Select(t => SongEntry(t, false)).ToList();
                return new BrowserEntry(g.Key.Album, g.Key.Album + "|" + g.Key.Artist, EntryKind.Album, g.Key.Artist)
                {
                    Album = g.Key.Album,
                    Artist = g.Key.Artist,
                    Children = children,
                    IsVault = _anon
                };
            }).ToList();
            if (_sortDesc) list.Reverse();
            _allEntries = list;
        }
        else
        {
            var artists = tracks.GroupBy(t => t.Artist).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);
            var list = artists.Select(g =>
            {
                var children = g.Select(t => SongEntry(t, false)).ToList();
                return new BrowserEntry(g.Key, g.Key, EntryKind.Artist, ItemWord(children.Count, "song"))
                {
                    Artist = g.Key,
                    Children = children,
                    IsVault = _anon
                };
            }).ToList();
            if (_sortDesc) list.Reverse();
            _allEntries = list;
        }
    }

    private void MusicTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressTab || _mode != LibMode.Music) return;
        _musicTab = TabAlbums.IsChecked == true ? 1 : TabArtists.IsChecked == true ? 2 : 0;
        _musicScope = null;
        if (SearchBox.Text.Length > 0) SearchBox.Text = "";
        BuildMusicEntries();
        FinishPage();
    }

    private void SetMusicTabs(int tab)
    {
        _suppressTab = true;
        _musicTab = tab;
        TabSongs.IsChecked = tab == 0;
        TabAlbums.IsChecked = tab == 1;
        TabArtists.IsChecked = tab == 2;
        _suppressTab = false;
    }

    private void OpenMusicScope(BrowserEntry album)
    {
        _musicTabBeforeScope = _musicTab;
        _musicScope = album;
        if (SearchBox.Text.Length > 0) SearchBox.Text = "";
        BuildMusicEntries();
        FinishPage();
    }

    /// <summary>"Show album" / "Show artist" from a song's menu.</summary>
    private void ShowAlbumOrArtist(BrowserEntry song, bool album)
    {
        ShowLibrary(true);
        _mode = LibMode.Music;
        _pageToken++;
        SetMusicTabs(0);
        _musicTabBeforeScope = album ? 1 : 2;

        var scope = album
            ? new BrowserEntry(song.Album, song.Album + "|" + song.Artist, EntryKind.Album, song.Artist) { Album = song.Album, Artist = song.Artist }
            : new BrowserEntry(song.Artist, song.Artist, EntryKind.Artist) { Artist = song.Artist };
        _navBack.Clear();
        _navForward.Clear();
        _musicScope = scope;
        if (_musicTracks.Count == 0) { BuildMusic(); return; }
        BuildMusicEntries();
        FinishPage();
    }

    private void BuildPlaylists()
    {
        PlaylistStore.EnsureLoaded();
        var list = PlaylistStore.All
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new BrowserEntry(p.Name, p.Id, EntryKind.Playlist, ItemCount(p.Items.Count), p))
            .ToList();
        if (_sortDesc) list.Reverse();
        _allEntries = list;
    }

    private void BuildPlaylistDetail(Playlist pl)
    {
        _allEntries = pl.Items.Where(PathExists).Select(EntryForPath).ToList();

        PlName.Text = pl.Name;
        PlInfo.Text = ItemCount(_allEntries.Count);
        PlCoverBrush.ImageSource = _anon ? null : LoadImage(pl.Cover);
    }

    private static string ItemCount(int n) => ItemWord(n, "item");
    private static string ItemWord(int n, string word) => n == 1 ? "1 " + word : $"{n} {word}s";

    private static BitmapImage? LoadImage(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.DecodePixelWidth = 400;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    private void OpenPlaylist(Playlist pl)
    {
        _mode = LibMode.PlaylistDetail;
        _pageToken++;
        _openPlaylist = pl;
        if (SearchBox.Text.Length > 0) SearchBox.Text = "";
        BuildPlaylistDetail(pl);
        FinishPage();
    }

    /// <summary>Re-reads the open page after something changed underneath it (playlist edited, recent cleared ...).</summary>
    private void RefreshPage()
    {
        switch (_mode)
        {
            case LibMode.Recent: BuildRecent(); break;
            case LibMode.Playlists: BuildPlaylists(); break;
            case LibMode.PlaylistDetail:
                if (_openPlaylist != null && PlaylistStore.All.Contains(_openPlaylist)) BuildPlaylistDetail(_openPlaylist);
                else { SetMode(LibMode.Playlists); return; }
                break;
            default: return;
        }
        FinishPage();
    }

    // ------------------------------------------------------------------ tiles

    private static BrowserEntry? EntryOf(object sender) => (sender as FrameworkElement)?.DataContext as BrowserEntry;

    private void Tile_Loaded(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is not { } entry) return;
        entry.Visible = true;
        ThumbnailLoader.Request(entry);
    }

    private void Tile_Unloaded(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is not { } entry) return;
        entry.Visible = false;
        entry.Thumbnail = null;   // keeps memory flat while scrolling through hundreds of files
    }

    private void Tile_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (EntryOf(sender) is not { } entry) return;

        if (_selectedEntry != null) _selectedEntry.IsSelected = false;
        _selectedEntry = entry;
        entry.IsSelected = true;

        if (e.ClickCount >= 2) ActivateEntry(entry);
    }

    private void Tile_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (EntryOf(sender) is not { } entry) return;
        e.Handled = true;
        ShowEntryMenu(entry, null);
    }

    private void TilePlay_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is not { } entry) return;
        if (entry.IsPlaylist && entry.Playlist != null) PlayPlaylist(entry.Playlist, shuffle: false);
        else if (entry.Children != null) PlayEntries(entry.Children.Where(c => c.IsFile).ToList(), 0);
        else ActivateEntry(entry);
    }

    private void TileMore_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is not { } entry) return;
        ShowEntryMenu(entry, sender as UIElement);
    }

    private void PlayEntries(List<BrowserEntry> files, int start)
    {
        if (files.Count == 0) return;
        _queue.Clear();
        foreach (var f in files) _queue.Add(new QueueItem(f.FullPath));
        PlayNormal(files[Math.Clamp(start, 0, files.Count - 1)].FullPath);
        RootGrid.Focus();
    }

    /// <summary>Double-click / play: folders, playlists, albums and artists open; files play with the visible files as the queue.</summary>
    private void ActivateEntry(BrowserEntry entry)
    {
        if (entry.IsDirectory) { OpenFolderEntry(entry.FullPath); return; }
        if (entry.IsPlaylist && entry.Playlist != null) { OpenPlaylist(entry.Playlist); return; }
        if (entry.IsAlbum || entry.IsArtist) { OpenMusicScope(entry); return; }
        if (!entry.IsFile) return;

        var files = _visibleEntries.Where(x => x.IsFile).ToList();

        // Smart queue: an episode whose series goes beyond this page (its next seasons are in other folders, or only some episodes are
        // shown here) queues the whole series in order, not just what is visible.
        if (_cfg.SmartQueue && !_anon && File.Exists(entry.FullPath))
        {
            var series = SmartQueue.SeriesOf(entry.FullPath);
            var visible = new HashSet<string>(files.Select(f => f.FullPath), StringComparer.OrdinalIgnoreCase);
            if (series.Count > 1 && series.Any(p => !visible.Contains(p)))
            {
                _queue.Clear();
                foreach (var p in series) _queue.Add(new QueueItem(p));
                PlayNormal(entry.FullPath);
                RootGrid.Focus();
                ShowOsd("Queued " + series.Count + " in order");
                return;
            }
        }

        PlayEntries(files, files.FindIndex(f => string.Equals(f.FullPath, entry.FullPath, StringComparison.OrdinalIgnoreCase)));
    }

    // ------------------------------------------------------------------ tick-box selection

    private IEnumerable<BrowserEntry> CheckedFiles() => _visibleEntries.Where(x => x.IsChecked && x.IsFile);

    private void UpdateSelectionBar()
    {
        int n = SelectionState.Instance.Count;
        SelectionBar.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        SelRestoreButton.Visibility = _anon ? Visibility.Visible : Visibility.Collapsed;   // restoring only exists in Anonymous Mode
        SelCountText.Text = n == 1 ? "1 item selected" : $"{n} items selected";
        int total = _visibleEntries.Count(x => x.IsFile);
        SelectAllCheck.IsChecked = n > 0 && n >= total;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        bool on = SelectAllCheck.IsChecked == true;
        foreach (var entry in _visibleEntries.Where(x => x.IsFile)) entry.IsChecked = on;
    }

    private void SelClear_Click(object sender, RoutedEventArgs e)
    {
        foreach (var entry in _visibleEntries) entry.IsChecked = false;
    }

    private void SelRestore_Click(object sender, RoutedEventArgs e) => RestoreSelected_Click(sender, e);

    private void SelPlay_Click(object sender, RoutedEventArgs e)
    {
        var files = CheckedFiles().ToList();
        if (files.Count == 0) return;
        PlayEntries(files, _cfg.Shuffle ? _rng.Next(files.Count) : 0);
    }

    private void SelPlayNext_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in CheckedFiles().Reverse().ToList()) Enqueue(f.FullPath, next: true);
    }

    private void SelAddTo_Click(object sender, RoutedEventArgs e)
    {
        var paths = CheckedFiles().Select(x => x.FullPath).ToList();
        if (paths.Count == 0) return;
        ShowPlaylistPicker(paths, SelAddToButton);
    }
}
