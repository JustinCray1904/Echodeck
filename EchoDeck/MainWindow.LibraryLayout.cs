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
    // ------------------------------------------------------------------ responsive tile columns

    private int _tileCols;
    private System.Windows.Threading.DispatcherTimer? _regroupTimer;

    private List<EntryRow> BuildRows(int columns)
    {
        var rows = new List<EntryRow>();
        if (columns > 0)
        {
            for (int i = 0; i < _visibleEntries.Count; i += columns)
                rows.Add(new EntryRow(_visibleEntries.Skip(i).Take(columns).ToList(), columns));
        }
        else
        {
            foreach (var entry in _visibleEntries) rows.Add(new EntryRow(new List<BrowserEntry> { entry }));
        }
        return rows;
    }

    /// <summary>As many tiles per row as fit the list's width (never fewer than two, never cropped).</summary>
    private int TileColumns(string key)
    {
        double width = LibraryList.ActualWidth;
        if (FindScrollViewer(LibraryList) is { ViewportWidth: > 0 } viewer) width = viewer.ViewportWidth;
        if (width <= 0) width = 960;

        double minCell = key == "RowGridTpl" ? 236 : key == "RowSquareTpl" ? 200 : 180;
        return Math.Clamp((int)Math.Floor(width / minCell), 2, 8);
    }

    private void LibraryList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_tileCols <= 0 || !e.WidthChanged) return;
        _regroupTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _regroupTimer.Tick -= RegroupTimer_Tick;
        _regroupTimer.Tick += RegroupTimer_Tick;
        _regroupTimer.Stop();
        _regroupTimer.Start();
    }

    private void RegroupTimer_Tick(object? sender, EventArgs e)
    {
        _regroupTimer?.Stop();
        if (_tileCols <= 0 || !_libraryVisible) return;

        var key = RowTemplateKey();
        if (key is not ("RowGridTpl" or "RowSquareTpl" or "RowArtistTpl")) return;
        int cols = TileColumns(key);
        if (cols == _tileCols) return;
        _tileCols = cols;

        // same tiles (selection stays), new grouping; keep roughly the same place in the list
        var viewer = FindScrollViewer(LibraryList);
        double ratio = viewer != null && viewer.ExtentHeight > 0 ? viewer.VerticalOffset / viewer.ExtentHeight : 0;
        LibraryList.ItemsSource = BuildRows(cols);
        if (viewer != null && ratio > 0)
            Dispatcher.InvokeAsync(() => viewer.ScrollToVerticalOffset(ratio * viewer.ExtentHeight), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void UpdateEmptyState()
    {
        bool empty = _visibleEntries.Count == 0;
        LibEmptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty) return;

        bool searching = SearchBox.Text.Trim().Length > 0;
        string? button = null;
        _emptyAction = null;

        string message;
        if (_loadingText != null)
        {
            message = _loadingText;
        }
        else if (searching)
        {
            message = "No matches for “" + SearchBox.Text.Trim() + "”.";
        }
        else if (_anon && _mode is LibMode.Home or LibMode.Videos or LibMode.Music)
        {
            message = "Nothing here yet. Use the lock icon at the top to import files into Anonymous Mode.";
            button = "Import files";
            _emptyAction = () => ImportFiles_Click(this, new RoutedEventArgs());
        }
        else
        {
            switch (_mode)
            {
                case LibMode.Recent:
                    message = "Nothing played yet. Videos and songs you play will show up here.";
                    button = "Browse Home";
                    _emptyAction = () => { ShowLibrary(true); SetMode(LibMode.Home); };
                    break;
                case LibMode.Playlists:
                    message = "No playlists yet. Playlists you create will live here.";
                    button = "Create a playlist";
                    _emptyAction = () => NewPlaylist_Click(this, new RoutedEventArgs());
                    break;
                case LibMode.PlaylistDetail:
                    message = "There are no items in this playlist. Use the ··· menu on any song or video and pick “Add to”.";
                    break;
                case LibMode.Blank:
                    message = "Pick Recent, Home, Videos or Music on the left to get started.";
                    break;
                case LibMode.Music:
                    message = "No music found. Add a music folder in Settings › Library.";
                    button = "Open settings";
                    _emptyAction = () => ShowSettings("library");
                    break;
                case LibMode.Videos:
                    message = "No videos found. Add a video folder in Settings › Library.";
                    button = "Open settings";
                    _emptyAction = () => ShowSettings("library");
                    break;
                default:
                    message = "No media found in this folder.";
                    button = "Open a folder";
                    _emptyAction = () => OpenFolder_Click(this, new RoutedEventArgs());
                    break;
            }
        }

        LibEmptyHint.Text = message;
        LibEmptyButton.Content = button ?? "";
        LibEmptyButton.Visibility = button == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void LibEmpty_Click(object sender, RoutedEventArgs e) => _emptyAction?.Invoke();

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        RebuildRows();
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        _sortDesc = !_sortDesc;
        switch (_mode)
        {
            case LibMode.Home:
                if (_currentDir != null) NavigateTo(_currentDir, record: false); else GoRoot();
                break;
            case LibMode.Videos:
                BuildVideos();
                break;
            case LibMode.Music:
                BuildMusicEntries();
                FinishPage();
                break;
            case LibMode.PlaylistDetail when _openPlaylist != null:
                SortPlaylistItems(_openPlaylist);
                BuildPlaylistDetail(_openPlaylist);
                FinishPage();
                break;
            case LibMode.Playlists:
                BuildPlaylists();
                FinishPage();
                break;
            default:
                _allEntries = (_sortDesc
                    ? _allEntries.OrderByDescending(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                    : _allEntries.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)).ToList();
                RebuildRows();
                break;
        }
    }

    private void SortPlaylistItems(Playlist pl)
    {
        pl.Items = (_sortDesc
            ? pl.Items.OrderByDescending(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase)
            : pl.Items.OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase)).ToList();
        PlaylistStore.Save();
    }

    private void ViewToggle_Click(object sender, RoutedEventArgs e)
    {
        _cfg.LibraryGrid = !_cfg.LibraryGrid;
        _cfg.Save(notify: false);
        ApplyViewMode();
        RebuildRows();
    }

    private void ApplyViewMode()
    {
        ViewToggleIcon.Data = (System.Windows.Media.Geometry)FindResource(_cfg.LibraryGrid ? "IcoList" : "IcoGrid");
    }

    private void ClearRecent_Click(object sender, RoutedEventArgs e)
    {
        if (_anon) _anonRecent.Clear(); else RecentStore.Clear();
        RefreshPage();
    }

    private void ShuffleAll_Click(object sender, RoutedEventArgs e)
    {
        var files = _visibleEntries.Where(x => x.IsFile).ToList();
        if (files.Count == 0) return;

        _cfg.Shuffle = true;
        UpdateShuffleUi();
        _queue.Clear();
        foreach (var f in files) _queue.Add(new QueueItem(f.FullPath));
        PlayNormal(files[_rng.Next(files.Count)].FullPath);
    }

    // ------------------------------------------------------------------ "..." / right-click menu for a tile

    /// <summary>The "Add to" sub-menu: the play queue, a new playlist, or any existing playlist.</summary>
    private MenuItem AddToMenu(IReadOnlyList<string> paths)
    {
        PlaylistStore.EnsureLoaded();
        var addTo = MenuEntry("Add to", "IcoAdd", null);

        var queue = MenuEntry("Play queue", "IcoPlaylist", (_, _) => { foreach (var p in paths) Enqueue(p, next: false); });
        addTo.Items.Add(queue);
        addTo.Items.Add(new Separator());
        addTo.Items.Add(MenuEntry("New playlist", "IcoAdd", (_, _) =>
        {
            var name = Ask("New playlist", "", "Create");
            if (name != null) AddToPlaylist(PlaylistStore.Create(name), paths);
        }));
        foreach (var pl in PlaylistStore.All.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var target = pl;
            addTo.Items.Add(MenuEntry(pl.Name, "IcoPlaylist", (_, _) => AddToPlaylist(target, paths)));
        }
        return addTo;
    }

    private void ShowEntryMenu(BrowserEntry entry, UIElement? anchor)
    {
        var menu = new ContextMenu();
        if (anchor != null) { menu.PlacementTarget = anchor; menu.Placement = PlacementMode.Bottom; }
        else menu.Placement = PlacementMode.MousePoint;

        if (entry.IsPlaylist && entry.Playlist != null)
        {
            var pl = entry.Playlist;
            menu.Items.Add(MenuEntry("Open", "IcoPlaylist", (_, _) => OpenPlaylist(pl)));
            menu.Items.Add(MenuEntry("Play", "IcoPlay", (_, _) => PlayPlaylist(pl, false)));
            menu.Items.Add(MenuEntry("Shuffle play", "IcoShuffle", (_, _) => PlayPlaylist(pl, true)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry("Rename", "IcoEdit", (_, _) => RenamePlaylist(pl)));
            if (!_anon) menu.Items.Add(MenuEntry("Change cover picture", "IcoCamera", (_, _) => ChangeCover(pl)));
            menu.Items.Add(MenuEntry("Delete", "IcoTrash", (_, _) => DeletePlaylist(pl)));
        }
        else if (entry.IsAlbum || entry.IsArtist)
        {
            var songs = (entry.Children ?? new List<BrowserEntry>()).Where(c => c.IsFile).ToList();
            var paths = songs.Select(s => s.FullPath).ToList();
            menu.Items.Add(MenuEntry("Open", entry.IsAlbum ? "IcoDisc" : "IcoUser", (_, _) => OpenMusicScope(entry)));
            menu.Items.Add(MenuEntry("Play", "IcoPlay", (_, _) => PlayEntries(songs, 0)));
            menu.Items.Add(MenuEntry("Play next", "IcoNext", (_, _) => { foreach (var p in Enumerable.Reverse(paths)) Enqueue(p, next: true); }));
            menu.Items.Add(AddToMenu(paths));
        }
        else if (entry.IsDirectory)
        {
            menu.Items.Add(MenuEntry("Open", "IcoFolder", (_, _) => OpenFolderEntry(entry.FullPath)));
            menu.Items.Add(MenuEntry("Add folder to queue", "IcoAdd", (_, _) => EnqueueFolder(entry.FullPath)));
            if (_anon)
                menu.Items.Add(MenuEntry("Restore this folder", "IcoExport", (_, _) => _ = RestoreEntriesAsync(VaultItemsUnder(entry.FullPath))));
            if (!_anon)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(MenuEntry("Show in File Explorer", "IcoFolder", (_, _) => ShowInExplorer(entry.FullPath)));
            }
        }
        else
        {
            var path = entry.FullPath;
            menu.Items.Add(MenuEntry("Play", "IcoPlay", (_, _) => ActivateEntry(entry)));
            menu.Items.Add(MenuEntry("Play next", "IcoNext", (_, _) => Enqueue(path, next: true)));
            menu.Items.Add(AddToMenu(new[] { path }));
            if (_mode == LibMode.PlaylistDetail && _openPlaylist != null)
            {
                var pl = _openPlaylist;
                menu.Items.Add(MenuEntry("Remove from playlist", "IcoClose", (_, _) =>
                {
                    pl.Items.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                    PlaylistStore.Save();
                    RefreshPage();
                }));
            }
            else if (_mode == LibMode.Recent)
            {
                menu.Items.Add(MenuEntry("Remove from recent", "IcoClose", (_, _) =>
                {
                    if (_anon) _anonRecent.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
                    else RecentStore.Remove(path);
                    RefreshPage();
                }));
            }
            menu.Items.Add(new Separator());
            if (!_anon && entry.IsAudio)
                menu.Items.Add(MenuEntry("Generate lyrics", "IcoMusic", (_, _) => StartLyricsBatch(new[] { path })));
            menu.Items.Add(MenuEntry("Properties", "IcoInfo", (_, _) => ShowProperties(path)));
            if (entry.IsAudio && entry.Album.Length > 0)
            {
                menu.Items.Add(MenuEntry("Show album", "IcoDisc", (_, _) => ShowAlbumOrArtist(entry, album: true)));
                menu.Items.Add(MenuEntry("Show artist", "IcoUser", (_, _) => ShowAlbumOrArtist(entry, album: false)));
            }
            menu.Items.Add(MenuEntry("Select", "IcoCheck", (_, _) => entry.IsChecked = true));
            if (_anon)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(MenuEntry("Restore…", "IcoExport", (_, _) => _ = RestoreEntriesAsync(RestoreTargets(entry))));
                menu.Items.Add(MenuEntry("Delete permanently", "IcoTrash", (_, _) => _ = DeleteEntriesAsync(RestoreTargets(entry))));
            }
            else
            {
                menu.Items.Add(MenuEntry("Show in File Explorer", "IcoFolder", (_, _) => ShowInExplorer(path)));
            }
        }
        menu.IsOpen = true;
    }

    private static void ShowInExplorer(string path)
    {
        try
        {
            var arg = File.Exists(path) ? "/select,\"" + path + "\"" : "\"" + path + "\"";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", arg) { UseShellExecute = true });
        }
        catch { /* nothing to open */ }
    }

    private void ShowProperties(string path)
    {
        if (_anon)
        {
            if (!_anonItems.TryGetValue(path, out var vi)) return;
            _modalOpen = true;
            try { new PropertiesWindow(vi) { Owner = this }.ShowDialog(); }
            finally { _modalOpen = false; }
            return;
        }

        if (!File.Exists(path)) { ShowOsd("That file isn't there any more"); return; }

        string? length = null;
        if (_hasMedia && _queueIndex >= 0 && _queueIndex < _queue.Count &&
            string.Equals(_queue[_queueIndex].FullPath, path, StringComparison.OrdinalIgnoreCase) && _lengthMs > 0)
            length = Fmt(_lengthMs);
        else if (_musicTracks.FirstOrDefault(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase)) is { } track)
            length = track.Duration;

        _modalOpen = true;
        try { new PropertiesWindow(path, length) { Owner = this }.ShowDialog(); }
        finally { _modalOpen = false; }
    }
}
