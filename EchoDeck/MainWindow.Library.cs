using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace EchoDeck;

/// <summary>Library pages (Recent / Home / Videos / Music / Playlists), tiles and their menus.</summary>
public partial class MainWindow
{
    private enum LibMode { Recent, Home, Videos, Music, Playlists, PlaylistDetail, Blank }

    private LibMode _mode = LibMode.Recent;
    private List<BrowserEntry> _allEntries = new();
    private List<BrowserEntry> _visibleEntries = new();
    private BrowserEntry? _selectedEntry;
    private Playlist? _openPlaylist;
    private Action? _emptyAction;

    // ------------------------------------------------------------------ switching pages

    private void SetMode(LibMode mode)
    {
        _mode = mode;
        _navBack.Clear();
        _navForward.Clear();
        _openPlaylist = null;
        _currentDir = null;
        if (SearchBox.Text.Length > 0) SearchBox.Text = "";

        switch (mode)
        {
            case LibMode.Home: _typeFilter = 0; GoRoot(); return;
            case LibMode.Videos: _typeFilter = 1; GoRoot(); return;
            case LibMode.Music: _typeFilter = 2; GoRoot(); return;
            case LibMode.Recent: BuildRecent(); break;
            case LibMode.Playlists: BuildPlaylists(); break;
            case LibMode.Blank:
                _allEntries = new List<BrowserEntry>();
                break;
        }
        FinishPage();
    }

    private void GoRoot()
    {
        var root = RootFolder();
        if (Directory.Exists(root)) NavigateTo(root, record: false);
        else { _allEntries = new List<BrowserEntry>(); FinishPage(); }
    }

    private void FinishPage()
    {
        PlHeader.Visibility = _mode == LibMode.PlaylistDetail ? Visibility.Visible : Visibility.Collapsed;
        NewPlaylistButton.Visibility = _mode == LibMode.Playlists ? Visibility.Visible : Visibility.Collapsed;
        ClearRecentButton.Visibility = _mode == LibMode.Recent && _allEntries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        LibPathText.Text = _mode switch
        {
            LibMode.Recent => "Recent",
            LibMode.Playlists => "Playlists",
            LibMode.PlaylistDetail => _openPlaylist?.Name ?? "Playlist",
            LibMode.Blank => "EchoDeck",
            _ => _currentDir != null ? BuildBreadcrumb(_currentDir) : "Home"
        };

        UpdateNavButtons();
        RebuildRows();
        if (_libraryVisible) HighlightNav();
        GC.Collect(1);
    }

    private void HighlightNav()
    {
        RadioButton? target = _mode switch
        {
            LibMode.Recent => NavRecent,
            LibMode.Home => NavHome,
            LibMode.Videos => NavVideos,
            LibMode.Music => NavMusic,
            LibMode.Playlists or LibMode.PlaylistDetail => NavPlaylists,
            _ => null
        };
        SetNavChecked(target);
    }

    /// <summary>Exactly one rail item is highlighted: the page that is open (none while a video is showing).</summary>
    private void SetNavChecked(RadioButton? target)
    {
        foreach (var nav in new[] { NavRecent, NavHome, NavVideos, NavMusic, NavPlaylists })
            nav.IsChecked = ReferenceEquals(nav, target);
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _vault != null) return;

        var mode = ReferenceEquals(sender, NavRecent) ? LibMode.Recent
                 : ReferenceEquals(sender, NavVideos) ? LibMode.Videos
                 : ReferenceEquals(sender, NavMusic) ? LibMode.Music
                 : ReferenceEquals(sender, NavPlaylists) ? LibMode.Playlists
                 : LibMode.Home;

