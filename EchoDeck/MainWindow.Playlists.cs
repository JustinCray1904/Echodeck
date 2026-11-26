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

        PlaylistStore.Create(name);
        if (_libraryVisible && _mode == LibMode.Playlists) RefreshPage();
        RefreshSide();
        ShowOsd("Playlist created");
    }

    private void AddToPlaylist(Playlist pl, IEnumerable<string> paths)
    {
        int added = 0;
        foreach (var p in paths)
        {
            if (!IsSupportedPath(p)) continue;
            if (pl.Items.Any(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase))) continue;
            pl.Items.Add(p);
            added++;
        }
        if (added > 0) PlaylistStore.Save();
        RefreshPage();
        RefreshSide();
        ShowOsd(added > 0 ? $"Added to “{pl.Name}”" : "Already in the playlist");
    }

    private void ShowPlaylistPicker(IReadOnlyList<string> paths, UIElement? anchor)
    {
        var menu = new ContextMenu();
        if (anchor != null) { menu.PlacementTarget = anchor; menu.Placement = PlacementMode.Bottom; }
        else menu.Placement = PlacementMode.MousePoint;

        var addTo = AddToMenu(paths);
        foreach (var item in addTo.Items.Cast<object>().ToList())
        {
            addTo.Items.Remove(item);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void PlayPlaylist(Playlist pl, bool shuffle)
    {
        var files = pl.Items.Where(PathExists).ToList();
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

    private void PlAddTo_Click(object sender, RoutedEventArgs e)
    {
        if (PlaylistFor(sender) is not { } pl) return;
        if (_anon)
        {
            Notify("Use the ··· menu on an item and choose Add to");
            return;
        }
        var dlg = new OpenFileDialog { Title = "Add to playlist", Filter = MediaTypes.DialogFilter, Multiselect = true };
        if (dlg.ShowDialog(this) == true) AddToPlaylist(pl, dlg.FileNames);
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
        if (_anon)
        {
            Notify("Cover pictures aren't available in Anonymous Mode");
            return;
        }

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
            TempoDialog.Show(this, "Couldn't set the cover: " + ex.Message, AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DeletePlaylist(Playlist pl)
    {
        var answer = TempoDialog.Show(this, "Delete the playlist “" + pl.Name + "”? The files themselves are not touched.",
            "Delete playlist", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        PlaylistStore.Delete(pl);
        if (_sidePlaylist == pl) _sidePlaylist = null;
        if (_openPlaylist == pl) _openPlaylist = null;
        if (_mode == LibMode.PlaylistDetail) SetMode(LibMode.Playlists);
        else RefreshPage();
        RefreshSide();
    }
}
