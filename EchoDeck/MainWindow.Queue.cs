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
    // ------------------------------------------------------------------ queue helpers

    private void Enqueue(string path, bool next)
    {
        if (!IsSupportedPath(path)) return;

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
        if (_anon)
        {
            foreach (var item in VirtualFiles(folder)) Enqueue(item.FullPath, next: false);
            return;
        }

        try
        {
            foreach (var f in Directory.EnumerateFiles(folder).Where(f => MediaTypes.IsSupportedExtension(Path.GetExtension(f)))
                         .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase))
                Enqueue(f, next: false);
        }
        catch { /* unreadable folder */ }
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

        string? playingPath = _hasMedia && _queueIndex >= 0 && _queueIndex < _queue.Count ? _queue[_queueIndex].FullPath : null;
        var items = new List<BrowserEntry>();
        string emptyText, emptyButton;

        BrowserEntry Row(string path)
        {
            var entry = EntryForPath(path);
            entry.IsPlaying = playingPath != null && string.Equals(path, playingPath, StringComparison.OrdinalIgnoreCase);
            entry.IsAnimating = entry.IsPlaying && _playingNow;   // the bars stand still while paused
            return entry;
        }

        if (_sideMode == SideMode.Queue)
        {
            foreach (var q in _queue) items.Add(Row(q.FullPath));
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
            foreach (var path in _sidePlaylist.Items.Where(PathExists)) items.Add(Row(path));
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
        SideWatermark.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;   // the big faint mark only while there is nothing to list
        SideEmptyHint.Text = text.Length > 0 ? "No matches." : emptyText;
        SideEmptyButton.Content = emptyButton;
        SideEmptyButton.Visibility = text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SideAdd_Click(object sender, RoutedEventArgs e)
    {
        if (_sideMode == SideMode.Queue) { AddFiles_Click(sender, e); return; }
        if (_sidePlaylist == null) { NewPlaylist_Click(sender, e); return; }
        if (_anon) { Notify("Use the ··· menu on an item and choose Add to"); return; }

        var dlg = new OpenFileDialog { Title = "Add to playlist", Filter = MediaTypes.DialogFilter, Multiselect = true };
        if (dlg.ShowDialog(this) == true) AddToPlaylist(_sidePlaylist, dlg.FileNames);
    }

    private void SidePlBack_Click(object sender, RoutedEventArgs e)
    {
        _sidePlaylist = null;
        RefreshSide();
    }

    // ------------------------------------------------------------------ queue: drop files in, drag items to reorder

    private const string QueueDragFormat = "TempoQueuePath";
    private Point _sideDragStart;
    private BrowserEntry? _sideDragEntry;

    /// <summary>The list item that contains <paramref name="d"/> (the element can be a Run or other non-visual: those go up the logical tree).</summary>
    private static ListBoxItem? ListItemOf(DependencyObject? d)
    {
        while (d != null && d is not ListBoxItem)
            d = d is System.Windows.Media.Visual || d is System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as ListBoxItem;
    }

    private void SideList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _sideDragStart = e.GetPosition(null);
        _sideDragEntry = _sideMode == SideMode.Queue ? ListItemOf(e.OriginalSource as DependencyObject)?.DataContext as BrowserEntry : null;
    }

    private void SideList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _sideDragEntry == null || _sideMode != SideMode.Queue) return;
        if (SideSearch.Text.Trim().Length > 0) return;   // a filtered list cannot be reordered

        var moved = e.GetPosition(null) - _sideDragStart;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var entry = _sideDragEntry;
        _sideDragEntry = null;
        DragDrop.DoDragDrop(SideList, new DataObject(QueueDragFormat, entry.FullPath), DragDropEffects.Move);
    }

    private void SideList_DragOver(object sender, DragEventArgs e)
    {
        if (_sideMode != SideMode.Queue) return;   // the playlists panel: the window's own handling

        if (e.Data.GetDataPresent(QueueDragFormat)) { e.Effects = DragDropEffects.Move; e.Handled = true; }
        else if (!_anon && e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Copy; e.Handled = true; }
        // Anonymous Mode: dropped files are imported by the window's handler
    }

    /// <summary>Where in the queue a drop at the mouse position goes (the queue's length = the end).</summary>
    private int QueueDropIndex(DragEventArgs e)
    {
        var hit = System.Windows.Media.VisualTreeHelper.HitTest(SideList, e.GetPosition(SideList));
        var item = ListItemOf(hit?.VisualHit);
        if (item == null) return _queue.Count;

        int index = SideList.ItemContainerGenerator.IndexFromContainer(item);
        if (index < 0) return _queue.Count;
        if (e.GetPosition(item).Y > item.ActualHeight / 2) index++;   // the lower half of a row: after it
        return Math.Min(index, _queue.Count);
    }

    private void SideList_Drop(object sender, DragEventArgs e)
    {
        if (_sideMode != SideMode.Queue) return;
        int target = SideSearch.Text.Trim().Length > 0 ? _queue.Count : QueueDropIndex(e);

        if (e.Data.GetData(QueueDragFormat) is string moving)
        {
            MoveQueueItem(moving, target);
            e.Handled = true;
            return;
        }

        if (_anon || e.Data.GetData(DataFormats.FileDrop) is not string[] items) return;   // Anonymous Mode: the window imports them

        var files = new List<string>();
        foreach (var dropped in items)
        {
            try
            {
                if (Directory.Exists(dropped))
                    files.AddRange(Directory.EnumerateFiles(dropped).Where(IsSupportedPath).OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase));
                else if (IsSupportedPath(dropped)) files.Add(dropped);
            }
            catch { /* an unreadable folder */ }
        }

        int added = 0;
        foreach (var f in files)
        {
            if (_queue.Any(q => string.Equals(q.FullPath, f, StringComparison.OrdinalIgnoreCase))) continue;
            _queue.Insert(Math.Min(target + added, _queue.Count), new QueueItem(f));
            added++;
        }
        if (target <= _queueIndex && added > 0) _queueIndex += added;   // the song that is playing keeps its place

        e.Handled = true;
        RefreshSide();
        UpdateTransportEnabled();
        if (added > 0) ShowOsd(added == 1 ? "Added to queue" : added + " added to queue");
    }

    private void MoveQueueItem(string path, int target)
    {
        int from = _queue.FindIndex(q => string.Equals(q.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (from < 0) return;

        var playing = _queueIndex >= 0 && _queueIndex < _queue.Count ? _queue[_queueIndex] : null;
        var item = _queue[from];
        _queue.RemoveAt(from);
        if (target > from) target--;   // the list got shorter above the target
        _queue.Insert(Math.Clamp(target, 0, _queue.Count), item);
        if (playing != null) _queueIndex = _queue.IndexOf(playing);   // the same song keeps playing

        RefreshSide();
        UpdateTransportEnabled();
    }

    private void SideList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SideList.SelectedItem is not BrowserEntry entry) return;

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
            foreach (var p in _sidePlaylist.Items.Where(PathExists)) _queue.Add(new QueueItem(p));
        }
        PlayNormal(entry.FullPath);
        RootGrid.Focus();
    }

    private void SideItem_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (EntryOf(sender) is not { } entry) return;
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
            menu.Items.Add(AddToMenu(new[] { path }));
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

    /// <summary>Q / P: show the queue or the playlists panel (press again to hide it). Only the shortcuts and the control-bar button open it.</summary>
    private void ShowSide(SideMode mode)
    {
        if (PanelsBusy) return;
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
        if (PanelsBusy)
        {
            if (sender is ToggleButton busyToggle) busyToggle.IsChecked = _sidebarVisible;
            return;
        }
        if (_libraryVisible) ShowLibrary(false);
        if (on) RefreshSide();
        SetSidebarVisible(on);
    }

    private void SetSidebarVisible(bool visible)
    {
        _sidebarVisible = visible;
        ControlsPlaylistToggle.IsChecked = visible;
        ApplySidebar();
    }

    // ------------------------------------------------------------------ queue navigation

    private void PlayAdjacent(int delta)
    {
        int i = QueueNavigator.Adjacent(_queue.Count, _queueIndex, delta, _cfg.Shuffle, _rng);
        if (i >= 0) PlayNormal(_queue[i].FullPath, stayInLibrary: _libraryVisible);
    }

    private void OnEndReached()
    {
        // watched to the end: nothing to continue next time
        if (_resumePath != null) { ResumeStore.Remove(_resumePath); _resumePath = null; }
        HideResumePrompt(animate: false);
        ResetTransport();
        _playbackEnded = true;   // cleared again as soon as something starts playing
        if (SleepStopsAfterSong()) return;   // the sleep timer was set to "end of this song"

        // what follows (the same song again, a random one, the next one, the first one again, or nothing) is decided by QueueNavigator
        int next = QueueNavigator.AfterEnd(_queue.Count, _queueIndex, _cfg.Loop, _cfg.Shuffle, _cfg.AutoAdvance, _rng);
        if (next >= 0) PlayNormal(_queue[next].FullPath, stayInLibrary: _libraryVisible);
    }
}
