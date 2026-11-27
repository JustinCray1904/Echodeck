using System.Windows;

namespace EchoDeck;

public partial class MainWindow
{
    // ------------------------------------------------------------------ delete (selection bar)

    private bool _deleteBusy;

    /// <summary>
    /// The Delete button of the selection bar, for the ticked items (one, several, or all with the tick-box at the left of the bar).
    /// Anonymous Mode: permanent and secure, as the right-click "Delete permanently" already was. Otherwise the files go to the Recycle Bin.
    /// </summary>
    private async void SelDelete_Click(object sender, RoutedEventArgs e)
    {
        var ticked = CheckedFiles().ToList();
        if (ticked.Count == 0)
        {
            Notify("Tick the items you want to delete first");
            return;
        }

        try
        {
            if (_anon) await DeleteEntriesAsync(ItemsOf(ticked));
            else await DeleteFilesToRecycleBinAsync(ticked);
        }
        catch (Exception ex)
        {
            AppLog.Write("Delete", ex, "deleting the ticked items failed");
            Notify("Something went wrong while deleting.");
        }
    }

    private bool IsOpenInPlayer(string path) =>
        _hasMedia && (string.Equals(path, CurrentSongPath(), StringComparison.OrdinalIgnoreCase)
                      || string.Equals(path, _modFor, StringComparison.OrdinalIgnoreCase));

    private async Task DeleteFilesToRecycleBinAsync(List<BrowserEntry> entries)
    {
        if (_deleteBusy) return;

        // the file that is playing is open, so Windows would refuse to move it: it is left out and said so
        var paths = entries.Select(x => x.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        int playing = paths.RemoveAll(IsOpenInPlayer);
        if (paths.Count == 0)
        {
            Notify("That file is playing right now. Play something else first, then delete it.");
            return;
        }

        string what = paths.Count == 1
            ? "“" + DisplayNames.Clean(Path.GetFileNameWithoutExtension(paths[0])) + "”"
            : paths.Count.ToString("N0") + " files";
        MessageBoxResult answer;
        _modalOpen = true;
        try
        {
            answer = TempoDialog.Show(this,
                $"Move {what} to the Recycle Bin?\n\nYou can restore {(paths.Count == 1 ? "it" : "them")} from there."
                + (playing > 0 ? "\n\n(The file that is playing is not included.)" : ""),
                "Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        }
        finally { _modalOpen = false; }
        if (answer != MessageBoxResult.Yes) return;

        _deleteBusy = true;
        try
        {
            var moved = await Task.Run(() => paths.Where(p => !File.Exists(p) || DuplicatesWindow.ToRecycleBin(p)).ToList());

            // forget the files that are gone: the queue, Recent, the library scan and the page that is open
            var gone = new HashSet<string>(moved, StringComparer.OrdinalIgnoreCase);
            foreach (var path in moved)
            {
                RemoveFromQueue(path);
                RecentStore.Remove(path);
            }
            LibraryScanner.Invalidate();
            _allEntries.RemoveAll(x => gone.Contains(x.FullPath));
            RebuildRows();

            int failed = paths.Count - moved.Count;
            Notify(failed == 0
                ? (moved.Count == 1 ? "Moved to the Recycle Bin" : $"Moved {moved.Count:N0} files to the Recycle Bin")
                : $"Moved {moved.Count:N0}; {failed:N0} couldn't be moved (in use, or no permission).");
        }
        finally { _deleteBusy = false; }
    }
}
