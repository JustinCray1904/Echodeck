using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace EchoDeck;

/// <summary>Library pages (Recent / Home / Videos / Music / Playlists), tiles, selection and their menus.</summary>
public partial class MainWindow
{
    private enum LibMode { Recent, Home, Videos, Music, Playlists, PlaylistDetail, Blank }

    private LibMode _mode = LibMode.Recent;
    private List<BrowserEntry> _allEntries = new();
    private List<BrowserEntry> _visibleEntries = new();
    private BrowserEntry? _selectedEntry;
    private Playlist? _openPlaylist;
    private Action? _emptyAction;
    private string? _loadingText;

    // Music page: 0 = songs, 1 = albums, 2 = artists, optionally scoped to one album / artist
    private int _musicTab;
    private int _musicTabBeforeScope;
    private BrowserEntry? _musicScope;
    private List<Track> _musicTracks = new();
    private bool _musicFromCache;   // _musicTracks came from the on-disk cache and a fresh scan is still due
    private bool _suppressTab;
    private int _pageToken;      // guards against a slow scan finishing after the user has moved on

    private void InitLibrary()
    {
        SelectionState.Instance.PropertyChanged += (_, _) => UpdateSelectionBar();
        SmoothScroll.Attach(LibraryList);
        SmoothScroll.Attach(SideList);
        LibraryList.SizeChanged += LibraryList_SizeChanged;
        Visualizer.CoverPulse = CoverScale;   // the cover swells a little with the bass

        // the lyrics box follows the room left beside the cover when the window or the side panels change
        MusicPanel.SizeChanged += (_, _) =>
        {
            if (_lyricsOpen && !UiAnim.IsRunning(LyricsCol))
            {
                double column = ComputeLyricsColumn();
                LyricsCol.Width = new GridLength(column);
                LyricsPanel.Width = column - LyricsPanelMargin;
            }
        };
        LibraryScanner.Progress += p => Dispatcher.InvokeAsync(() => OnScanProgress(p));
        LibraryScanner.Notice += message => Dispatcher.InvokeAsync(() => Notify(message));
    }

    // ------------------------------------------------------------------ roots

    private List<string> HomeRoots() =>
        _cfg.VideoFolders.Concat(_cfg.MusicFolders).Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private string RootFolder()
    {
        var roots = HomeRoots();
        return roots.Count > 0 ? roots[0] : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    }

    // ------------------------------------------------------------------ switching pages

    private void SetMode(LibMode mode)
    {
        _mode = mode;
        _pageToken++;
        _navBack.Clear();
        _navForward.Clear();
        _openPlaylist = null;
        _currentDir = null;
        _musicScope = null;
        _anonSubDir = "";
        _anonSubBack.Clear();
        _loadingText = null;
        HideLoading();
        if (SearchBox.Text.Length > 0) SearchBox.Text = "";

        switch (mode)
        {
            case LibMode.Home: GoRoot(); return;
            case LibMode.Videos: BuildVideos(); return;
            case LibMode.Music: BuildMusic(); return;
            case LibMode.Recent: BuildRecent(); break;
            case LibMode.Playlists: BuildPlaylists(); break;
            case LibMode.Blank: _allEntries = new List<BrowserEntry>(); break;
        }
        FinishPage();
    }

