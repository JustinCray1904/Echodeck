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
        _anonSubDir = "";
        _anonSubBack.Clear();
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

    // ------------------------------------------------------------------ Videos / Music pages grouped by folder
    // In Anonymous Mode the Videos page and the Songs list of the Music page show the folders the items came from (like the Home page),
    // not one long list of everything. A folder opens in place; Back returns. Only items of the page's kind are shown (videos or songs).

    private string _anonSubDir = "";                           // the folder open inside the page ("" = the list of folders)
    private readonly Stack<string> _anonSubBack = new();

    private bool AnonFolderPage => _anon && (_mode == LibMode.Videos || (_mode == LibMode.Music && _musicScope == null && _musicTab == 0));

    private bool HasKind(string dir, bool audio, int depth = 0)
    {
        if (depth > 40) return false;
        if (_vFiles.TryGetValue(dir, out var files) && files.Any(i => i.IsAudio == audio)) return true;
        if (_vKids.TryGetValue(dir, out var kids))
            foreach (var kid in kids)
                if (HasKind(kid, audio, depth + 1)) return true;
        return false;
    }

    private List<BrowserEntry> AnonFolderEntries(bool audio)
    {
        // a folder that no longer holds anything of this kind (everything was restored): back to the list of folders
        if (_anonSubDir.Length > 0 && !HasKind(_anonSubDir, audio)) { _anonSubDir = ""; _anonSubBack.Clear(); }

        for (int guard = 0; guard < 40; guard++)
        {
            var list = new List<BrowserEntry>();
            var kids = KidsOf(_anonSubDir).Where(k => HasKind(k, audio)).ToList();
            foreach (var kid in kids) list.Add(VirtualFolderEntry(kid));

            var files = VirtualFiles(_anonSubDir).Where(i => i.IsAudio == audio)
                .OrderBy(DisplayTitle, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (_sortDesc) files.Reverse();

            // at the top, a single folder with nothing beside it is opened for you (as on the Home page)
            if (_anonSubDir.Length == 0 && kids.Count == 1 && files.Count == 0)
            {
                _anonSubDir = list[0].FullPath;
                continue;
            }

            foreach (var f in files) list.Add(VaultEntry(f, f.SizeText));
            return list;
        }
        return new List<BrowserEntry>();
    }

    private void RebuildAnonFolderPage()
    {
        _pageToken++;
        if (_mode == LibMode.Videos) BuildVideos();
        else { BuildMusicEntries(); FinishPage(); }
    }

    /// <summary>A folder tile was opened: inside the Videos / Music pages (Anonymous Mode) it opens in place, elsewhere it is browsed.</summary>
    private void OpenFolderEntry(string dir)
    {
        if (AnonFolderPage)
        {
            _anonSubBack.Push(_anonSubDir);
            _anonSubDir = dir;
            RebuildAnonFolderPage();
            return;
        }
        NavigateTo(dir);
    }

    /// <summary>Every vault item inside a virtual folder (and its sub-folders); on the Videos / Music pages only that page's kind.</summary>
    private List<VaultItem> VaultItemsUnder(string dir)
    {
        bool filter = AnonFolderPage;
        bool audio = _mode == LibMode.Music;
        return _anonList.Where(i =>
        {
            if (filter && i.IsAudio != audio) return false;
            string key = VKey(i.Origin);
            return key.Equals(dir, StringComparison.OrdinalIgnoreCase) || key.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase);
        }).ToList();
    }

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
}
