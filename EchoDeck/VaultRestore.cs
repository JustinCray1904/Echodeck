using System.Windows;
using Microsoft.Win32;

namespace EchoDeck;

/// <summary>"Restore": take a file out of Anonymous Mode again, back to where it came from (or anywhere you choose).</summary>
internal static class VaultRestore
{
    /// <returns>A status message for the UI; empty when the user cancelled.</returns>
    public static async Task<string> RunAsync(Window owner, VaultService service, VaultItem item,
        Func<VaultItem, Task<string?>> delete)
    {
        var dlg = new OpenFolderDialog { Title = "Restore “" + item.Name + "” to…" };
        dlg.InitialDirectory = !string.IsNullOrEmpty(item.Origin) && Directory.Exists(item.Origin)
            ? item.Origin
            : Environment.GetFolderPath(item.IsAudio ? Environment.SpecialFolder.MyMusic : Environment.SpecialFolder.MyVideos);
        if (dlg.ShowDialog(owner) != true) return "";

        var answer = MessageBox.Show(owner,
            "Remove “" + item.Name + "” from Anonymous Mode after restoring it?\n\n" +
            "Yes  –  move it back (the encrypted copy is deleted)\nNo  –  copy it out and keep it here",
            "Restore", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return "";

        string saved = await service.RestoreAsync(item, dlg.FolderName);
        if (answer != MessageBoxResult.Yes) return "Copied to " + saved;

        var error = await delete(item);
        return error == null ? "Moved back to " + saved : "Restored to " + saved + ", but couldn't remove it: " + error;
    }

    /// <summary>Restores several items at once (to their original folders, or one folder you pick).</summary>
    public static async Task<string> RunManyAsync(Window owner, VaultService service, IReadOnlyList<VaultItem> items,
        Func<VaultItem, Task<string?>> delete, IProgress<double>? fraction = null)
    {
        if (items.Count == 0) return "";
        if (items.Count == 1) return await RunAsync(owner, service, items[0], delete);

        var where = MessageBox.Show(owner,
            $"Restore {items.Count} items?\n\nYes  –  put each one back in the folder it came from\n" +
            "No  –  choose one folder for all of them\nCancel  –  stop",
            "Restore", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (where == MessageBoxResult.Cancel) return "";

        string? common = null;
        if (where == MessageBoxResult.No)
        {
            var dlg = new OpenFolderDialog { Title = "Restore " + items.Count + " items to…" };
            if (dlg.ShowDialog(owner) != true) return "";
            common = dlg.FolderName;
        }

        var remove = MessageBox.Show(owner,
            "Remove them from Anonymous Mode after restoring?\n\n" +
            "Yes  –  move them back (the encrypted copies are deleted)\nNo  –  copy them out and keep them here",
            "Restore", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (remove == MessageBoxResult.Cancel) return "";

        int done = 0, failed = 0;
        string? lastError = null;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var dest = common ?? (!string.IsNullOrWhiteSpace(item.Origin)
                ? item.Origin!
                : Environment.GetFolderPath(item.IsAudio ? Environment.SpecialFolder.MyMusic : Environment.SpecialFolder.MyVideos));
            try
            {
                await service.RestoreAsync(item, dest);
                if (remove == MessageBoxResult.Yes)
                {
                    var error = await delete(item);
                    if (error != null) { failed++; lastError = item.Name + ": " + error; continue; }
                }
                done++;
            }
            catch (Exception ex)
            {
                failed++;
                lastError = item.Name + ": " + ex.Message;
            }
            fraction?.Report((i + 1.0) / items.Count);
        }

        return failed == 0 ? $"Restored {done} items" : $"Restored {done}, {failed} failed. {lastError}";
    }
}