    private void GoRoot()
    {
        if (_anon) { GoRootAnon(); return; }
        var roots = HomeRoots();
        _currentDir = null;
        if (roots.Count == 1) { NavigateTo(roots[0], record: false); return; }

        // several library folders: show them as folder cards
        _allEntries = roots.Select(r =>
            new BrowserEntry(Path.GetFileName(r.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : r, r,
                EntryKind.Folder, r)).ToList();
        BreadcrumbText.Text = "Library";
        FinishPage();
    }

    private void FinishPage()
    {
        PlHeader.Visibility = _mode == LibMode.PlaylistDetail ? Visibility.Visible : Visibility.Collapsed;
        NewPlaylistButton.Visibility = _mode == LibMode.Playlists ? Visibility.Visible : Visibility.Collapsed;
        ClearRecentButton.Visibility = _mode == LibMode.Recent && _allEntries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShuffleAllButton.Visibility = (_mode == LibMode.Music && _musicTab == 0) || _mode == LibMode.Videos
            ? Visibility.Visible : Visibility.Collapsed;
        MusicTabs.Visibility = _mode == LibMode.Music && _musicScope == null ? Visibility.Visible : Visibility.Collapsed;

        LibPathText.Text = _mode switch
        {
            LibMode.Recent => "Recent",
            LibMode.Playlists => "Playlists",
            LibMode.PlaylistDetail => _openPlaylist?.Name ?? "Playlist",
            LibMode.Videos => AnonFolderPage && _anonSubDir.Length > 0 ? "Videos  ›  " + LastSegment(_anonSubDir) : "Videos",
            LibMode.Music => _musicScope != null ? _musicScope.Name : AnonFolderPage && _anonSubDir.Length > 0 ? "Music  ›  " + LastSegment(_anonSubDir) : "Music",
            LibMode.Blank => "Tempo",
            _ => _currentDir != null ? BuildBreadcrumb(_currentDir) : "Home"
        };

        bool canToggleView = _mode is LibMode.Home or LibMode.Videos or LibMode.Recent || AnonFolderPage;
        ViewToggleButton.Visibility = canToggleView ? Visibility.Visible : Visibility.Collapsed;

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
        if (!IsLoaded) return;

        var mode = ReferenceEquals(sender, NavRecent) ? LibMode.Recent
                 : ReferenceEquals(sender, NavVideos) ? LibMode.Videos
                 : ReferenceEquals(sender, NavMusic) ? LibMode.Music
                 : ReferenceEquals(sender, NavPlaylists) ? LibMode.Playlists
                 : LibMode.Home;

        ShowLibrary(true);
        SetMode(mode);
    }

    /// <summary>The icon at the top-left only shows / hides the navigation. The queue and playlists open with Q and P.</summary>
    private void RailToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_libraryVisible) { ApplyRail(); return; }   // the pages always keep their side bar
        if (PanelsBusy) { TopSidebarToggle.IsChecked = !_railHidden; return; }
        _railHidden = TopSidebarToggle.IsChecked != true;
        ApplyRail();
    }

    /// <summary>B: show / hide the side bar next to the player. On the Recent, Home, Videos, Music and Playlists pages it stays.</summary>
    /// <summary>A side panel is mid-transition: key presses are ignored until it settles, so mashing the shortcuts can't stack transitions.</summary>
    private bool PanelsBusy => UiAnim.IsRunning(SidebarPanel) || UiAnim.IsRunning(Rail);

    private void ToggleRail()
    {
        if (_libraryVisible || _isFullscreen || PanelsBusy) return;
        _railHidden = !_railHidden;
        ApplyRail();
    }

    private bool _railHidden;
    private readonly Thickness _railMargin = new(0, 0, 8, 0);

    private double RailWidth => _libraryVisible ? 236 : 56;

    private void ApplyRail(bool animate = true)
    {
        // On the library pages the side bar is always there; only next to the player can it be collapsed.
        bool collapsed = (_railHidden && !_libraryVisible) || ChromeHidden;
        TopSidebarToggle.IsEnabled = !_libraryVisible;
        TopSidebarToggle.IsChecked = !(_railHidden && !_libraryVisible);

        if (!animate)
        {
            UiAnim.Cancel(Rail);
            Rail.Width = RailWidth;
            Rail.Margin = _railMargin;
            Rail.Opacity = 1;
            Rail.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            return;
        }

        if (collapsed)
        {
            UiAnim.SlidePanel(Rail, false, RailWidth, _railMargin, SmoothPanels, CurtainOn, CurtainOff);
        }
        else if (Rail.Visibility == Visibility.Visible)
        {
            // already showing: switch between the wide (pages) and narrow (player) side bar in one step, so the
            // video window next to it is laid out once instead of on every frame
            UiAnim.Cancel(Rail);
            Rail.Width = RailWidth;
            Rail.Opacity = 1;
        }
        else
        {
            UiAnim.SlidePanel(Rail, true, RailWidth, _railMargin, SmoothPanels, CurtainOn, CurtainOff);
        }
    }

    /// <summary>The queue / playlists panel next to the player: slides open and shut.</summary>
    private void ApplySidebar(bool animate = true)
    {
        bool show = _sidebarVisible && !ChromeHidden && !_libraryVisible;
        var margin = new Thickness(0, 0, 8, 0);
        if (!animate)
        {
            UiAnim.Cancel(SidebarPanel);
            SidebarPanel.Width = SidebarWidth;
            SidebarPanel.Margin = margin;
            SidebarPanel.Opacity = 1;
            SidebarPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        UiAnim.SlidePanel(SidebarPanel, show, SidebarWidth, margin, SmoothPanels, CurtainOn, CurtainOff);
    }

    // ------------------------------------------------------------------ browsing folders (Home)

    private void NavigateTo(string dir, bool record = true)
    {
        if (_anon) { NavigateVirtual(dir, record); return; }

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
                list.Add(new BrowserEntry(DisplayNames.Clean(Path.GetFileNameWithoutExtension(f)), f,
                    MediaTypes.IsAudio(f) ? EntryKind.Audio : EntryKind.Video, VaultService.FormatSize(size)));
            }
        }
        catch (Exception)
        {
            return; // folder vanished or access denied: stay where we are
        }

        _mode = LibMode.Home;
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
        if (_anon) return VirtualBreadcrumb(dir);

        var full = dir.TrimEnd(Path.DirectorySeparatorChar);
        var root = HomeRoots()
            .Select(r => r.TrimEnd(Path.DirectorySeparatorChar))
            .Where(r => full.StartsWith(r, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Length).FirstOrDefault();

        if (root != null && full.Length > root.Length)
        {
            var rel = full.Substring(root.Length).Trim(Path.DirectorySeparatorChar);
            return "Home  ›  " + string.Join("  ›  ", rel.Split(Path.DirectorySeparatorChar));
        }
        return root != null ? "Home" : "Home  ›  " + Path.GetFileName(full);
    }

    private void UpdateNavButtons()
    {
        bool can = _mode switch
        {
            LibMode.Videos or LibMode.Music when AnonFolderPage && _anonSubBack.Count > 0 => true,
            LibMode.Home => _navBack.Count > 0 || (_currentDir != null && (_anon ? _anonRootMulti : HomeRoots().Count > 1)),
            LibMode.PlaylistDetail => true,
            LibMode.Music => _musicScope != null,
            _ => false
        };
        LibBackButton.IsEnabled = can;
        LibForwardButton.IsEnabled = _mode == LibMode.Home && _navForward.Count > 0;
        LibBackButton.Opacity = LibBackButton.IsEnabled ? 1 : 0.4;
        LibForwardButton.Opacity = LibForwardButton.IsEnabled ? 1 : 0.4;
    }

    private void LibBack_Click(object sender, RoutedEventArgs e)
    {
        switch (_mode)
        {
            case LibMode.Videos or LibMode.Music when AnonFolderPage && _anonSubBack.Count > 0:
                _anonSubDir = _anonSubBack.Pop();
                RebuildAnonFolderPage();
                return;
            case LibMode.PlaylistDetail: SetMode(LibMode.Playlists); return;
            case LibMode.Music when _musicScope != null:
                _musicScope = null;
                SetMusicTabs(_musicTabBeforeScope);
                BuildMusicEntries();
                FinishPage();
                return;
            case LibMode.Home:
                if (_navBack.Count > 0 && _currentDir != null)
                {
                    _navForward.Push(_currentDir);
                    NavigateTo(_navBack.Pop(), record: false);
                }
                else if (_currentDir != null && (_anon ? _anonRootMulti : HomeRoots().Count > 1))
                {
                    GoRoot();
                }
                return;
        }
    }

    private void LibForward_Click(object sender, RoutedEventArgs e)
    {
        if (_navForward.Count == 0 || _currentDir == null) return;
        _navBack.Push(_currentDir);
        NavigateTo(_navForward.Pop(), record: false);
    }

    // ------------------------------------------------------------------ rows

    private string RowTemplateKey() => _mode switch
    {
        LibMode.Playlists => "RowSquareTpl",
        LibMode.Music when AnonFolderPage => _cfg.LibraryGrid ? "RowGridTpl" : "RowListTpl",   // folders and songs, like the Home page
        LibMode.Music => _musicScope != null || _musicTab == 0 ? "RowSongTpl" : _musicTab == 1 ? "RowSquareTpl" : "RowArtistTpl",
        LibMode.PlaylistDetail => "RowListTpl",
        _ => _cfg.LibraryGrid ? "RowGridTpl" : "RowListTpl"
    };

    private void RebuildRows()
    {
        var text = SearchBox.Text.Trim();
        IEnumerable<BrowserEntry> query = _allEntries;
        if (text.Length > 0)
            query = query.Where(x => x.Name.IndexOf(text, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                                     x.Artist.IndexOf(text, StringComparison.CurrentCultureIgnoreCase) >= 0);

        _visibleEntries = query.ToList();
        _selectedEntry = null;
        SelectionState.Instance.Reset();

        var key = RowTemplateKey();
        bool tiles = key is "RowGridTpl" or "RowSquareTpl" or "RowArtistTpl";
        _tileCols = tiles ? TileColumns(key) : 0;

        LibraryList.MaxWidth = double.PositiveInfinity;   // the number of columns follows the width instead of a fixed cap
        LibraryList.ItemTemplate = (DataTemplate)FindResource(key);
        LibraryList.ItemsSource = BuildRows(tiles ? _tileCols : 0);

        CountText.Text = _mode == LibMode.Music && _musicScope == null && _musicTab == 1 ? ItemWord(_visibleEntries.Count, "album")
                       : _mode == LibMode.Music && _musicScope == null && _musicTab == 2 ? ItemWord(_visibleEntries.Count, "artist")
                       : ItemCount(_visibleEntries.Count);
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        UpdateEmptyState();
        UpdateSelectionBar();
    }
}