        ShowLibrary(true);
        SetMode(mode);
    }

    // ------------------------------------------------------------------ page content

    private void BuildRecent()
    {
        var list = new List<BrowserEntry>();
        foreach (var path in RecentStore.Items.Where(File.Exists))
        {
            var kind = MediaTypes.IsAudio(path) ? EntryKind.Audio : EntryKind.Video;
            list.Add(new BrowserEntry(Path.GetFileNameWithoutExtension(path), path, kind,
                Path.GetFileName(Path.GetDirectoryName(path) ?? "")));
        }
        _allEntries = list;
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
        _allEntries = pl.Items.Where(File.Exists).Select(p =>
            new BrowserEntry(Path.GetFileNameWithoutExtension(p), p,
                MediaTypes.IsAudio(p) ? EntryKind.Audio : EntryKind.Video,
                Path.GetFileName(Path.GetDirectoryName(p) ?? ""))).ToList();

        PlName.Text = pl.Name;
        PlInfo.Text = ItemCount(_allEntries.Count);
        ShowCover(pl);
    }

    private static string ItemCount(int n) => n == 1 ? "1 item" : $"{n} items";

    private void ShowCover(Playlist pl)
    {
        PlCoverBrush.ImageSource = LoadImage(pl.Cover);
    }

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

    // ------------------------------------------------------------------ browsing folders (Home / Videos / Music)

    private void NavigateTo(string dir, bool record = true)
    {
        var list = new List<BrowserEntry>();
        try
        {
            var dirs = Directory.EnumerateDirectories(dir).OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToList();
            var files = Directory.EnumerateFiles(dir)
                .Where(f => MediaTypes.IsSupportedExtension(Path.GetExtension(f)))
                .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (_sortDesc) { dirs.Reverse(); files.Reverse(); }

            foreach (var d in dirs)
            {
                var attr = File.GetAttributes(d);
                if ((attr & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                list.Add(new BrowserEntry(Path.GetFileName(d), d, EntryKind.Folder, "Folder"));
            }
            foreach (var f in files)
            {
                long size = 0;
                try { size = new FileInfo(f).Length; } catch { /* unreadable */ }
                list.Add(new BrowserEntry(Path.GetFileNameWithoutExtension(f), f,
                    MediaTypes.IsAudio(f) ? EntryKind.Audio : EntryKind.Video, VaultService.FormatSize(size)));
            }
        }
        catch (Exception)
        {
            return; // folder vanished or access denied: stay where we are
        }

        if (_mode is not (LibMode.Home or LibMode.Videos or LibMode.Music))
            _mode = _typeFilter == 1 ? LibMode.Videos : _typeFilter == 2 ? LibMode.Music : LibMode.Home;

        if (record && _currentDir != null && !string.Equals(_currentDir, dir, StringComparison.OrdinalIgnoreCase))
        {
            _navBack.Push(_currentDir);
            _navForward.Clear();
        }

        _currentDir = dir;
        _allEntries = list;
        BreadcrumbText.Text = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : dir;
        FinishPage();
    }

    private string BuildBreadcrumb(string dir)
    {
        string head = _typeFilter == 1 ? "Videos" : _typeFilter == 2 ? "Music" : "Home";
        var root = RootFolder().TrimEnd(Path.DirectorySeparatorChar);
        var full = dir.TrimEnd(Path.DirectorySeparatorChar);

        if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && full.Length > root.Length)
        {
            var rel = full.Substring(root.Length).Trim(Path.DirectorySeparatorChar);
            return head + "  ›  " + string.Join("  ›  ", rel.Split(Path.DirectorySeparatorChar));
        }
        return full.Equals(root, StringComparison.OrdinalIgnoreCase) ? head : head + "  ›  " + Path.GetFileName(full);
    }

    private void UpdateNavButtons()
    {
        bool folderMode = _mode is LibMode.Home or LibMode.Videos or LibMode.Music;
        LibBackButton.IsEnabled = folderMode ? _navBack.Count > 0 : _mode == LibMode.PlaylistDetail;
        LibForwardButton.IsEnabled = folderMode && _navForward.Count > 0;
        LibBackButton.Opacity = LibBackButton.IsEnabled ? 1 : 0.4;
        LibForwardButton.Opacity = LibForwardButton.IsEnabled ? 1 : 0.4;
    }

    private void LibBack_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == LibMode.PlaylistDetail) { SetMode(LibMode.Playlists); return; }
        if (_navBack.Count == 0 || _currentDir == null) return;
        _navForward.Push(_currentDir);
        NavigateTo(_navBack.Pop(), record: false);
    }

    private void LibForward_Click(object sender, RoutedEventArgs e)
    {
        if (_navForward.Count == 0 || _currentDir == null) return;
        _navBack.Push(_currentDir);
        NavigateTo(_navForward.Pop(), record: false);
    }

    // ------------------------------------------------------------------ rows (four tiles per row)

    private void RebuildRows()
    {
        var text = SearchBox.Text.Trim();
        IEnumerable<BrowserEntry> query = _allEntries;
        if (text.Length > 0)
            query = query.Where(x => x.Name.IndexOf(text, StringComparison.CurrentCultureIgnoreCase) >= 0);
        if (_mode is LibMode.Home or LibMode.Videos or LibMode.Music && _typeFilter != 0)
            query = query.Where(x => x.IsDirectory || (_typeFilter == 1 ? !x.IsAudio : x.IsAudio));

        _visibleEntries = query.ToList();
        _selectedEntry = null;

        bool grid = _cfg.LibraryGrid;
        var rows = new List<EntryRow>();
        if (grid)
        {
            for (int i = 0; i < _visibleEntries.Count; i += 4)
                rows.Add(new EntryRow(_visibleEntries.Skip(i).Take(4).ToList()));
        }
        else
        {
            foreach (var entry in _visibleEntries) rows.Add(new EntryRow(new List<BrowserEntry> { entry }));
        }

        LibraryList.ItemTemplate = (DataTemplate)FindResource(grid ? "RowGridTpl" : "RowListTpl");
        LibraryList.ItemsSource = rows;

        CountText.Text = ItemCount(_visibleEntries.Count);
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        UpdateEmptyState();
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
        if (searching)
        {
            message = "No matches for “" + SearchBox.Text.Trim() + "”.";
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
                    message = "This playlist is empty. Use the ··· menu on any song or video and pick “Add to playlist”.";
                    break;
                case LibMode.Blank:
                    message = "Pick Recent, Home, Videos or Music on the left to get started.";
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
            case LibMode.Home or LibMode.Videos or LibMode.Music:
                if (_currentDir != null) NavigateTo(_currentDir, record: false);
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
        RecentStore.Clear();
        RefreshPage();
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
        else ActivateEntry(entry);
    }

    private void TileMore_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is not { } entry) return;
        ShowEntryMenu(entry, sender as UIElement);
    }

    /// <summary>Double-click / play: folders and playlists open, files play with the visible files as the queue.</summary>
    private void ActivateEntry(BrowserEntry entry)
    {
        if (_vault != null) return;

        if (entry.IsDirectory) { NavigateTo(entry.FullPath); return; }
        if (entry.IsPlaylist && entry.Playlist != null) { OpenPlaylist(entry.Playlist); return; }
        if (!entry.IsFile) return;

        _queue.Clear();
        foreach (var f in _visibleEntries.Where(x => x.IsFile)) _queue.Add(new QueueItem(f.FullPath));
        PlayNormal(entry.FullPath);
        RootGrid.Focus();
    }

    // ------------------------------------------------------------------ "..." / right-click menu for a tile

    private void ShowEntryMenu(BrowserEntry entry, UIElement? anchor)
    {
        if (_vault != null) return;

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
            menu.Items.Add(MenuEntry("Change cover picture", "IcoCamera", (_, _) => ChangeCover(pl)));
            menu.Items.Add(MenuEntry("Delete", "IcoTrash", (_, _) => DeletePlaylist(pl)));
        }
        else if (entry.IsDirectory)
        {
            menu.Items.Add(MenuEntry("Open", "IcoFolder", (_, _) => NavigateTo(entry.FullPath)));
            menu.Items.Add(MenuEntry("Add folder to queue", "IcoAdd", (_, _) => EnqueueFolder(entry.FullPath)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry("Show in File Explorer", "IcoFolder", (_, _) => ShowInExplorer(entry.FullPath)));
        }
        else
        {
            var path = entry.FullPath;
            menu.Items.Add(MenuEntry("Play", "IcoPlay", (_, _) => ActivateEntry(entry)));
            menu.Items.Add(MenuEntry("Play next", "IcoNext", (_, _) => Enqueue(path, next: true)));
            menu.Items.Add(MenuEntry("Add to queue", "IcoAdd", (_, _) => Enqueue(path, next: false)));
            menu.Items.Add(MenuEntry("Add to playlist…", "IcoPlaylist", (_, _) => ShowPlaylistPicker(new[] { path }, anchor)));
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
                    RecentStore.Remove(path);
                    RefreshPage();
                }));
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry("Properties", "IcoInfo", (_, _) => ShowProperties(path)));
            menu.Items.Add(MenuEntry("Show in File Explorer", "IcoFolder", (_, _) => ShowInExplorer(path)));
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
        try
        {
            var info = new FileInfo(path);
            string duration = "";
            if (_hasMedia && _queueIndex >= 0 && _queueIndex < _queue.Count &&
                string.Equals(_queue[_queueIndex].FullPath, path, StringComparison.OrdinalIgnoreCase) && _lengthMs > 0)
                duration = "\nDuration:  " + Fmt(_lengthMs);

            MessageBox.Show(this,
                $"Name:  {info.Name}\nFolder:  {info.DirectoryName}\nSize:  {VaultService.FormatSize(info.Length)}\n" +
                $"Modified:  {info.LastWriteTime:g}\nType:  {(MediaTypes.IsAudio(path) ? "Audio" : "Video")} ({info.Extension}){duration}",
                "Properties", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Properties", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ------------------------------------------------------------------ queue helpers

    private void Enqueue(string path, bool next)
    {
        if (_vault != null || !MediaTypes.IsSupportedFile(path)) return;

        var existing = _queue.FindIndex(q => string.Equals(q.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0 && next)
        {
            var item = _queue[existing];
            _queue.RemoveAt(existing);
            if (existing < _queueIndex) _queueIndex--;
            _queue.Insert(Math.Min(_queue.Count, _queueIndex + 1), item);
        }
        else if (existing < 0)
        {
            _queue.Insert(next ? Math.Min(_queue.Count, _queueIndex + 1) : _queue.Count, new QueueItem(path));
        }

        RefreshSide();
        UpdateTransportEnabled();
        ShowOsd(next ? "Plays next" : "Added to queue");
    }

    private void EnqueueFolder(string folder)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(folder).Where(f => MediaTypes.IsSupportedExtension(Path.GetExtension(f)))
                         .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase))
                Enqueue(f, next: false);
        }
        catch { /* unreadable folder */ }
    }

    // ------------------------------------------------------------------ playlists

    private string? Ask(string title, string initial = "", string ok = "OK")
    {
        _modalOpen = true;
        try
        {
            var dlg = new TextPromptWindow(title, initial, ok) { Owner = this };
            return dlg.ShowDialog() == true ? dlg.Value : null;
        }
        finally { _modalOpen = false; }
    }

    private void NewPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var name = Ask("New playlist", "", "Create");
        if (name == null) return;

        var pl = PlaylistStore.Create(name);
        if (_libraryVisible && _mode == LibMode.Playlists) RefreshPage();
        else if (_sidebarVisible) RefreshSide();
        ShowOsd("Playlist created");
        _ = pl;
    }

    private void AddToPlaylist(Playlist pl, IEnumerable<string> paths)
    {
        int added = 0;
        foreach (var p in paths)
        {
            if (!MediaTypes.IsSupportedFile(p)) continue;
            if (pl.Items.Any(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase))) continue;
            pl.Items.Add(p);
            added++;
        }
        if (added > 0) PlaylistStore.Save();
        RefreshPage();
        RefreshSide();
        ShowOsd(added > 0 ? $"Added to “{pl.Name}”" : "Already in the playlist");
    }

    private void ShowPlaylistPicker(IEnumerable<string> paths, UIElement? anchor)
    {
        PlaylistStore.EnsureLoaded();
        var list = paths.ToList();
        var menu = new ContextMenu();
        if (anchor != null) { menu.PlacementTarget = anchor; menu.Placement = PlacementMode.Bottom; }
        else menu.Placement = PlacementMode.MousePoint;

        foreach (var pl in PlaylistStore.All.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var target = pl;
            menu.Items.Add(MenuEntry(pl.Name, "IcoPlaylist", (_, _) => AddToPlaylist(target, list)));
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("New playlist…", "IcoAdd", (_, _) =>
        {
            var name = Ask("New playlist", "", "Create");
            if (name == null) return;
            AddToPlaylist(PlaylistStore.Create(name), list);
        }));
        menu.IsOpen = true;
    }

    private void PlayPlaylist(Playlist pl, bool shuffle)
    {
        var files = pl.Items.Where(File.Exists).ToList();
        if (files.Count == 0) { ShowOsd("This playlist is empty"); return; }

        _queue.Clear();
        foreach (var f in files) _queue.Add(new QueueItem(f));

        if (shuffle)
        {
            _cfg.Shuffle = true;
            UpdateShuffleUi();
        }
        PlayNormal(files[shuffle ? _rng.Next(files.Count) : 0]);
    }

    private Playlist? PlaylistFor(object sender) =>
        (sender as FrameworkElement)?.Tag as string == "side" ? _sidePlaylist : _openPlaylist ?? _sidePlaylist;

    private void PlPlay_Click(object sender, RoutedEventArgs e)
    {
        if (PlaylistFor(sender) is { } pl) PlayPlaylist(pl, _cfg.Shuffle);
    }

    private void PlRename_Click(object sender, RoutedEventArgs e)
    {
        if (PlaylistFor(sender) is { } pl) RenamePlaylist(pl);
    }

    private void PlCover_Click(object sender, RoutedEventArgs e)
    {
        if (PlaylistFor(sender) is { } pl) ChangeCover(pl);
    }

    private void PlDelete_Click(object sender, RoutedEventArgs e)
    {
        if (PlaylistFor(sender) is { } pl) DeletePlaylist(pl);
    }

    private void RenamePlaylist(Playlist pl)
    {
        var name = Ask("Rename playlist", pl.Name, "Rename");
        if (name == null) return;
        pl.Name = name;
        PlaylistStore.Save();
        RefreshPage();
        RefreshSide();
    }

    private void ChangeCover(Playlist pl)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose a cover picture",
            Filter = "Pictures|*.jpg;*.jpeg;*.png;*.bmp;*.gif|All files|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            Directory.CreateDirectory(PlaylistStore.CoversFolder);
            var dest = Path.Combine(PlaylistStore.CoversFolder, pl.Id + Path.GetExtension(dlg.FileName));
            File.Copy(dlg.FileName, dest, overwrite: true);
            pl.Cover = dest;
            PlaylistStore.Save();
            RefreshPage();
            RefreshSide();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't set the cover: " + ex.Message, AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DeletePlaylist(Playlist pl)
    {
        var answer = MessageBox.Show(this, "Delete the playlist “" + pl.Name + "”? The files themselves are not touched.",
            "Delete playlist", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        PlaylistStore.Delete(pl);
        if (_sidePlaylist == pl) _sidePlaylist = null;
        if (_openPlaylist == pl) _openPlaylist = null;
        if (_mode == LibMode.PlaylistDetail) SetMode(LibMode.Playlists);
        else RefreshPage();
        RefreshSide();
    }

    // ------------------------------------------------------------------ queue / playlist panel (Q and P)

    private enum SideMode { Queue, Playlists }

    private SideMode _sideMode = SideMode.Queue;
    private Playlist? _sidePlaylist;
    private bool _sideSortDesc;
    private readonly ObservableCollection<BrowserEntry> _sideItems = new();

    private void SideTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _sideMode = SideTabPlaylists.IsChecked == true ? SideMode.Playlists : SideMode.Queue;
        if (_sideMode == SideMode.Queue) _sidePlaylist = null;
        if (SideSearch.Text.Length > 0) SideSearch.Text = "";
        RefreshSide();
    }

    private void SideSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        RefreshSide();
    }

    private void SideSort_Click(object sender, RoutedEventArgs e)
    {
        _sideSortDesc = !_sideSortDesc;

        if (_sideMode == SideMode.Queue)
        {
            string? current = _queueIndex >= 0 && _queueIndex < _queue.Count ? _queue[_queueIndex].FullPath : null;
            var sorted = (_sideSortDesc
                ? _queue.OrderByDescending(q => q.Name, StringComparer.CurrentCultureIgnoreCase)
                : _queue.OrderBy(q => q.Name, StringComparer.CurrentCultureIgnoreCase)).ToList();
            _queue.Clear();
            _queue.AddRange(sorted);
            _queueIndex = current == null ? -1 : _queue.FindIndex(q => string.Equals(q.FullPath, current, StringComparison.OrdinalIgnoreCase));
        }
        else if (_sidePlaylist != null)
        {
            bool saved = _sortDesc;
            _sortDesc = _sideSortDesc;
            SortPlaylistItems(_sidePlaylist);
            _sortDesc = saved;
        }
        RefreshSide();
    }

    private void RefreshSide()
    {
        if (!IsLoaded) return;
        var text = SideSearch.Text.Trim();
        SideSearchHint.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var items = new List<BrowserEntry>();
        string emptyText, emptyButton;

        if (_sideMode == SideMode.Queue)
        {
            foreach (var q in _queue)
                items.Add(new BrowserEntry(Path.GetFileNameWithoutExtension(q.FullPath), q.FullPath,
                    MediaTypes.IsAudio(q.FullPath) ? EntryKind.Audio : EntryKind.Video,
                    Path.GetFileName(Path.GetDirectoryName(q.FullPath) ?? "")));
            emptyText = "Your queue is empty.";
            emptyButton = "Add to queue";
            SideAddButton.ToolTip = "Add to queue";
            SidePlHeader.Visibility = Visibility.Collapsed;
        }
        else if (_sidePlaylist == null)
        {
            PlaylistStore.EnsureLoaded();
            var lists = PlaylistStore.All.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (_sideSortDesc) lists.Reverse();
            foreach (var p in lists)
                items.Add(new BrowserEntry(p.Name, p.Id, EntryKind.Playlist, ItemCount(p.Items.Count), p));
            emptyText = "You haven't created a playlist yet.";
            emptyButton = "Create playlist";
            SideAddButton.ToolTip = "New playlist";
            SidePlHeader.Visibility = Visibility.Collapsed;
        }
        else
        {
            foreach (var path in _sidePlaylist.Items.Where(File.Exists))
                items.Add(new BrowserEntry(Path.GetFileNameWithoutExtension(path), path,
                    MediaTypes.IsAudio(path) ? EntryKind.Audio : EntryKind.Video,
                    Path.GetFileName(Path.GetDirectoryName(path) ?? "")));
            emptyText = "This playlist is empty.";
            emptyButton = "Add files";
            SideAddButton.ToolTip = "Add files to this playlist";
            SidePlHeader.Visibility = Visibility.Visible;
            SidePlName.Text = _sidePlaylist.Name;
            SidePlInfo.Text = ItemCount(items.Count);
        }

        if (text.Length > 0)
            items = items.Where(x => x.Name.IndexOf(text, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();

        _sideItems.Clear();
        foreach (var item in items) _sideItems.Add(item);
        SideList.ItemsSource = _sideItems;

        bool empty = _sideItems.Count == 0;
        SideEmptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        SideEmptyHint.Text = text.Length > 0 ? "No matches." : emptyText;
        SideEmptyButton.Content = emptyButton;
        SideEmptyButton.Visibility = text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SideAdd_Click(object sender, RoutedEventArgs e)
    {
        if (_sideMode == SideMode.Queue) { AddFiles_Click(sender, e); return; }
        if (_sidePlaylist == null) { NewPlaylist_Click(sender, e); return; }

        var dlg = new OpenFileDialog { Title = "Add to playlist", Filter = MediaTypes.DialogFilter, Multiselect = true };
        if (dlg.ShowDialog(this) == true) AddToPlaylist(_sidePlaylist, dlg.FileNames);
    }

    private void SidePlBack_Click(object sender, RoutedEventArgs e)
    {
        _sidePlaylist = null;
        RefreshSide();
    }

    private void SideList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SideList.SelectedItem is not BrowserEntry entry || _vault != null) return;

        if (entry.IsPlaylist && entry.Playlist != null)
        {
            _sidePlaylist = entry.Playlist;
            if (SideSearch.Text.Length > 0) SideSearch.Text = "";
            RefreshSide();
            return;
        }

        if (_sideMode == SideMode.Playlists && _sidePlaylist != null)
        {
            _queue.Clear();
            foreach (var p in _sidePlaylist.Items.Where(File.Exists)) _queue.Add(new QueueItem(p));
        }
        PlayNormal(entry.FullPath);
        RootGrid.Focus();
    }

    private void SideItem_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (EntryOf(sender) is not { } entry || _vault != null) return;
        e.Handled = true;

        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        if (entry.IsPlaylist && entry.Playlist != null)
        {
            var pl = entry.Playlist;
            menu.Items.Add(MenuEntry("Open", "IcoPlaylist", (_, _) => { _sidePlaylist = pl; RefreshSide(); }));
            menu.Items.Add(MenuEntry("Play", "IcoPlay", (_, _) => PlayPlaylist(pl, false)));
            menu.Items.Add(MenuEntry("Rename", "IcoEdit", (_, _) => RenamePlaylist(pl)));
            menu.Items.Add(MenuEntry("Delete", "IcoTrash", (_, _) => DeletePlaylist(pl)));
        }
        else
        {
            var path = entry.FullPath;
            menu.Items.Add(MenuEntry("Play", "IcoPlay", (_, _) => PlayNormal(path)));
            menu.Items.Add(MenuEntry("Add to playlist…", "IcoPlaylist", (_, _) => ShowPlaylistPicker(new[] { path }, null)));
            if (_sideMode == SideMode.Queue)
            {
                menu.Items.Add(MenuEntry("Remove from queue", "IcoClose", (_, _) =>
                {
                    int idx = _queue.FindIndex(q => string.Equals(q.FullPath, path, StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0)
                    {
                        _queue.RemoveAt(idx);
                        if (idx < _queueIndex) _queueIndex--;
                    }
                    RefreshSide();
                    UpdateTransportEnabled();
                }));
            }
            else if (_sidePlaylist != null)
            {
                var pl = _sidePlaylist;
                menu.Items.Add(MenuEntry("Add to queue", "IcoAdd", (_, _) => Enqueue(path, next: false)));
                menu.Items.Add(MenuEntry("Remove from playlist", "IcoClose", (_, _) =>
                {
                    pl.Items.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                    PlaylistStore.Save();
                    RefreshSide();
                    RefreshPage();
                }));
            }
            menu.Items.Add(MenuEntry("Properties", "IcoInfo", (_, _) => ShowProperties(path)));
        }
        menu.IsOpen = true;
    }

    /// <summary>Q / P: show the queue or the playlists panel (press again to hide it).</summary>
    private void ShowSide(SideMode mode)
    {
        if (_libraryVisible) ShowLibrary(false);

        if (_sidebarVisible && _sideMode == mode)
        {
            SetSidebarVisible(false);
            return;
        }

        if (mode == SideMode.Queue) SideTabQueue.IsChecked = true; else SideTabPlaylists.IsChecked = true;
        _sideMode = mode;
        RefreshSide();
        SetSidebarVisible(true);
    }

    private void SidebarToggle_Click(object sender, RoutedEventArgs e)
    {
        bool on = (sender as ToggleButton)?.IsChecked == true;
        if (_libraryVisible) ShowLibrary(false);
        if (on) RefreshSide();
        SetSidebarVisible(on);
    }

    private void SetSidebarVisible(bool visible)
    {
        _sidebarVisible = visible;
        TopSidebarToggle.IsChecked = visible;
        ControlsPlaylistToggle.IsChecked = visible;
        SidebarPanel.Visibility = visible && !_isFullscreen && _vault == null && !_libraryVisible
            ? Visibility.Visible : Visibility.Collapsed;
        SchedulePositionVault();
    }

    // ------------------------------------------------------------------ queue navigation

    private void PlayAdjacent(int delta)
    {
        if (_vault != null || _queue.Count == 0) return;

        if (_cfg.Shuffle && _queue.Count > 1)
        {
            int pick;
            do { pick = _rng.Next(_queue.Count); } while (pick == _queueIndex);
            PlayNormal(_queue[pick].FullPath);
            return;
        }

        int i = _queueIndex + delta;
        if (i < 0 || i >= _queue.Count) return;
        PlayNormal(_queue[i].FullPath);
    }

    private void OnEndReached()
    {
        ResetTransport();
        if (_vault != null) return; // a vault item that ends never touches the playlist

        if (_cfg.Loop == LoopMode.One && _queueIndex >= 0 && _queueIndex < _queue.Count)
        {
            PlayNormal(_queue[_queueIndex].FullPath);
            return;
        }

        if (_cfg.Shuffle && _queue.Count > 1) { PlayAdjacent(+1); return; }
        if (!_cfg.AutoAdvance && _cfg.Loop != LoopMode.All) return;

        if (_queueIndex >= 0 && _queueIndex + 1 < _queue.Count)
            PlayNormal(_queue[_queueIndex + 1].FullPath);
        else if (_cfg.Loop == LoopMode.All && _queue.Count > 0)
            PlayNormal(_queue[0].FullPath);
    }
}
